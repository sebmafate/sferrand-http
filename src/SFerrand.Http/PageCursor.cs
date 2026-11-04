using System.Net.Http.Headers;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

/// <summary>Where the next page of a paginated resource is, as told by the page just read.</summary>
/// <remarks>
/// Either an address (a <c>Link</c> header, a <c>next</c> field in the body), absolute or relative to the page just requested, or parameters to change on the same
/// request (a page number, a cursor token). An address is subject to the usual rules: it must be on a trusted origin.
/// </remarks>
public sealed class PageCursor
{
    private PageCursor(string? url, IReadOnlyDictionary<string, string>? parameters)
    {
        Url = url;
        Parameters = parameters;
    }

    /// <summary>The address of the next page, absolute or relative to the page just requested; <see langword="null"/> when the cursor carries parameters.</summary>
    public string? Url { get; }

    /// <summary>The parameters to set on the request, over those already there; <see langword="null"/> when the cursor carries an address.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; }

    /// <summary>A cursor made of the address of the next page.</summary>
    /// <param name="url">Absolute, or relative to the page just requested (RFC 3986).</param>
    public static PageCursor ToUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return new PageCursor(url, null);
    }

    /// <summary>A cursor made of query parameters to set on the next request (for example <c>page</c>, or an opaque <c>cursor</c> token).</summary>
    /// <param name="parameters">Names and values; they replace parameters of the same name on the previous request.</param>
    public static PageCursor WithParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new PageCursor(null, parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    /// <summary>The cursor announced by a <c>Link: &lt;...&gt;; rel="next"</c> header, or <see langword="null"/> on the last page.</summary>
    /// <param name="headers">The response headers.</param>
    public static PageCursor? FromLinkHeader(HttpResponseHeaders? headers)
        => LinkHeader.FindNext(headers) is { } next ? ToUrl(next) : null;
}
