namespace SFerrand.Http;

/// <summary>
/// The response body is larger than <see cref="HttpClientOptions.MaxResponseBytes"/>.
/// </summary>
/// <remarks>
/// It does not derive from <see cref="HttpRequestException"/> on purpose: a transport failure is retried, and downloading a body that is
/// too large again would only repeat the problem.
/// </remarks>
public sealed class HttpResponseTooLargeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="limit">The limit that was exceeded, in bytes.</param>
    public HttpResponseTooLargeException(long limit)
        : base($"The response is larger than the limit of {limit} bytes.") => Limit = limit;

    /// <summary>The limit that was exceeded, in bytes.</summary>
    public long Limit { get; }
}
