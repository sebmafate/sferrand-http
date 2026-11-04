# SFerrand.Http

A small base class for **typed REST and GraphQL API clients** on .NET 8+. You write the methods of your API,
it takes care of the plumbing: URLs, auth, headers, JSON, retries and timeouts.

```
dotnet add package SFerrand.Http
```

- **A result object, not exceptions, for HTTP errors**: `HttpResult<T>` carries the status, headers, raw body and
  the deserialized body. Exceptions are kept for what is actually exceptional (transport failure, timeout, unreadable JSON).
- **Resilient by default**: exponential backoff with jitter, `Retry-After` support, per-attempt timeouts. Only idempotent
  methods are retried unless you opt in.
- **Safe with a shared `HttpClient`**: auth and headers are set on each request, never on `DefaultRequestHeaders`,
  so nothing accumulates and tokens can be refreshed on every call.
- **Predictable URLs**: no `BaseAddress` surprises; the resource is always appended to the base.
- **Small surface, few dependencies**: only `Microsoft.Extensions.{Http,Options,Logging.Abstractions}`.

## Quick start

```csharp
using SFerrand.Http;

public sealed record Widget(string Name, int Size);

public sealed class WidgetsClient : HttpClientBase
{
    public WidgetsClient(IOptions<HttpClientOptions> options, IHttpClientFactory factory, ILogger<WidgetsClient> logger)
        : base(options, factory, logger) { }

    public Task<HttpResult<Widget>> GetAsync(string id, CancellationToken ct = default)
        => GetAsync<Widget>("widgets", id, cancellationToken: ct);

    public Task<HttpResult<IEnumerable<Widget>>> SearchAsync(string text, CancellationToken ct = default)
        => ListAsync<Widget>("widgets", parameters: new Dictionary<string, string> { ["q"] = text }, cancellationToken: ct);

    public Task<HttpResult<Widget>> CreateAsync(Widget widget, CancellationToken ct = default)
        => PostAsync<Widget>("widgets", widget, cancellationToken: ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => DeleteAsync("widgets", id, cancellationToken: ct);
}
```

Registration (works with any `IHttpClientFactory`; `AddHttpClient` gives you pooling and handler lifetime):

```csharp
services.AddHttpClient();
services.Configure<HttpClientOptions>(o =>
{
    o.BaseUri = "https://api.example.com/v2";
    o.TokenFactory = () => tokenProvider.CurrentToken;   // evaluated for every request
    o.Headers["X-Client"] = () => "my-app";
});
services.AddTransient<WidgetsClient>();
```

Usage:

```csharp
var result = await client.GetAsync("42");

if (result.IsSuccess)
    Console.WriteLine(result.Result!.Name);                       // deserialized body
else
    Console.WriteLine($"{(int)result.StatusCode}: {result.Content}"); // raw error body, no exception
```

`HttpResult<T>` converts implicitly to `T?`, and `HttpResult<string>` keeps the body raw (no JSON parsing).

## The API you inherit

| Method | HTTP | Notes |
|---|---|---|
| `GetAsync<T>(resource, id?, parameters?, headers?, baseUri?, ct)` | GET | |
| `ListAsync<T>(...)` | GET | JSON array into `IEnumerable<T>` |
| `PostAsync<T>(resource, body?, headers?, baseUri?, ct)` | POST | no body when `null` |
| `PutAsync<T>(resource, body, id?, ...)` | PUT | |
| `PatchAsync<T>(resource, body, id?, ...)` | PATCH | |
| `DeleteAsync(resource, id?, body?, ...)` | DELETE | returns `bool` |
| `SendGraphQLAsync<T, TVariables>(query, headers?, graphQLUri?, ct)` | POST | endpoint from `GraphQLUri` |
| `SendAsync<T>(method, resource, id, parameters, body, headers, baseUri, ct)` | any | the primitive behind all of the above |

`headers` replace same-named default headers for that call only; `baseUri` redirects a single call to another host.
A protected constructor `HttpClientBase(HttpClientBase other)` lets several focused clients share one configuration.

## URL building

The resource is **always appended** to the base address, whatever the slashes at the junction:

| Base | Resource | Id | Result |
|---|---|---|---|
| `https://api.test/v1` | `/users` | | `https://api.test/v1/users` |
| `https://api.test/orgs/acme/` | `repos` | `my repo` | `https://api.test/orgs/acme/repos/my%20repo` |
| `https://api.test/v1` | `repos` | `owner/name` | `https://api.test/v1/repos/owner/name` (each segment is encoded) |
| `https://api.test/v1` | `search` | | `…/search?q=a%20b&page=2` with `parameters` (encoded, in order) |

An absolute resource URL (`https://other.test/x`) replaces the base.

## Retries and timeouts

```csharp
o.RequestTimeout = TimeSpan.FromSeconds(10);   // per attempt (default 30 s)
o.Retry.MaxRetries = 3;                        // default 3; 0 disables
o.Retry.BaseDelay = TimeSpan.FromMilliseconds(500);
o.Retry.MaxDelay = TimeSpan.FromSeconds(30);
o.Retry.RetryNonIdempotentMethods = false;     // POST/PATCH are not replayed unless you opt in
```

- **What is retried**: statuses 408, 429, 500, 502, 503, 504 (`Retry.RetryStatusCodes`), `HttpRequestException`,
  and attempts that exceeded `RequestTimeout`. GET, HEAD, OPTIONS, PUT and DELETE are eligible.
- **Delay**: `BaseDelay × 2^(n-1)`, capped at `MaxDelay`, with "equal jitter" (half fixed, half random) so concurrent
  clients do not retry in lockstep.
- **`Retry-After`** (seconds or date) is honoured. If the server asks for more than `MaxDelay`, retrying is pointless
  and the response is returned to you immediately.
- **When retries run out**: a failing status is returned as the last `HttpResult`; a timeout becomes a
  `TimeoutException`; a transport error is rethrown. Caller cancellation (`CancellationToken`) stops everything at once,
  including a pending retry wait, and surfaces as `OperationCanceledException`.

## Good to know

- **JSON** defaults to `JsonSerializerDefaults.Web` (camelCase, case-insensitive reads). Change `o.JsonSerializerOptions`
  for APIs using `snake_case` or other conventions.
- **Logging** records method, URL **without its query string** (it may carry secrets), status and duration. Bodies and
  tokens are never logged.
- **HTTP version** is a preference (`o.HttpVersion = HttpVersion.Version20`), requests may fall back to a lower one.
- **Async tokens**: set `o.AsyncTokenFactory` instead of `TokenFactory` to fetch credentials without blocking.
- A successful response whose body is not valid JSON for `T` throws `JsonException` and is not retried.

## Testing your clients

`SimpleHttpClientFactory` accepts any `HttpMessageHandler`, so a stub is enough to test a client end to end without
a network:

```csharp
var handler = new StubHandler();                       // your HttpMessageHandler returning canned responses
var factory = new SimpleHttpClientFactory(handler);
var client = new WidgetsClient(Options.Create(new HttpClientOptions { BaseUri = "https://api.test" }), factory, NullLogger<WidgetsClient>.Instance);
```

The [test suite](tests/SFerrand.Http.Tests) contains a ready-made recording stub (`Support/StubHandler.cs`).

## Documentation

- **API reference**: every public and protected member is documented with XML comments (shipped in the package, so IntelliSense shows them).
- [Design notes](docs/DESIGN.md): why it works this way, what was rejected, known limits.
- [Test suite](tests/README.md): strategy, conventions, timing-sensitive tests.
- [Contributing](CONTRIBUTING.md), [Changelog](CHANGELOG.md) and [Releasing](RELEASING.md) (versioning and publication of the NuGet package).

## Build and test

```
dotnet test
```

The library and its tests target `net8.0` and `net10.0`. The tests run on both when both runtimes are installed (as in CI);
otherwise the `net8.0` run rolls forward to the newest runtime. Coverage and mutation-check procedures are in [CONTRIBUTING](CONTRIBUTING.md).

## License

[MIT](LICENSE)
