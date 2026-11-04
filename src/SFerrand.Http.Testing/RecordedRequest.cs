namespace SFerrand.Http.Testing;

/// <summary>A request as the server saw it. A request is disposed once sent, so everything the tests need is copied when it arrives.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The full address, query string included.</param>
/// <param name="Headers">Every header, request and content headers together, by name (case-insensitive).</param>
/// <param name="Body">The body read as text, or <see langword="null"/> when the request had none.</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string[]> Headers, string? Body)
{
    /// <summary>The value of a header (several values are joined with a comma), or <see langword="null"/> when it was not sent.</summary>
    /// <param name="name">The header name; case does not matter.</param>
    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(",", values) : null;
}
