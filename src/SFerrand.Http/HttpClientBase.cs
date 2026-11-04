using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

/// <summary>
/// Base class for typed API clients. Derive from it and expose the operations of your API:
/// <code>
/// public sealed class WidgetsClient : HttpClientBase
/// {
///     public WidgetsClient(IOptions&lt;HttpClientOptions&gt; options, IHttpClientFactory factory, ILogger&lt;WidgetsClient&gt; logger)
///         : base(options, factory, logger) { }
///
///     public Task&lt;HttpResult&lt;Widget&gt;&gt; GetAsync(string id, CancellationToken ct = default)
///         =&gt; GetAsync&lt;Widget&gt;("widgets", id, cancellationToken: ct);
/// }
/// </code>
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>HTTP error statuses are returned in an <see cref="HttpResult{TResult}"/>; exceptions are reserved for transport failures,
/// timeouts (<see cref="TimeoutException"/>), cancellation and unreadable JSON.</item>
/// <item>Authentication and headers are set on each request, so the underlying <see cref="HttpClient"/> can be shared safely.</item>
/// <item>Secure by default: requests only go to origins declared in the options, clear-text <c>http</c> is refused (except on the loopback interface),
/// header values are checked for line breaks, path traversal is refused and response bodies are size-limited. See the README, section "Security".</item>
/// <item>Transient failures are retried according to <see cref="HttpClientOptions.Retry"/>; only idempotent methods by default.</item>
/// </list>
/// </remarks>
public abstract class HttpClientBase
{
    private readonly HttpClientOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    // Origins (scheme://host:port) this client may talk to: the base address, the GraphQL endpoint and the declared trusted origins.
    private readonly HashSet<string> _trustedOrigins;

    // Resolved once at construction: explicit option, else the name of the host application, else the library name.
    private readonly string _userAgent;

    /// <summary>Creates a client from its options.</summary>
    /// <param name="options">Client configuration; <see cref="HttpClientOptions.BaseUri"/> must be an absolute http(s) address.</param>
    /// <param name="httpClientFactory">Source of the <see cref="HttpClient"/> instances.</param>
    /// <param name="logger">Logger; <see langword="null"/> disables logging.</param>
    /// <exception cref="ArgumentException">The base address is missing or not an absolute http(s) URI.</exception>
    protected HttpClientBase(IOptions<HttpClientOptions> options, IHttpClientFactory httpClientFactory, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _options = options.Value ?? throw new ArgumentException("The options value is null.", nameof(options));
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger.Instance;
        _userAgent = _options.UserAgent
                     ?? Assembly.GetEntryAssembly()?.GetName().Name
                     ?? "SFerrand.Http";

        ValidateHeaderValue(_userAgent, "User-Agent");
        if (_options.MaxResponseBytes is <= 0)
            throw new ArgumentException($"{nameof(HttpClientOptions.MaxResponseBytes)} must be positive, or null for no limit.", nameof(options));

        _trustedOrigins = new HashSet<string>(StringComparer.Ordinal) { ValidateAddress(_options.BaseUri, nameof(HttpClientOptions.BaseUri)) };
        if (!string.IsNullOrEmpty(_options.GraphQLUri))
            _trustedOrigins.Add(ValidateAddress(_options.GraphQLUri, nameof(HttpClientOptions.GraphQLUri)));
        foreach (var origin in _options.TrustedOrigins)
            _trustedOrigins.Add(ValidateAddress(origin, nameof(HttpClientOptions.TrustedOrigins)));
    }

    /// <summary>
    /// Checks an address given in the options and returns its origin. Refuses what is not an absolute http(s) address, credentials embedded in it,
    /// and clear-text http to a host that is not the loopback interface (unless <see cref="HttpClientOptions.AllowInsecureHttp"/> says otherwise).
    /// Messages never repeat more than scheme, host and port: the value may carry secrets.
    /// </summary>
    private string ValidateAddress(string? value, string optionName)
    {
        if (value is null || !RequestUri.TryParseHttp(value, out var uri))
            throw new ArgumentException($"{optionName} must be an absolute http(s) URI ({RequestUri.Describe(value)}).", "options");

        if (uri.UserInfo.Length > 0)
            throw new ArgumentException($"{optionName} must not contain credentials (user:password@host): use TokenFactory or a header.", "options");

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !_options.AllowInsecureHttp)
            throw new ArgumentException($"{optionName} uses clear-text http ({RequestUri.Describe(value)}): use https, or set {nameof(HttpClientOptions.AllowInsecureHttp)}.", "options");

        return RequestUri.Origin(uri);
    }

    /// <summary>
    /// Creates a client that shares the configuration, authentication and logger of another one,
    /// for example to split a large API surface into several focused clients.
    /// </summary>
    protected HttpClientBase(HttpClientBase other)
    {
        ArgumentNullException.ThrowIfNull(other);

        _options = other._options;
        _httpClientFactory = other._httpClientFactory;
        _logger = other._logger;
        _userAgent = other._userAgent;
        _trustedOrigins = other._trustedOrigins;
    }

    /// <summary>The JSON options used for request bodies and response parsing.</summary>
    protected virtual JsonSerializerOptions JsonSerializerOptions => _options.JsonSerializerOptions;

    #region GET / LIST

    /// <summary>Sends a <c>GET</c> request and deserializes the body into <typeparamref name="TResult"/>.</summary>
    /// <typeparam name="TResult">Type the response body is deserialized into; <see cref="string"/> keeps it raw.</typeparam>
    /// <example>
    /// <code>
    /// // GET https://api.example.com/v2/widgets/42?expand=owner
    /// var result = await GetAsync&lt;Widget&gt;("widgets", "42", new Dictionary&lt;string, string&gt; { ["expand"] = "owner" });
    /// </code>
    /// </example>
    /// <exception cref="TimeoutException">Every attempt exceeded <see cref="HttpClientOptions.RequestTimeout"/>.</exception>
    /// <exception cref="HttpRequestException">The transport failed on every attempt.</exception>
    /// <exception cref="JsonException">A successful response body could not be deserialized.</exception>
    /// <param name="resourceUri">Path of the resource, appended to the base address. An absolute URL is accepted only on a trusted origin; <c>.</c> and <c>..</c> segments, a fragment and embedded credentials are refused.</param>
    /// <param name="id">Optional identifier appended as a path segment. It is URL-encoded, and <c>.</c> or <c>..</c> segments are refused.</param>
    /// <param name="parameters">Optional query string parameters.</param>
    /// <param name="headers">Optional headers for this call only; they replace same-named default headers.</param>
    /// <param name="baseUri">Optional base address for this call only. Its origin must be the one of <see cref="HttpClientOptions.BaseUri"/> or be declared in <see cref="HttpClientOptions.TrustedOrigins"/>.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    protected virtual Task<HttpResult<TResult>> GetAsync<TResult>(
        string resourceUri,
        string? id = null,
        IDictionary<string, string>? parameters = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
        => SendAsync<TResult>(HttpMethod.Get, resourceUri, id, parameters, body: null, headers, baseUri, cancellationToken);

    /// <summary>Sends a <c>GET</c> request and deserializes a JSON array into a sequence of <typeparamref name="TItem"/>.</summary>
    /// <typeparam name="TItem">Type of the elements of the JSON array.</typeparam>
    /// <remarks>Pagination is not followed: loop over pages in your own client, using <c>parameters</c> and the response headers.</remarks>
    /// <inheritdoc cref="GetAsync{TResult}"/>
    protected virtual Task<HttpResult<IEnumerable<TItem>>> ListAsync<TItem>(
        string resourceUri,
        string? id = null,
        IDictionary<string, string>? parameters = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
        => SendAsync<IEnumerable<TItem>>(HttpMethod.Get, resourceUri, id, parameters, body: null, headers, baseUri, cancellationToken);

    #endregion

    #region POST / PUT / PATCH / DELETE

    /// <summary>Sends a <c>POST</c> request with an optional JSON body.</summary>
    /// <typeparam name="TResult">Type the response body is deserialized into; <see cref="string"/> keeps it raw.</typeparam>
    /// <remarks>
    /// <c>POST</c> is not idempotent, so it is <b>not retried</b> unless <see cref="RetryOptions.RetryNonIdempotentMethods"/> is set:
    /// replaying it after a lost response could create the resource twice.
    /// </remarks>
    /// <param name="resourceUri">Path of the resource, appended to the base address. An absolute URL is accepted only on a trusted origin; <c>.</c> and <c>..</c> segments, a fragment and embedded credentials are refused.</param>
    /// <param name="body">Object serialized as JSON; <see langword="null"/> sends no body.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only. Its origin must be the one of <see cref="HttpClientOptions.BaseUri"/> or be declared in <see cref="HttpClientOptions.TrustedOrigins"/>.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    protected virtual Task<HttpResult<TResult>> PostAsync<TResult>(
        string resourceUri,
        object? body = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
        => SendAsync<TResult>(HttpMethod.Post, resourceUri, id: null, parameters: null, body, headers, baseUri, cancellationToken);

    /// <summary>Sends a <c>PUT</c> request with a JSON body.</summary>
    /// <typeparam name="TResult">Type the response body is deserialized into; <see cref="string"/> keeps it raw.</typeparam>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    /// <param name="resourceUri">Path of the resource, appended to the base address. An absolute URL is accepted only on a trusted origin; <c>.</c> and <c>..</c> segments, a fragment and embedded credentials are refused.</param>
    /// <param name="body">Object serialized as JSON.</param>
    /// <param name="id">Optional identifier appended as a path segment. It is URL-encoded, and <c>.</c> or <c>..</c> segments are refused.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only. Its origin must be the one of <see cref="HttpClientOptions.BaseUri"/> or be declared in <see cref="HttpClientOptions.TrustedOrigins"/>.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    protected virtual Task<HttpResult<TResult>> PutAsync<TResult>(
        string resourceUri,
        object body,
        string? id = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync<TResult>(HttpMethod.Put, resourceUri, id, parameters: null, body, headers, baseUri, cancellationToken);
    }

    /// <summary>Sends a <c>PATCH</c> request with a JSON body.</summary>
    /// <remarks>Like <c>POST</c>, <c>PATCH</c> is not retried unless <see cref="RetryOptions.RetryNonIdempotentMethods"/> is set.</remarks>
    /// <inheritdoc cref="PutAsync{TResult}"/>
    protected virtual Task<HttpResult<TResult>> PatchAsync<TResult>(
        string resourceUri,
        object body,
        string? id = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync<TResult>(HttpMethod.Patch, resourceUri, id, parameters: null, body, headers, baseUri, cancellationToken);
    }

    /// <summary>Sends a <c>DELETE</c> request, with an optional JSON body.</summary>
    /// <returns><see langword="true"/> for a 2xx status; the failure is logged otherwise.</returns>
    /// <remarks>
    /// Only the outcome is returned. Call <see cref="SendAsync{TResult}"/> with <see cref="HttpMethod.Delete"/>
    /// when you need the status code or the error body.
    /// </remarks>
    /// <inheritdoc cref="PutAsync{TResult}"/>
    protected virtual async Task<bool> DeleteAsync(
        string resourceUri,
        string? id = null,
        object? body = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<string>(HttpMethod.Delete, resourceUri, id, parameters: null, body, headers, baseUri, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess;
    }

    #endregion

    #region GraphQL

    /// <summary>Sends a GraphQL query as a <c>POST</c> to <see cref="HttpClientOptions.GraphQLUri"/>.</summary>
    /// <remarks>
    /// GraphQL reports most failures with a <c>200 OK</c> and an <c>errors</c> array: inspect your envelope type,
    /// not only <see cref="HttpResult{TResult}.IsSuccess"/>. Queries are not retried unless
    /// <see cref="RetryOptions.RetryNonIdempotentMethods"/> is set, since the verb is <c>POST</c> (mutations are not safe to replay).
    /// </remarks>
    /// <example>
    /// <code>
    /// var query = new GraphQLQuery&lt;object&gt; { Query = "query($id: ID!) { widget(id: $id) { name } }", Variables = new { id = "42" } };
    /// var result = await SendGraphQLAsync&lt;WidgetEnvelope, object&gt;(query);
    /// </code>
    /// </example>
    /// <typeparam name="TResult">Type of the response envelope (usually with <c>data</c> and <c>errors</c>).</typeparam>
    /// <typeparam name="TVariables">Type of the variables object.</typeparam>
    /// <param name="query">The query and its variables.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="graphQLUri">Optional endpoint for this call only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="InvalidOperationException">No GraphQL endpoint is configured.</exception>
    protected virtual Task<HttpResult<TResult>> SendGraphQLAsync<TResult, TVariables>(
        GraphQLQuery<TVariables> query,
        IDictionary<string, string>? headers = null,
        string? graphQLUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var endpoint = graphQLUri ?? _options.GraphQLUri
            ?? throw new InvalidOperationException($"No GraphQL endpoint: set {nameof(HttpClientOptions)}.{nameof(HttpClientOptions.GraphQLUri)} or pass {nameof(graphQLUri)}.");

        return SendAsync<TResult>(HttpMethod.Post, string.Empty, id: null, parameters: null, query, headers, endpoint, cancellationToken);
    }

    #endregion

    /// <summary>
    /// Sends a request with the retry policy, timeout, authentication and headers of this client.
    /// Use it for verbs that have no dedicated method (<c>HEAD</c>, <c>OPTIONS</c>...).
    /// </summary>
    /// <remarks>
    /// <para>Every other method of this class is a thin wrapper around this one. For each call it:</para>
    /// <list type="number">
    /// <item>builds the URI (see the README, "URL building") and serializes the body <b>once</b>, since a body is replayed on retries;</item>
    /// <item>builds a <b>fresh</b> <see cref="HttpRequestMessage"/> per attempt (a message cannot be sent twice), evaluating the token and header factories again;</item>
    /// <item>sends it with a per-attempt timeout; a failing status listed in <see cref="RetryOptions.RetryStatusCodes"/>, a
    /// transport failure or a timeout leads to another attempt after a backoff delay (see <see cref="RetryOptions"/>);</item>
    /// <item>returns the last <see cref="HttpResult{TResult}"/> when attempts run out or the status is not retryable.</item>
    /// </list>
    /// <para>Override it to add cross-cutting behaviour (correlation headers, metrics, circuit breaking...) around every call.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // HEAD: is the widget there?
    /// var head = await SendAsync&lt;string&gt;(HttpMethod.Head, "widgets", "42", null, null, null, null, ct);
    /// bool exists = head.StatusCode == HttpStatusCode.OK;
    /// </code>
    /// </example>
    /// <typeparam name="TResult">Type of the response body; <see cref="string"/> keeps it raw.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="resourceUri">Path of the resource, appended to the base address. An absolute URL is accepted only on a trusted origin; <c>.</c> and <c>..</c> segments, a fragment and embedded credentials are refused.</param>
    /// <param name="id">Optional identifier appended as a path segment. It is URL-encoded, and <c>.</c> or <c>..</c> segments are refused.</param>
    /// <param name="parameters">Optional query string parameters.</param>
    /// <param name="body">Object serialized as JSON; <see langword="null"/> sends no body.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only. Its origin must be the one of <see cref="HttpClientOptions.BaseUri"/> or be declared in <see cref="HttpClientOptions.TrustedOrigins"/>.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    /// <exception cref="ArgumentException">
    /// The address is on an origin that is not trusted, uses clear-text <c>http</c>, carries credentials, a fragment or <c>.</c>/<c>..</c> segments; or a header
    /// has an invalid name, is managed by the HTTP stack, or has a value (or a token) containing a control character. Nothing is sent, and the message never repeats a secret.
    /// </exception>
    /// <exception cref="InvalidOperationException">The request was redirected, behind the client's back, to an origin that is not trusted.</exception>
    /// <exception cref="HttpResponseTooLargeException">The body is larger than <see cref="HttpClientOptions.MaxResponseBytes"/>. It is not retried.</exception>
    /// <exception cref="TimeoutException">Every attempt exceeded <see cref="HttpClientOptions.RequestTimeout"/>.</exception>
    /// <exception cref="HttpRequestException">The transport failed on every attempt.</exception>
    /// <exception cref="JsonException">A successful response body could not be deserialized.</exception>
    protected virtual async Task<HttpResult<TResult>> SendAsync<TResult>(
        HttpMethod method,
        string resourceUri,
        string? id,
        IDictionary<string, string>? parameters,
        object? body,
        IDictionary<string, string>? headers,
        string? baseUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);

        var uri = RequestUri.Build(baseUri ?? _options.BaseUri, resourceUri, id, parameters);
        EnsureTrusted(uri);

        // Scheme, host and path only: the query string and any user info may carry secrets.
        var loggedUri = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        // Serialize with the runtime type (not TResult or object) and only once: the same bytes are replayed on every retry.
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonSerializerOptions);

        var retry = _options.Retry;
        // A non-idempotent method gets a single attempt: retrying it could repeat a side effect.
        var maxAttempts = retry.IsEligible(method) ? Math.Max(retry.MaxRetries, 0) + 1 : 1;

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan delay;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = await CreateRequestAsync(method, uri, payload, headers, cancellationToken).ConfigureAwait(false);

                // The timeout applies to one attempt (headers + body read), not to the whole call. It is a linked source,
                // so the caller's token still cancels immediately, and we can tell the two cases apart in the catch below.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.RequestTimeout > TimeSpan.Zero && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
                    attemptCts.CancelAfter(_options.RequestTimeout);

                var client = _httpClientFactory.CreateClient(_options.HttpClientName ?? string.Empty);

                // Headers first, body later: the size limit is enforced while reading, so a hostile body is never buffered whole.
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);

                // The HTTP stack may have followed redirects behind our back. A response from an origin nobody declared is not the API's answer.
                if (response.RequestMessage?.RequestUri is { } finalUri && !_trustedOrigins.Contains(RequestUri.Origin(finalUri)))
                {
                    throw new InvalidOperationException(
                        $"The request was redirected to an origin that is not trusted ({RequestUri.Describe(finalUri.AbsoluteUri)}). Declare it in {nameof(HttpClientOptions.TrustedOrigins)} or disable automatic redirects.");
                }

                var result = await HttpResult<TResult>.FromResponseAsync(response, JsonSerializerOptions, _options.MaxResponseBytes, attemptCts.Token).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    _logger.LogDebug("HTTP {Method} {Uri} -> {StatusCode} in {ElapsedMs} ms", method, loggedUri, (int)result.StatusCode, stopwatch.ElapsedMilliseconds);
                    return result;
                }

                // null means "do not retry": attempts exhausted, status not retryable, or Retry-After longer than MaxDelay.
                var nextDelay = attempt < maxAttempts && retry.RetryStatusCodes.Contains(result.StatusCode)
                    ? retry.GetDelay(attempt, response.Headers.RetryAfter)
                    : null;

                if (nextDelay is null)
                {
                    _logger.LogWarning("HTTP {Method} {Uri} -> {StatusCode} {ReasonPhrase} in {ElapsedMs} ms", method, loggedUri, (int)result.StatusCode, result.ReasonPhrase, stopwatch.ElapsedMilliseconds);
                    return result;
                }

                delay = nextDelay.Value;
                _logger.LogWarning("HTTP {Method} {Uri} -> {StatusCode}; retry {Attempt}/{MaxRetries} in {DelayMs} ms", method, loggedUri, (int)result.StatusCode, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Not cancelled by the caller (that case is not caught and propagates as is): the attempt timed out.
                if (attempt >= maxAttempts)
                    throw new TimeoutException($"HTTP {method} {loggedUri} timed out after {_options.RequestTimeout.TotalSeconds:0.##} s (attempts: {attempt}).", ex);

                delay = retry.GetDelay(attempt)!.Value;
                _logger.LogWarning("HTTP {Method} {Uri} timed out; retry {Attempt}/{MaxRetries} in {DelayMs} ms", method, loggedUri, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                delay = retry.GetDelay(attempt)!.Value;
                _logger.LogWarning(ex, "HTTP {Method} {Uri} failed; retry {Attempt}/{MaxRetries} in {DelayMs} ms", method, loggedUri, attempt, maxAttempts - 1, (int)delay.TotalMilliseconds);
            }

            // Reached only when a retry was decided above; every other path returned or threw.
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a fresh request: a message cannot be sent twice, so every attempt gets its own.
    /// Headers are applied in increasing priority: <c>User-Agent</c>, authorization, option headers, per-call headers.
    /// </summary>
    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        Uri uri,
        byte[]? payload,
        IDictionary<string, string>? headers,
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

            if (payload is not null)
            {
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

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

    // Headers that frame the message or manage the connection. They belong to the HTTP stack: letting a caller set them is how request
    // smuggling and cache poisoning start, and nothing legitimate needs it.
    private static readonly HashSet<string> StackManagedHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection", "Keep-Alive", "Upgrade", "TE", "Trailer",
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

    private static void ValidateHeaderName(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(IsTokenCharacter))
            throw new ArgumentException("A header name is not a valid HTTP token (letters, digits and !#$%&'*+-.^_`|~ only).", "headers");

        if (StackManagedHeaderNames.Contains(name))
            throw new ArgumentException($"The header '{name}' is managed by the HTTP stack and cannot be set.", "headers");
    }

    // The value is never repeated in the message: it may be a credential. The name is safe to repeat: callers validate it first (or pass a literal).
    private static void ValidateHeaderValue(string value, string headerName)
    {
        foreach (var c in value)
        {
            if (c is (< ' ' and not '\t') or '\u007f')
                throw new ArgumentException($"The value of the header '{headerName}' contains a control character (line break, NUL...).", "headers");
        }
    }

    private static bool IsTokenCharacter(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

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
}
