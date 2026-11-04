using System.Collections.Concurrent;

namespace SFerrand.Http;

/// <summary>
/// A minimal <see cref="IHttpClientFactory"/> for tests, scripts and console tools:
/// one thread-safe, cached <see cref="HttpClient"/> per name, optionally backed by a custom handler.
/// </summary>
/// <remarks>
/// In an ASP.NET Core or Generic Host application, prefer <c>services.AddHttpClient()</c>.
/// Clients are shared: do not change their <see cref="HttpClient.BaseAddress"/> or default headers.
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
        => _clients.GetOrAdd(name ?? string.Empty, _ => _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false));

    /// <summary>Disposes every client created so far. The custom handler, if any, is left to its owner.</summary>
    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }
}
