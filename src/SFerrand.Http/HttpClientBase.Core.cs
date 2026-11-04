using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

// The sending engine: one logical call = (retries of (an attempt = a request and the redirects it leads to)).
public abstract partial class HttpClientBase
{
    /// <summary>
    /// Sends one logical call. The <paramref name="read"/> delegate turns the response of the final request into a result; it runs under the timeout of the
    /// attempt, so a stalled body cannot hang the call.
    /// </summary>
    private async Task<HttpResult<TResult>> SendCoreAsync<TResult>(
        HttpMethod method,
        string resourceUri,
        string? id,
        IDictionary<string, string>? parameters,
        RequestBody? body,
        IDictionary<string, string>? headers,
        string? baseUri,
        Func<HttpResponseMessage, CancellationToken, Task<HttpResult<TResult>>> read,
        CancellationToken cancellationToken)
    {
        var uri = RequestUri.Build(baseUri ?? _options.BaseUri, resourceUri, id, parameters);
        EnsureTrusted(uri);

        var loggedUri = Describe(uri);
        var retry = _options.Retry;

        // One key per logical call, reused by every attempt: that is what lets the server recognize a replay. A key supplied by the caller counts too.
        var idempotencyKey = retry.ResolveIdempotencyKey(method, headers, out var hasIdempotencyKey);
        // A non-idempotent method without a key gets a single attempt: retrying it could repeat a side effect.
        var maxAttempts = retry.IsEligible(method, hasIdempotencyKey) ? Math.Max(retry.MaxRetries, 0) + 1 : 1;

        using var totalTimeout = CreateTotalTimeout(cancellationToken);
        var callToken = totalTimeout?.Token ?? cancellationToken;

        using var activity = HttpClientTelemetry.StartCall(method, uri, loggedUri);
        var callStarted = Stopwatch.GetTimestamp();
        int? finalStatus = null;
        string? errorType = null;
        var attempts = 0;

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                attempts = attempt;
                TimeSpan delay;
                var attemptStarted = Stopwatch.StartNew();
                try
                {
                    // The timeout applies to one attempt (headers + body read), not to the whole call. It is a linked source,
                    // so the caller's token still cancels immediately, and we can tell the cases apart in the catch below.
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
                    if (_options.RequestTimeout > TimeSpan.Zero && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
                        attemptCts.CancelAfter(_options.RequestTimeout);

                    var client = _httpClientFactory.CreateClient(_options.HttpClientName ?? string.Empty);
                    var (response, request) = await SendFollowingRedirectsAsync(client, method, uri, body, headers, idempotencyKey, attemptCts.Token).ConfigureAwait(false);
                    using var disposeRequest = request;
                    using var disposeResponse = response;

                    var result = await read(response, attemptCts.Token).ConfigureAwait(false);
                    finalStatus = (int)result.StatusCode;

                    if (result.IsSuccess)
                    {
                        Log.Succeeded(_logger, method, loggedUri, finalStatus.Value, attemptStarted.ElapsedMilliseconds);
                        return result;
                    }

                    errorType = finalStatus.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    // null means "do not retry": attempts exhausted, status not retryable, or Retry-After longer than MaxDelay.
                    var nextDelay = attempt < maxAttempts && retry.RetryStatusCodes.Contains(result.StatusCode)
                        ? retry.GetDelay(attempt, response.Headers.RetryAfter)
                        : null;

                    if (nextDelay is null)
                    {
                        Log.Failed(_logger, method, loggedUri, finalStatus.Value, result.ReasonPhrase, attemptStarted.ElapsedMilliseconds);
                        return result;
                    }

                    delay = nextDelay.Value;
                    Log.RetryAfterStatus(_logger, method, loggedUri, finalStatus.Value, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Not cancelled by the caller (that case is not caught and propagates as is): a timeout.
                    if (totalTimeout is { IsCancellationRequested: true })
                        throw new TimeoutException($"HTTP {method} {loggedUri} exceeded the total timeout of {_options.TotalTimeout!.Value.TotalSeconds:0.##} s (attempts: {attempt}).", ex);

                    if (attempt >= maxAttempts)
                        throw new TimeoutException($"HTTP {method} {loggedUri} timed out after {_options.RequestTimeout.TotalSeconds:0.##} s (attempts: {attempt}).", ex);

                    delay = retry.GetDelay(attempt)!.Value;
                    Log.RetryAfterTimeout(_logger, method, loggedUri, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    delay = retry.GetDelay(attempt)!.Value;
                    Log.RetryAfterException(_logger, ex, method, loggedUri, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
                }
                catch (IOException ex) when (ex is not DownloadInterruptedException)
                {
                    // The body is read after the headers, so a connection dropped in the middle of it surfaces here (HttpIOException), no longer wrapped in an
                    // HttpRequestException by the framework. It is the same transient failure as before, and gets the same treatment.
                    if (attempt >= maxAttempts)
                        throw new HttpRequestException("The connection was lost while the response was being read.", ex);

                    delay = retry.GetDelay(attempt)!.Value;
                    Log.RetryAfterException(_logger, ex, method, loggedUri, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
                }

                // Reached only when a retry was decided above; every other path returned or threw.
                HttpClientTelemetry.Retries.Add(1, HttpClientTelemetry.Tags(method, uri, finalStatus, errorType));
                try
                {
                    await Task.Delay(delay, callToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && totalTimeout is { IsCancellationRequested: true })
                {
                    throw new TimeoutException($"HTTP {method} {loggedUri} exceeded the total timeout of {_options.TotalTimeout!.Value.TotalSeconds:0.##} s while waiting to retry (attempts: {attempt}).", ex);
                }
            }
        }
        catch (Exception ex)
        {
            errorType = ex is TimeoutException ? "timeout" : ex.GetType().FullName;
            throw;
        }
        finally
        {
            HttpClientTelemetry.CallDuration.Record(Stopwatch.GetElapsedTime(callStarted).TotalSeconds, HttpClientTelemetry.Tags(method, uri, finalStatus, errorType));
            if (activity is not null)
            {
                if (finalStatus is { } status)
                    activity.SetTag("http.response.status_code", status);
                if (attempts > 1)
                    activity.SetTag("http.request.resend_count", attempts - 1);
                if (errorType is not null)
                {
                    activity.SetTag("error.type", errorType);
                    activity.SetStatus(ActivityStatusCode.Error);
                }
            }
        }
    }

    /// <summary>The overall deadline of a call, retries and waits included; <see langword="null"/> when none is configured.</summary>
    private CancellationTokenSource? CreateTotalTimeout(CancellationToken cancellationToken)
    {
        // Timeout.InfiniteTimeSpan is -1 ms, so "no deadline" is covered by the same test; zero is refused by the options validation.
        if (_options.TotalTimeout is not { } total || total <= TimeSpan.Zero)
            return null;

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(total);
        return source;
    }

    /// <summary>The address of a request without its query string: safe to log, to put in a message or in a span.</summary>
    private static string Describe(Uri uri) => uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    #region Redirects

    /// <summary>
    /// Sends a request and follows the redirects it leads to, up to <see cref="HttpClientOptions.MaxRedirects"/>. Each hop is checked against the same rules as a
    /// call (trusted origin, no downgrade to clear text), and credentials stop at the first origin change: the token, the default headers and the
    /// per-call headers are sent to the origin the call started with, and to no other.
    /// </summary>
    /// <remarks>
    /// Only effective when the handler does not follow redirects itself (<c>AllowAutoRedirect = false</c>, the default of <see cref="SimpleHttpClientFactory"/>): a handler
    /// that follows them hides the 3xx, and the redirect then happens before this code can apply its rules.
    /// </remarks>
    private async Task<(HttpResponseMessage Response, HttpRequestMessage Request)> SendFollowingRedirectsAsync(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        RequestBody? body,
        IDictionary<string, string>? headers,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var startOrigin = RequestUri.Origin(uri);
        var currentUri = uri;
        var currentMethod = method;
        var currentBody = body;
        var withCredentials = true;

        for (var hop = 0; ; hop++)
        {
            var request = await CreateRequestAsync(currentMethod, currentUri, currentBody, headers, idempotencyKey, withCredentials, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                request.Dispose();
                throw;
            }

            try
            {
                // The HTTP stack may have followed redirects behind our back. A response from an origin nobody declared is not the API's answer.
                if (response.RequestMessage?.RequestUri is { } finalUri && !_trustedOrigins.Contains(RequestUri.Origin(finalUri)))
                {
                    throw new InvalidOperationException(
                        $"The request was redirected to an origin that is not trusted ({RequestUri.Describe(finalUri.AbsoluteUri)}). Declare it in {nameof(HttpClientOptions.TrustedOrigins)} or disable automatic redirects.");
                }

                if (hop >= _options.MaxRedirects || !TryGetRedirect(response, currentUri, out var target))
                    return (response, request);

                var crossOrigin = RequestUri.Origin(target) != RequestUri.Origin(currentUri);
                if (!TryChangeForRedirect(response.StatusCode, currentMethod, currentBody, crossOrigin, out var nextMethod, out var nextBody))
                    return (response, request);

                ValidateRedirectTarget(currentUri, target);

                Log.Redirected(_logger, currentMethod, Describe(currentUri), (int)response.StatusCode, Describe(target));
                HttpClientTelemetry.Redirects.Add(1, HttpClientTelemetry.Tags(currentMethod, currentUri, (int)response.StatusCode, null));

                withCredentials = withCredentials && RequestUri.Origin(target) == startOrigin;
                currentUri = target;
                currentMethod = nextMethod;
                currentBody = nextBody;
            }
            catch
            {
                response.Dispose();
                request.Dispose();
                throw;
            }

            response.Dispose();
            request.Dispose();
        }
    }

    private static bool TryGetRedirect(HttpResponseMessage response, Uri current, out Uri target)
    {
        target = current;
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || response.Headers.Location is not { } location)
        {
            return false;
        }

        // A relative Location is relative to the URL that was requested (RFC 9110); the fragment is never sent.
        var absolute = location.IsAbsoluteUri ? location : new Uri(current, location);
        target = new Uri(absolute.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped), UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// What the next request looks like. 303 becomes a GET; 301 and 302 turn a POST into a GET, like browsers and the framework; 307 and 308 keep method and body,
    /// except that a body is never re-sent to another origin (the redirect is then returned as the result instead of followed).
    /// </summary>
    private static bool TryChangeForRedirect(HttpStatusCode status, HttpMethod method, RequestBody? body, bool crossOrigin, out HttpMethod nextMethod, out RequestBody? nextBody)
    {
        nextMethod = method;
        nextBody = body;

        switch (status)
        {
            case HttpStatusCode.SeeOther:
                if (method != HttpMethod.Head)
                    nextMethod = HttpMethod.Get;
                nextBody = null;
                return true;

            case HttpStatusCode.MovedPermanently or HttpStatusCode.Found:
                if (method == HttpMethod.Post)
                {
                    nextMethod = HttpMethod.Get;
                    nextBody = null;
                }

                return true;

            default: // 307, 308
                return !(crossOrigin && body is not null);
        }
    }

    private void ValidateRedirectTarget(Uri from, Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"A redirect leads to an address that is not http(s) ({RequestUri.Describe(target.AbsoluteUri)}).");

        if (target.UserInfo.Length > 0)
            throw new InvalidOperationException($"A redirect leads to an address that contains credentials ({RequestUri.Describe(target.AbsoluteUri)}).");

        if (!_trustedOrigins.Contains(RequestUri.Origin(target)))
        {
            throw new InvalidOperationException(
                $"A redirect leads to an origin that is not trusted ({RequestUri.Describe(target.AbsoluteUri)}). Declare it in {nameof(HttpClientOptions.TrustedOrigins)} if it is legitimate.");
        }

        if (from.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
            throw new InvalidOperationException($"A redirect downgrades the connection to clear text ({RequestUri.Describe(target.AbsoluteUri)}).");
    }

    #endregion

    #region Requests and headers

    /// <summary>
    /// Builds a fresh request: a message cannot be sent twice, so every attempt (and every hop) gets its own.
    /// Headers are applied in increasing priority: <c>User-Agent</c>, <c>Accept</c>, authorization, option headers, idempotency key, per-call headers.
    /// Without <paramref name="withCredentials"/> only the first two are set.
    /// </summary>
    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        Uri uri,
        RequestBody? body,
        IDictionary<string, string>? headers,
        string? idempotencyKey,
        bool withCredentials,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        try
        {
            if (_options.HttpVersion is { } version)
            {
                request.Version = version;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }

            if (body is not null)
            {
                request.Content = new ByteArrayContent(body.Bytes);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
            }

            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (_options.Accept is { } accept)
                request.Headers.TryAddWithoutValidation("Accept", accept);

            if (!withCredentials)
                return request;

            var token = _options.AsyncTokenFactory is { } asyncFactory
                ? await asyncFactory(cancellationToken).ConfigureAwait(false)
                : _options.TokenFactory?.Invoke();
            if (!string.IsNullOrEmpty(token))
            {
                ValidateHeaderValue(token, "Authorization");
                request.Headers.Authorization = new AuthenticationHeaderValue(_options.AuthenticationScheme, token);
            }

            foreach (var (name, valueFactory) in _options.Headers)
                SetHeader(request, name, valueFactory());

            if (idempotencyKey is not null)
                SetHeader(request, _options.Retry.IdempotencyKeyHeader!, idempotencyKey);

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                    SetHeader(request, name, value);
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    // Headers that .NET only accepts on HttpContent: adding them to HttpRequestMessage.Headers throws.
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    /// <summary>
    /// Sets a header, replacing any previous value. Content headers (<c>Content-Type</c>...) go on the content;
    /// they are ignored for requests without a body, since there is nothing to describe.
    /// </summary>
    /// <remarks>
    /// Values are added <i>without</i> the framework's validation (so unusual formats work), which means nothing stops a line break from becoming a
    /// second header on the wire. That is checked here, so a header name or value that comes from outside the application cannot forge headers.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is not a valid header name or is managed by the HTTP stack, or the value contains control characters.</exception>
    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        ValidateHeaderName(name);
        ValidateHeaderValue(value, name);

        HttpHeaders? headers = ContentHeaderNames.Contains(name) ? request.Content?.Headers : request.Headers;
        if (headers is null)
            return;

        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }

    internal static void ValidateHeaderName(string name)
    {
        if (HeaderRules.NameError(name) is { } error)
            throw new ArgumentException(error, "headers");
    }

    internal static void ValidateHeaderValue(string value, string headerName)
    {
        if (HeaderRules.ValueError(value, headerName) is { } error)
            throw new ArgumentException(error, "headers");
    }

    /// <summary>
    /// Refuses a request to an origin the application did not declare. Without this, a resource or a <c>baseUri</c> that comes from outside
    /// would send the token and the default headers to an address of the caller's choosing (credential theft and server-side request forgery).
    /// </summary>
    private void EnsureTrusted(Uri uri)
    {
        if (!_trustedOrigins.Contains(RequestUri.Origin(uri)))
        {
            throw new ArgumentException(
                $"The address is on an origin this client is not allowed to call ({RequestUri.Describe(uri.AbsoluteUri)}). Declare it in {nameof(HttpClientOptions.TrustedOrigins)} if it is legitimate.",
                "resourceUri");
        }
    }

    #endregion
}
