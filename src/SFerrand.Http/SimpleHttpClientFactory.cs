using System.Collections.Concurrent;

namespace SFerrand.Http;

/// <summary>
/// A minimal <see cref="IHttpClientFactory"/> for tests, scripts and console tools:
/// one thread-safe, cached <see cref="HttpClient"/> per name, optionally backed by a custom handler.
/// </summary>
/// <remarks>
/// In an ASP.NET Core or Generic Host application, prefer <c>services.AddHttpClient()</c>.
/// Clients are shared: do not change their <see cref="HttpClient.BaseAddress"/> or default headers.
/// <para>
/// Without a custom handler the clients <b>do not follow redirects</b>. When the HTTP stack follows a redirect to another origin it keeps every
/// custom header (<c>X-Api-Key</c>, <c>Cookie</c>...) and drops only <c>Authorization</c>, so a redirect would hand your API key to whoever
/// the server pointed at. A 3xx is returned to the caller as an ordinary result. See the README for the same setting with <c>AddHttpClient</c>.
/// </para>
/// </remarks>
public sealed class SimpleHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly HttpMessageHandler? _handler;

    /// <summary>Creates a factory whose clients all use <paramref name="handler"/>.</summary>
    /// <param name="handler">
    /// Handler used by every client, typically a stub in tests. It is not disposed with the factory.
    /// <see langword="null"/> uses the default handler.
    /// </param>
    public SimpleHttpClientFactory(HttpMessageHandler? handler = null) => _handler = handler;

    /// <inheritdoc />
    public HttpClient CreateClient(string name)
        => _clients.GetOrAdd(name ?? string.Empty, _ => _handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : new HttpClient(_handler, disposeHandler: false));

    /// <summary>Disposes every client created so far. The custom handler, if any, is left to its owner.</summary>
    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
