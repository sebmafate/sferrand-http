# SFerrand.Http

A small base class for **typed REST and GraphQL API clients** on .NET 8+. You write the methods of your API,
it takes care of the plumbing: URLs, auth, headers, JSON, retries and timeouts.

```
dotnet add package SFerrand.Http
```

- **A result object, not exceptions, for HTTP errors**: `HttpResult<T>` carries the status, headers, raw body, the deserialized body
  and, for an error, the parsed [`ProblemDetails`](#errors). `EnsureSuccess()` turns it into an exception when you prefer one.
- **Resilient by default**: exponential backoff with jitter, `Retry-After`, per-attempt and overall timeouts, idempotency keys that make `POST` safe to retry.
- **Secure by default**: calls only go to declared origins, safe redirects, no clear text, no header injection, no path traversal, bounded responses (see [Security](#security)).
- **Everyday needs covered**: pagination as `IAsyncEnumerable`, form and multipart bodies, bounded streamed downloads, a typed GraphQL envelope.
- **Observable**: one span and three metrics under the name `SFerrand.Http` (OpenTelemetry-ready), compile-time log messages.
- **Trim and native-AOT friendly**: with a source-generated JSON context nothing relies on reflection.
- **Safe with a shared `HttpClient`**: auth and headers are set on each request, never on `DefaultRequestHeaders`.
- **Small surface, few dependencies**: only `Microsoft.Extensions.{Http,Options,Logging.Abstractions}`. A companion package, `SFerrand.Http.Testing`, helps to test your clients.

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

Registration, with the safe defaults (an `HttpClient` that does not follow redirects by itself, decompression, options validated **at start-up**):

```csharp
services.AddSFerrandHttpClient<WidgetsClient>(o =>
{
    o.BaseUri = "https://api.example.com/v2";
    o.TokenFactory = () => tokenProvider.CurrentToken;   // evaluated for every request
    o.Headers["X-Client"] = () => "my-app";
});
```

It returns the builder of the underlying `HttpClient`, so you can add handlers (`AddStandardResilienceHandler()` and so on). Each client has its own options.
You can also register things by hand, with any `IHttpClientFactory`: see [Redirects](#redirects) for the one setting that matters.

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
| `SendGraphQLAsync<T, TVariables>(query, headers?, graphQLUri?, ct)` | POST | endpoint from `GraphQLUri`; you define the envelope |
| `QueryGraphQLAsync<TData, TVariables>(query, ...)` | POST | the standard `{ data, errors }` envelope: see [Errors](#errors) |
| `PostFormAsync<T>(resource, fields, ...)` | POST | `application/x-www-form-urlencoded` |
| `PostMultipartAsync<T>(resource, form, ...)` | POST | `multipart/form-data` with a `MultipartForm` |
| `DownloadAsync(resource, stream, id?, parameters?, headers?, baseUri?, maxBytes?, ct)` | GET | body copied to a stream, bounded; returns the byte count |
| `GetPagesAsync<TPage>(...)` / `ListAllAsync<TItem>(...)` | GET | pages / items as `IAsyncEnumerable`: see [Pagination](#pagination) |
| `SendAsync<T>(method, resource, id, parameters, body, headers, baseUri, ct)` | any | the primitive behind all of the above |

`headers` replace same-named default headers for that call only; `baseUri` redirects a single call to another host, **provided its origin is declared** (see [Security](#security)).
A protected constructor `HttpClientBase(HttpClientBase other)` lets several focused clients share one configuration.

## URL building

The resource is **always appended** to the base address, whatever the slashes at the junction:

| Base | Resource | Id | Result |
|---|---|---|---|
| `https://api.test/v1` | `/users` | | `https://api.test/v1/users` |
| `https://api.test/orgs/acme/` | `repos` | `my repo` | `https://api.test/orgs/acme/repos/my%20repo` |
| `https://api.test/v1` | `repos` | `owner/name` | `https://api.test/v1/repos/owner/name` (each segment is encoded) |
| `https://api.test/v1` | `search` | | `…/search?q=a%20b&page=2` with `parameters` (encoded, in order) |

An absolute resource URL replaces the base only when it is on the **same origin** (or a declared trusted one). Any other origin is refused before anything is sent, so a resource that comes from a user cannot make the client call, with your token, an address you never configured.

## Security

The client is meant to be used with values that partly come from outside (an identifier typed by a user, a URL found in a response). These are the rules it enforces; each one is covered by tests, several of which reproduce a real attack.

| Risk | What the client does |
|---|---|
| **Credentials sent to the wrong host** (a user-supplied absolute URL, a per-call `baseUri`) | Only origins in `BaseUri`, `GraphQLUri` and `TrustedOrigins` can be called. Any other origin raises `ArgumentException` before anything is sent. |
| **Clear text** | `http://` is refused, except on `localhost`, `127.0.0.1` and `[::1]`, unless `AllowInsecureHttp` is set. Misconfiguration is detected when the client is built. |
| **Header injection** | Header values (default, per call, token, user agent) containing a line break or another control character are refused, as are invalid header names. The values are added without the framework's validation, which does **not** reject a line break: on a real connection `"ok\r\nX-Injected: 1"` arrived at the server as a second header. |
| **Request smuggling** | `Host`, `Content-Length`, `Transfer-Encoding`, `Connection`, `Upgrade`, `TE`, `Trailer`, `Keep-Alive` and `Proxy-Connection` cannot be set. |
| **Path traversal** | `.` and `..` segments (also as `%2e`, or with `\`) in a resource or an identifier are refused: `Uri` would silently resolve them and reach another endpoint with your credentials. A fragment in a resource is refused. Other characters of an identifier are encoded. |
| **Secrets in logs and messages** | Logs hold scheme, host and path only (never the query or user info), and error messages never repeat an address or a header value. Credentials embedded in an address (`user:pw@host`) are refused. |
| **Memory exhaustion** | A body larger than `MaxResponseBytes` (default 64 MiB) raises `HttpResponseTooLargeException` as soon as it is known or crossed, without being buffered whole, and is not retried. The timeout of an attempt covers the reading of the body. |
| **Redirects** | See below. |

### Redirects

The client follows redirects **itself**, hop by hop (`MaxRedirects`, default 5), and applies to each hop the rules of a call: the target must be on a trusted origin, a downgrade to clear text is refused, an address with credentials is refused. **Credentials stop at the first change of origin**: the token, the default headers and the per-call headers go to the origin the call started with and to no other (a redirect to a declared CDN is followed, without them). A 303 becomes a `GET`, a 301 or 302 turns a `POST` into a `GET`, a 307 or 308 keeps method and body (and is returned, not followed, when it would send a body to another origin). Past `MaxRedirects` the last 3xx is returned as the result.

This only works when the `HttpClient` does **not** follow redirects itself: when it does, it keeps every custom header (`X-Api-Key`, `Cookie`...) across origins and drops only `Authorization`, and the client sees nothing. So:

- `AddSFerrandHttpClient` and `SimpleHttpClientFactory` create clients that do not follow redirects;
- with your own registration, set it:

```csharp
services.AddHttpClient("api")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// and in the options: o.HttpClientName = "api";
```

- whatever the handler does, a response that comes from an origin you did not declare is never returned (`InvalidOperationException`). That protects against a forged answer, not against the leak, which has already happened by then: this is why the setting above matters.

Declare the extra hosts you really talk to (a file server, a CDN) explicitly:

```csharp
o.TrustedOrigins.Add("https://uploads.example.com");
```

## Errors

A 4xx or 5xx is a result, not an exception: look at `IsSuccess`, or call `EnsureSuccess()` to get an `ApiException` instead.

```csharp
var result = await client.GetAsync("42");

if (result.Problem is { } problem)                    // application/problem+json (RFC 9457), parsed for you
    Console.WriteLine($"{problem.Title}: {problem.Detail} ({problem.Extensions["traceId"]})");

var widget = (await client.GetAsync("42")).EnsureSuccess().Result;     // throws ApiException on an error status
```

`ApiException` carries the `StatusCode`, the raw `Content` and the `Problem`. Its **message never contains the raw body** (it can hold personal data or secrets, and messages end up in logs): only the status and the problem title.

A GraphQL server answers `200 OK` even when a field failed. `QueryGraphQLAsync` reads the standard envelope:

```csharp
var response = await QueryGraphQLAsync<WidgetData, object>(query);
var data = response.EnsureSuccess().Result!.EnsureNoErrors();    // GraphQLException if the server reported errors
```

## Pagination

```csharp
// Link: <...>; rel="next" (GitHub, GitLab...), or page numbers
await foreach (var repo in ListAllAsync<Repo>("orgs/acme/repos", paging: new PaginationOptions { PageSize = 100 }))
    Console.WriteLine(repo.Name);

// A cursor in the body: you say where the next page is
await foreach (var page in GetPagesAsync<Page>("widgets",
    next: r => r.Result?.NextCursor is { } c ? PageCursor.WithParameters(new Dictionary<string, string> { ["cursor"] = c }) : null))
{
    foreach (var item in page.EnsureSuccess().Result!.Items) { /* ... */ }
}
```

Pages are fetched as you enumerate: stop early and the requests stop. The next address comes from the server, so it is **untrusted**: it goes through the same checks as any call (a link to another origin or with credentials is refused), and a relative one is resolved against the page just requested. A loop, or more than `MaxPages` pages (default 1000), throws instead of truncating silently.

## Bodies and downloads

```csharp
await PostFormAsync<Token>("oauth/token", new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "read write" });

var form = new MultipartForm().AddField("title", "Report").AddFile("file", "report.pdf", pdfBytes, "application/pdf");
await PostMultipartAsync<Document>("documents", form);          // held in memory, replayable on a retry

await using var file = File.Create("big.bin");
var result = await DownloadAsync("files/42", file);             // streamed, bounded by MaxDownloadBytes (1 GiB)
Console.WriteLine($"{result.Result} bytes");
```

A download is retried like a `GET` while nothing has been written; after the first byte a failure is final (`DownloadInterruptedException`, with the byte count), because replaying would write the same bytes twice. Multipart field and file names containing a quote or a control character are refused: the framework writes them unescaped into the headers of the part.

## Retries and timeouts

```csharp
o.RequestTimeout = TimeSpan.FromSeconds(10);   // per attempt (default 30 s)
o.TotalTimeout = TimeSpan.FromSeconds(60);     // the whole call: attempts, waits and redirects (default: none)
o.Retry.MaxRetries = 3;                        // default 3; 0 disables
o.Retry.BaseDelay = TimeSpan.FromMilliseconds(500);
o.Retry.MaxDelay = TimeSpan.FromSeconds(30);
o.Retry.IdempotencyKeyHeader = "Idempotency-Key";   // makes POST and PATCH safe to retry, see below
```

- **What is retried**: statuses 408, 429, 500, 502, 503, 504 (`Retry.RetryStatusCodes`), `HttpRequestException`, a connection lost while the body is read,
  and attempts that exceeded `RequestTimeout`. GET, HEAD, OPTIONS, PUT and DELETE are eligible.
- **POST and PATCH are not replayed by default**: a response lost after the server acted would create the resource twice. Two ways out:
  `IdempotencyKeyHeader` (below), or `RetryNonIdempotentMethods = true` if you know the API tolerates it.
- **Idempotency keys**: with `IdempotencyKeyHeader` set, each `POST` or `PATCH` carries a fresh key, the **same on every attempt**, so a server that honours the header can recognize a replay and answer with the original outcome. A key you pass yourself in the headers of the call is used as is. Only enable it for an API that honours the header.
- **Delay**: `BaseDelay × 2^(n-1)`, capped at `MaxDelay`, with "equal jitter" (half fixed, half random) so concurrent
  clients do not retry in lockstep.
- **`Retry-After`** (seconds or date) is honoured. If the server asks for more than `MaxDelay`, retrying is pointless
  and the response is returned to you immediately.
- **When retries run out**: a failing status is returned as the last `HttpResult`; a timeout becomes a
  `TimeoutException` (the message says whether it was an attempt or the total); a transport error is rethrown. Caller cancellation (`CancellationToken`) stops everything at once,
  including a pending retry wait, and surfaces as `OperationCanceledException`.

## Observability

Subscribe to `SFerrandHttpTelemetry.Name` (`"SFerrand.Http"`):

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SFerrandHttpTelemetry.Name))
    .WithMetrics(m => m.AddMeter(SFerrandHttpTelemetry.Name));
```

- **A span per logical call** (retries and redirects included; the HTTP stack records its own span for each request underneath, so the retries show as children, and the W3C trace context is propagated for you). Tags: `http.request.method`, `server.address`, `server.port`, `url.full` (scheme, host and path only: **never the query string**), `http.response.status_code`, `http.request.resend_count`, `error.type`.
- **Metrics**: `sferrand.http.client.call.duration` (histogram, seconds), `sferrand.http.client.retries` and `sferrand.http.client.redirects` (counters).
- **Logs** are generated at compile time and never contain query strings, bodies or credentials.

## Good to know

- **JSON** defaults to `JsonSerializerDefaults.Web` (camelCase, case-insensitive reads). Change `o.JsonSerializerOptions`
  for APIs using `snake_case` or other conventions.
- **Logging** records method, URL **without its query string** (it may carry secrets), status and duration. Bodies and
  tokens are never logged.
- **HTTP version** is a preference (`o.HttpVersion = HttpVersion.Version20`), requests may fall back to a lower one.
- **Async tokens**: set `o.AsyncTokenFactory` instead of `TokenFactory` to fetch credentials without blocking.
- A successful response whose body is not valid JSON for `T` throws `JsonException` and is not retried.
- `Accept` is `application/json` unless you change `o.Accept` or set the header yourself.

## Trimming and native AOT

The library is marked trimmable and AOT-compatible (the analyzers run on every build). JSON metadata comes from `JsonTypeInfo`, so a source-generated context is all it needs:

```csharp
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(IEnumerable<Widget>))]      // what ListAsync<Widget> reads: declare the exact type
internal sealed partial class AppJson : JsonSerializerContext;

o.JsonSerializerOptions.TypeInfoResolver = AppJson.Default;
```

A type the context does not know is refused, never silently read by reflection, and without a resolver in an application where reflection is turned off you get a message that says what to set. Checked by publishing a fully trimmed application with reflection-based JSON disabled and running it.

## Testing your clients

The companion package `SFerrand.Http.Testing` provides a scripted, recording `HttpMessageHandler`:

```
dotnet add package SFerrand.Http.Testing
```

```csharp
var handler = new StubHandler()
    .Respond(HttpStatusCode.ServiceUnavailable)                          // the first attempt fails...
    .Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");            // ...the retry succeeds
var client = new WidgetsClient(Options.Create(new HttpClientOptions { BaseUri = "https://api.test", TokenFactory = () => "tok" }), new SimpleHttpClientFactory(handler), null);

var result = await client.GetAsync("42");

Assert.AreEqual(2, handler.Requests.Count);
Assert.AreEqual("Bearer tok", handler.Requests[1].Header("Authorization"));
```

`Respond`, `Throw` (a transport failure) and `Hang` (a server that never answers) script the outcomes; `Requests` records method, address, every header and the body.
With `AddSFerrandHttpClient`, use `.ConfigurePrimaryHttpMessageHandler(() => handler)`.

## Documentation

- **API reference**: every public and protected member is documented with XML comments (shipped in the package, so IntelliSense shows them).
- [Security policy](SECURITY.md) and the [Security](#security) section above.
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
