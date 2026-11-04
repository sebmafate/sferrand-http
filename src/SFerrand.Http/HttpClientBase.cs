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
/// <item>Transient failures are retried according to <see cref="HttpClientOptions.Retry"/>; only idempotent methods by default.</item>
/// </list>
/// </remarks>
public abstract class HttpClientBase
{
    private readonly HttpClientOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
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

        if (!Uri.TryCreate(_options.BaseUri, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{nameof(HttpClientOptions.BaseUri)} must be an absolute http(s) URI, got '{_options.BaseUri}'.", nameof(options));
        }
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
    }

    /// <summary>The JSON options used for request bodies and response parsing.</summary>
    protected virtual JsonSerializerOptions JsonSerializerOptions => _options.JsonSerializerOptions;

    #region GET / LIST

    /// <summary>Sends a <c>GET</c> request and deserializes the body into <typeparamref name="TResult"/>.</summary>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="parameters">Optional query string parameters.</param>
    /// <param name="headers">Optional headers for this call only; they replace same-named default headers.</param>
    /// <param name="baseUri">Optional base address for this call only.</param>
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
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="body">Object serialized as JSON; <see langword="null"/> sends no body.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    protected virtual Task<HttpResult<TResult>> PostAsync<TResult>(
        string resourceUri,
        object? body = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
        => SendAsync<TResult>(HttpMethod.Post, resourceUri, id: null, parameters: null, body, headers, baseUri, cancellationToken);

    /// <summary>Sends a <c>PUT</c> request with a JSON body.</summary>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="body">Object serialized as JSON.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only.</param>
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
    /// <typeparam name="TResult">Type of the response body; <see cref="string"/> keeps it raw.</typeparam>
    /// <param name="method">The HTTP method.</param>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="parameters">Optional query string parameters.</param>
    /// <param name="body">Object serialized as JSON; <see langword="null"/> sends no body.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
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
        var loggedUri = uri.GetLeftPart(UriPartial.Path); // the query string may carry secrets
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonSerializerOptions);

        var retry = _options.Retry;
        var maxAttempts = retry.IsEligible(method) ? Math.Max(retry.MaxRetries, 0) + 1 : 1;

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan delay;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = await CreateRequestAsync(method, uri, payload, headers, cancellationToken).ConfigureAwait(false);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_options.RequestTimeout > TimeSpan.Zero && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
                    attemptCts.CancelAfter(_options.RequestTimeout);

                var client = _httpClientFactory.CreateClient(_options.HttpClientName ?? string.Empty);
                using var response = await client.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                var result = await HttpResult<TResult>.FromResponseAsync(response, JsonSerializerOptions, attemptCts.Token).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    _logger.LogDebug("HTTP {Method} {Uri} -> {StatusCode} in {ElapsedMs} ms", method, loggedUri, (int)result.StatusCode, stopwatch.ElapsedMilliseconds);
                    return result;
                }

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
                // Not cancelled by the caller: the attempt timed out.
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

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds a fresh request: a message cannot be sent twice, so every attempt gets its own.</summary>
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
                request.Headers.Authorization = new AuthenticationHeaderValue(_options.AuthenticationScheme, token);

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

    // Headers that .NET only accepts on HttpContent: using them on the request headers throws.
    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    /// <summary>
    /// Sets a header, replacing any previous value. Content headers (<c>Content-Type</c>...) go on the content;
    /// they are ignored for requests without a body, since there is nothing to describe.
    /// </summary>
    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        HttpHeaders? headers = ContentHeaderNames.Contains(name) ? request.Content?.Headers : request.Headers;
        if (headers is null)
            return;

        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
