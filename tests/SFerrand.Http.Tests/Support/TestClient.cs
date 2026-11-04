using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SFerrand.Http.Tests.Support;

public sealed record Widget(string Name, int Size);

/// <summary>Exposes the protected API of <see cref="HttpClientBase"/>.</summary>
internal sealed class TestClient : HttpClientBase
{
    public TestClient(HttpClientOptions options, IHttpClientFactory factory, ILogger? logger = null)
        : base(Options.Create(options), factory, logger) { }

    public TestClient(HttpClientBase other) : base(other) { }

    public Task<HttpResult<T>> Get<T>(string resource, string? id = null, IDictionary<string, string>? parameters = null, IDictionary<string, string>? headers = null, string? baseUri = null, CancellationToken ct = default)
        => GetAsync<T>(resource, id, parameters, headers, baseUri, ct);

    public Task<HttpResult<IEnumerable<T>>> List<T>(string resource, IDictionary<string, string>? parameters = null)
        => ListAsync<T>(resource, parameters: parameters);

    public Task<HttpResult<T>> Post<T>(string resource, object? body = null, IDictionary<string, string>? headers = null, CancellationToken ct = default)
        => PostAsync<T>(resource, body, headers, cancellationToken: ct);

    public Task<HttpResult<T>> Put<T>(string resource, object body, string? id = null)
        => PutAsync<T>(resource, body, id);

    public Task<HttpResult<T>> Patch<T>(string resource, object body, string? id = null)
        => PatchAsync<T>(resource, body, id);

    public Task<bool> Delete(string resource, string? id = null, object? body = null)
        => DeleteAsync(resource, id, body);

    public Task<HttpResult<T>> GraphQL<T, TVariables>(GraphQLQuery<TVariables> query, string? graphQLUri = null)
        => SendGraphQLAsync<T, TVariables>(query, graphQLUri: graphQLUri);

    public Task<HttpResult<string>> Head(string resource)
        => SendAsync<string>(HttpMethod.Head, resource, null, null, null, null, null, CancellationToken.None);
}

/// <summary>Builds clients wired to a <see cref="StubHandler"/>, with fast retries.</summary>
internal static class TestClients
{
    public const string BaseUri = "https://api.test/v1";

    public static HttpClientOptions Options(Action<HttpClientOptions>? configure = null)
    {
        var options = new HttpClientOptions { BaseUri = BaseUri, RequestTimeout = TimeSpan.FromSeconds(5) };
        options.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(10);
        configure?.Invoke(options);
        return options;
    }

    public static TestClient Create(StubHandler handler, Action<HttpClientOptions>? configure = null, ILogger? logger = null)
        => new(Options(configure), new SimpleHttpClientFactory(handler), logger);
}

/// <summary>Captures formatted log messages.</summary>
internal sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add($"{logLevel}: {formatter(state, exception)}");
}
