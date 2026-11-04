# SFerrand.Http.Testing

A scripted, recording `HttpMessageHandler` to test code that uses `HttpClient`, in particular API clients built on
[SFerrand.Http](https://github.com/sebmafate/sferrand-http), end to end and without a network.

```
dotnet add package SFerrand.Http.Testing
```

```csharp
var handler = new StubHandler()
    .Respond(HttpStatusCode.ServiceUnavailable)                         // the first attempt fails...
    .Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");           // ...the retry succeeds

var client = new WidgetsClient(Options.Create(new HttpClientOptions { BaseUri = "https://api.test" }),
                               new SimpleHttpClientFactory(handler), logger: null);

var result = await client.GetAsync("42");

Assert.AreEqual(2, handler.Requests.Count);
Assert.AreEqual("Bearer tok", handler.Requests[1].Header("Authorization"));
```

- `Respond(status, body, configure)`: a response (the body is JSON); `configure` sets headers such as `Retry-After`, `Location` or `Link`.
- `Respond(request => ...)`: a response computed from the request.
- `Throw(exception)`: a transport failure. `Hang()`: a server that never answers (to test timeouts and cancellation).
- The last scripted outcome repeats, so `Respond(503)` alone means "always 503".
- `Requests`: every request received, with method, address, all headers (request and content) and the body as text.

MIT licence.
