using System.Text.Json;

namespace SFerrand.Http;

/// <summary>
/// Configuration of an <see cref="HttpClientBase"/>.
/// </summary>
public class HttpClientOptions
{
    /// <summary>
    /// Absolute base address of the API. Resource paths are always appended to it
    /// (<c>https://api.example.com/v2</c> + <c>users</c> gives <c>https://api.example.com/v2/users</c>).
    /// </summary>
    public string BaseUri { get; set; } = string.Empty;

    /// <summary>Absolute address of the GraphQL endpoint, when different from <see cref="BaseUri"/>.</summary>
    public string? GraphQLUri { get; set; }

    /// <summary>Scheme of the <c>Authorization</c> header. Default: <c>Bearer</c>.</summary>
    public string AuthenticationScheme { get; set; } = "Bearer";

    /// <summary>
    /// Returns the credential to send in the <c>Authorization</c> header. Evaluated for every request,
    /// so it can return a refreshed token. A <see langword="null"/> or empty result sends no header.
    /// </summary>
    public Func<string?>? TokenFactory { get; set; }

    /// <summary>Asynchronous alternative to <see cref="TokenFactory"/>; takes precedence when both are set.</summary>
    public Func<CancellationToken, ValueTask<string?>>? AsyncTokenFactory { get; set; }

    /// <summary>
    /// Headers added to every request. Values are factories evaluated for every request.
    /// A header with the same name passed to a call replaces the one set here.
    /// </summary>
    public IDictionary<string, Func<string>> Headers { get; } =
        new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// JSON options used for request bodies and response parsing.
    /// Default: <see cref="JsonSerializerDefaults.Web"/> (camelCase, case-insensitive reads).
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>Name of the <see cref="HttpClient"/> to request from the <see cref="IHttpClientFactory"/>.</summary>
    public string? HttpClientName { get; set; }

    /// <summary>Value of the <c>User-Agent</c> header. Default: the entry assembly name.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Maximum duration of a single attempt (not of the whole call, retries included).
    /// Use <see cref="Timeout.InfiniteTimeSpan"/> to disable. Default: 30 s.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Preferred HTTP version, e.g. <c>HttpVersion.Version20</c>. The request may fall back to a lower version.
    /// <see langword="null"/> keeps the framework default.
    /// </summary>
    public Version? HttpVersion { get; set; }

    /// <summary>Retry policy.</summary>
    public RetryOptions Retry { get; } = new();
}
