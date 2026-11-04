using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace SFerrand.Http.Internal;

/// <summary>
/// Builds request URIs from a base address, a resource path, an optional identifier and query parameters, and refuses the shapes that
/// let a caller-controlled string reach somewhere else than intended.
/// </summary>
internal static class RequestUri
{
    /// <summary>
    /// <list type="bullet">
    /// <item>The resource path is always appended to the base address, whatever the slashes at the junction.</item>
    /// <item>An absolute <paramref name="resourceUri"/> replaces the base address (the caller decides whether that origin is acceptable).</item>
    /// <item><paramref name="id"/> becomes an extra path segment; a <c>/</c> inside it separates segments, each one URL-encoded.</item>
    /// <item>Query parameters are URL-encoded and added after any query already present in the resource.</item>
    /// </list>
    /// Refused with an <see cref="ArgumentException"/> that never repeats the offending value (it may be a secret): credentials embedded
    /// in an address, a fragment in the resource, and <c>.</c> or <c>..</c> segments in the resource or the identifier, which <see cref="Uri"/>
    /// would silently resolve and so climb out of the intended path.
    /// </summary>
    /// <param name="baseUri">Absolute http(s) base address.</param>
    /// <param name="resourceUri">Relative path (or absolute URL) of the resource; may be empty to target the base itself.</param>
    /// <param name="id">Optional identifier appended as path segment(s).</param>
    /// <param name="parameters">Optional query parameters, appended in order.</param>
    /// <exception cref="ArgumentException">An argument has one of the shapes listed above, or <paramref name="baseUri"/> is not an absolute http(s) address.</exception>
    public static Uri Build(string baseUri, string resourceUri, string? id = null, IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUri);
        ArgumentNullException.ThrowIfNull(resourceUri);

        // IsAbsoluteHttp, not just "absolute": on Unix "/v1" parses as the file URI file:///v1.
        if (!TryParseHttp(baseUri, out var parsedBase))
            throw new ArgumentException($"The base address is not an absolute http(s) URI ({Describe(baseUri)}).", nameof(baseUri));
        RejectCredentials(parsedBase, nameof(baseUri));

        if (resourceUri.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("The resource must not contain a fragment ('#'): a fragment is never sent, and would swallow the identifier and the parameters.", nameof(resourceUri));

        // "ftp://host/x" is not a path to append to the base address: it is an address, and not one this client can call.
        if (HasOtherScheme(resourceUri))
            throw new ArgumentException("The resource is an address with a scheme other than http or https.", nameof(resourceUri));

        if (TryParseHttp(resourceUri, out var parsedResource))
            RejectCredentials(parsedResource, nameof(resourceUri));

        var raw = parsedResource is not null
            ? resourceUri
            : resourceUri.Length == 0
                ? baseUri
                : baseUri.TrimEnd('/') + "/" + resourceUri.TrimStart('/');

        var queryStart = raw.IndexOf('?');
        var path = queryStart < 0 ? raw : raw[..queryStart];
        var query = queryStart < 0 ? string.Empty : raw[(queryStart + 1)..];

        // Only what the caller supplied is checked (the base address is configuration, and may legitimately contain anything the owner wrote).
        var suppliedPath = parsedResource is not null ? path : resourceUri.Split('?')[0];
        if (HasDotSegment(suppliedPath, decodePercentDots: true))
            throw new ArgumentException("The resource must not contain '.' or '..' segments.", nameof(resourceUri));

        if (!string.IsNullOrEmpty(id))
        {
            if (HasDotSegment(id, decodePercentDots: false))
                throw new ArgumentException("The identifier must not contain '.' or '..' segments.", nameof(id));

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

    // scheme "://" at the start; a relative path never starts like this.
    private static bool HasOtherScheme(string resource)
    {
        var separator = resource.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !char.IsAsciiLetter(resource[0]))
            return false;

        for (var i = 1; i < separator; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(resource[i]) || resource[i] is '+' or '-' or '.'))
                return false;
        }

        var scheme = resource[..separator];
        return !scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses an absolute http(s) address.</summary>
    public static bool TryParseHttp(string value, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>
    /// The origin of an address: scheme, host and port, normalized (case, default ports, internationalized names). Two addresses can send each other
    /// credentials only when their origins are equal.
    /// </summary>
    public static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}"; // Uri already lower-cases the host

    /// <summary>
    /// A description of an address that is safe to put in a message or a log: scheme, host and port only. Never the user info, the path or the
    /// query, which can hold secrets, and nothing at all of a value that is not an address.
    /// </summary>
    public static string Describe(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.Host}" + (uri.IsDefaultPort ? string.Empty : $":{uri.Port}")
            : "not an absolute URI";

    /// <summary>Credentials in an address (<c>user:password@host</c>) end up in logs and proxies: they have no place there.</summary>
    public static void RejectCredentials(Uri uri, string parameterName)
    {
        if (uri.UserInfo.Length > 0)
            throw new ArgumentException("An address must not contain credentials (user:password@host): use TokenFactory or a header.", parameterName);
    }

    private static bool HasDotSegment(string path, bool decodePercentDots)
    {
        foreach (var segment in path.Split('/', '\\'))
        {
            var candidate = decodePercentDots ? segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) : segment;
            if (candidate is "." or "..")
                return true;
        }

        return false;
    }
}
