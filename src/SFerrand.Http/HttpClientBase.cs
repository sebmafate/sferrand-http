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
public abstract partial class HttpClientBase
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
        if (_options.MaxRedirects < 0)
            throw new ArgumentException($"{nameof(HttpClientOptions.MaxRedirects)} must not be negative.", nameof(options));
        if (_options.TotalTimeout is { } total && total <= TimeSpan.Zero && total != Timeout.InfiniteTimeSpan)
            throw new ArgumentException($"{nameof(HttpClientOptions.TotalTimeout)} must be positive, or null for no deadline.", nameof(options));
        if (_options.Accept is { } accept)
            ValidateHeaderValue(accept, "Accept");
        if (_options.Retry.IdempotencyKeyHeader is { } keyHeader)
            ValidateHeaderName(keyHeader);

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
    protected virtual Task<HttpResult<TResult>> SendAsync<TResult>(
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

        return SendCoreAsync(
            method, resourceUri, id, parameters, RequestBody.From(body, JsonSerializerOptions), headers, baseUri,
            (response, token) => HttpResult<TResult>.FromResponseAsync(response, JsonSerializerOptions, _options.MaxResponseBytes, token),
            cancellationToken);
    }
}
