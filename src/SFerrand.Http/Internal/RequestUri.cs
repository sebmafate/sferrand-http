using System.Text;

namespace SFerrand.Http.Internal;

/// <summary>
/// Builds request URIs from a base address, a resource path, an optional identifier and query parameters.
/// </summary>
internal static class RequestUri
{
    /// <summary>
    /// <list type="bullet">
    /// <item>The resource path is always appended to the base address, whatever the slashes at the junction.</item>
    /// <item>An absolute <paramref name="resourceUri"/> replaces the base address.</item>
    /// <item><paramref name="id"/> becomes an extra path segment; a <c>/</c> inside it separates segments, each one URL-encoded.</item>
    /// <item>Query parameters are URL-encoded and added after any query already present in the resource.</item>
    /// </list>
    /// </summary>
    /// <param name="baseUri">Absolute http(s) base address.</param>
    /// <param name="resourceUri">Relative path (or absolute URL) of the resource; may be empty to target the base itself.</param>
    /// <param name="id">Optional identifier appended as path segment(s).</param>
    /// <param name="parameters">Optional query parameters, appended in order.</param>
    /// <exception cref="ArgumentException"><paramref name="baseUri"/> is not an absolute http(s) address.</exception>
    public static Uri Build(string baseUri, string resourceUri, string? id = null, IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUri);
        ArgumentNullException.ThrowIfNull(resourceUri);

        // IsAbsoluteHttp, not just "absolute": on Unix "/v1" parses as the file URI file:///v1.
        if (!IsAbsoluteHttp(baseUri))
            throw new ArgumentException($"'{baseUri}' is not an absolute http(s) URI.", nameof(baseUri));

        var raw = IsAbsoluteHttp(resourceUri)
            ? resourceUri
            : resourceUri.Length == 0
                ? baseUri
                : baseUri.TrimEnd('/') + "/" + resourceUri.TrimStart('/');

        var queryStart = raw.IndexOf('?');
        var path = queryStart < 0 ? raw : raw[..queryStart];
        var query = queryStart < 0 ? string.Empty : raw[(queryStart + 1)..];

        if (!string.IsNullOrEmpty(id))
        {
            var segments = id.Trim('/').Split('/').Select(Uri.EscapeDataString);
            path = path.TrimEnd('/') + "/" + string.Join('/', segments);
        }

        if (parameters is not null)
        {
            var encoded = new StringBuilder(query);
            foreach (var (key, value) in parameters)
            {
                if (encoded.Length > 0)
                    encoded.Append('&');
                encoded.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty));
            }
            query = encoded.ToString();
        }

        return new Uri(query.Length == 0 ? path : path + "?" + query, UriKind.Absolute);
    }

    private static bool IsAbsoluteHttp(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
