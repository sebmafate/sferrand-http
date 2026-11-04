using System.Net;
using System.Net.Sockets;
using System.Text;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// End-to-end tests against a real HTTP server on the loopback interface, going through the genuine
/// <see cref="HttpClient"/> pipeline (header validation, content negotiation, sockets) rather than a stub.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class IntegrationTests
{
    private sealed record Seen(string Method, string PathAndQuery, string? Authorization, string? UserAgent, string? Custom, string? ContentType, string Body);

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<int, Seen, HttpListenerResponse, Task> _handler;
        private int _hits;

        public string BaseUri { get; }
        public List<Seen> Requests { get; } = new();

        public LoopbackServer(Func<int, Seen, HttpListenerResponse, Task> handler)
        {
            _handler = handler;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUri = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUri + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                var request = context.Request;
                var body = request.HasEntityBody ? await new StreamReader(request.InputStream).ReadToEndAsync() : string.Empty;
                var seen = new Seen(request.HttpMethod, request.Url!.PathAndQuery, request.Headers["Authorization"], request.Headers["User-Agent"],
                    request.Headers["X-Custom"], request.ContentType, body);
                lock (Requests) Requests.Add(seen);

                await _handler(Interlocked.Increment(ref _hits), seen, context.Response);
                context.Response.Close();
            }
        }

        public void Stop() => _listener.Stop();

        public void Dispose() => _listener.Close();
    }

    private static async Task Json(HttpListenerResponse response, int status, string json)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
    }

    private static TestClient Client(string baseUri, Action<HttpClientOptions>? configure = null)
        => new(
            TestClients.Options(o =>
            {
                o.BaseUri = baseUri + "/api";
                o.TokenFactory = () => "tok";
                o.UserAgent = "integration/1.0";
                o.Headers["X-Custom"] = () => "yes";
                configure?.Invoke(o);
            }),
            new SimpleHttpClientFactory());

    [TestMethod]
    public async Task Get_RetriesAfterA503_AndSendsRealHeaders()
    {
        using var server = new LoopbackServer((hit, _, response) => hit == 1
            ? Json(response, 503, "{}")
            : Json(response, 200, """{"name":"w","size":3}"""));
        var client = Client(server.BaseUri);

        var result = await client.Get<Widget>("widgets", "42", new Dictionary<string, string> { ["q"] = "a b" });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(new Widget("w", 3), result.Result);
        Assert.HasCount(2, server.Requests);
        Assert.AreEqual("/api/widgets/42?q=a%20b", server.Requests[1].PathAndQuery);
        Assert.AreEqual("Bearer tok", server.Requests[1].Authorization);
        Assert.AreEqual("integration/1.0", server.Requests[1].UserAgent);
        Assert.AreEqual("yes", server.Requests[1].Custom);
    }

    [TestMethod]
    public async Task Post_SendsAJsonBody_AndDeleteSucceeds()
    {
        using var server = new LoopbackServer((_, seen, response) => seen.Method == "DELETE"
            ? Json(response, 204, string.Empty)
            : Json(response, 201, """{"name":"n","size":1}"""));
        var client = Client(server.BaseUri);

        var created = await client.Post<Widget>("widgets", new Widget("n", 1));
        var deleted = await client.Delete("widgets", "42");

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.AreEqual("""{"name":"n","size":1}""", server.Requests[0].Body);
        Assert.AreEqual("application/json; charset=utf-8", server.Requests[0].ContentType);
        Assert.IsTrue(deleted);
        Assert.AreEqual("DELETE", server.Requests[1].Method);
    }

    /// <summary>
    /// Timing-based by nature: the server answers after 2 s, the attempt budget is 100 ms, so the margin is 20x and the test stays reliable on a slow CI machine.
    /// </summary>
    [TestMethod]
    public async Task ASlowServer_TriggersTheAttemptTimeout()
    {
        using var server = new LoopbackServer(async (_, _, response) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Json(response, 200, "{}");
        });
        var client = Client(server.BaseUri, o =>
        {
            o.RequestTimeout = TimeSpan.FromMilliseconds(100);
            o.Retry.MaxRetries = 0;
        });

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<Widget>("widgets"));
    }

    /// <summary>
    /// Reserves a free port, stops the server, then calls it: the operating system refuses the connection, giving a genuine <see cref="HttpRequestException"/>
    /// that no stub can reproduce faithfully.
    /// </summary>
    [TestMethod]
    public async Task ARefusedConnection_IsRetriedThenRethrown()
    {
        var server = new LoopbackServer((_, _, response) => Json(response, 200, "{}"));
        var baseUri = server.BaseUri;
        server.Stop();
        server.Dispose();
        var client = Client(baseUri, o => o.Retry.MaxRetries = 2);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.Get<Widget>("widgets"));
    }

    /// <summary>
    /// 40 concurrent requests over real sockets and one pooled <see cref="HttpClient"/>: each response must match its own request.
    /// </summary>
    [TestMethod]
    public async Task ManyConcurrentCalls_ShareOneHttpClient_WithoutCrossTalk()
    {
        using var server = new LoopbackServer((_, seen, response) => Json(response, 200, $$"""{"name":"{{seen.PathAndQuery}}","size":1}"""));
        var next = 0;
        var client = Client(server.BaseUri, o => o.TokenFactory = () => $"t{Interlocked.Increment(ref next)}");

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => client.Get<Widget>("widgets", i.ToString())));

        Assert.IsTrue(results.All(r => r.IsSuccess));
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 40).Select(i => $"/api/widgets/{i}").ToArray(),
            results.Select(r => r.Result!.Name).ToArray());
        Assert.AreEqual(40, server.Requests.Select(r => r.Authorization).Distinct().Count()); // one fresh token per request
        Assert.IsTrue(server.Requests.All(r => r.UserAgent == "integration/1.0"));
    }

    #region Security over a real connection

    /// <summary>
    /// Observed on a real socket before the fix: a header value holding a line break reached the server as a second header. The call is now refused
    /// before anything is written, so the server never sees it.
    /// </summary>
    [TestMethod]
    public async Task AHeaderValueWithALineBreak_NeverReachesTheServer()
    {
        using var server = new LoopbackServer((_, _, response) => Json(response, 200, "{}"));
        var client = Client(server.BaseUri);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Trace"] = "ok\r\nX-Injected: 1" }));

        Assert.IsEmpty(server.Requests);
    }

    private static Task Redirect(HttpListenerResponse response, string location)
    {
        response.StatusCode = 302;
        response.RedirectLocation = location;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Followed across origins, a redirect keeps every custom header (only <c>Authorization</c> is dropped): <c>X-Api-Key</c> or <c>Cookie</c> would be
    /// handed to whoever the server points at. The default factory does not follow redirects, so the other server is never contacted.
    /// </summary>
    [TestMethod]
    public async Task ARedirectToAnotherOrigin_IsNotFollowedByTheDefaultFactory()
    {
        using var elsewhere = new LoopbackServer((_, _, response) => Json(response, 200, "{}"));
        using var api = new LoopbackServer((_, _, response) => Redirect(response, elsewhere.BaseUri + "/landing"));
        var client = Client(api.BaseUri);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(HttpStatusCode.Found, result.StatusCode);
        Assert.IsEmpty(elsewhere.Requests);
    }

    /// <summary>
    /// A handler that follows redirects (the default of <c>AddHttpClient</c>) cannot be stopped from here, but the answer of an origin nobody declared is
    /// never returned as the API's.
    /// </summary>
    [TestMethod]
    public async Task WhenTheHandlerFollowsRedirects_TheAnswerOfAnotherOriginIsRefused()
    {
        using var elsewhere = new LoopbackServer((_, _, response) => Json(response, 200, """{"name":"evil","size":1}"""));
        using var api = new LoopbackServer((_, _, response) => Redirect(response, elsewhere.BaseUri + "/landing"));
        var client = new TestClient(TestClients.Options(o => { o.BaseUri = api.BaseUri + "/api"; o.TokenFactory = () => "tok"; }),
            new SimpleHttpClientFactory(new SocketsHttpHandler { AllowAutoRedirect = true }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));
    }

    [TestMethod]
    public async Task ARedirectToTheSameOrigin_IsFollowed_WhenTheHandlerAllowsIt()
    {
        using var api = new LoopbackServer((hit, seen, response) => hit == 1
            ? Redirect(response, "/api/moved")
            : Json(response, 200, """{"name":"w","size":3}"""));
        var client = new TestClient(TestClients.Options(o => o.BaseUri = api.BaseUri + "/api"),
            new SimpleHttpClientFactory(new SocketsHttpHandler { AllowAutoRedirect = true }));

        var result = await client.Get<Widget>("widgets");

        Assert.AreEqual(new Widget("w", 3), result.Result);
    }

    /// <summary>
    /// A hostile server that never stops sending: the client gives up at the limit and closes the connection, instead of reading everything and failing at the end.
    /// Failing late would pass a naive test but still fill the memory, so the test also checks how much the server managed to send before the client hung up.
    /// </summary>
    [TestMethod]
    public async Task AnEndlessBody_IsCutOffAtTheLimit_WithoutBeingReadToTheEnd()
    {
        const int chunkSize = 64 * 1024;
        long chunksSent = 0;
        using var server = new LoopbackServer(async (_, _, response) =>
        {
            response.ContentType = "application/json";
            response.SendChunked = true;
            var chunk = new byte[chunkSize];
            try
            {
                for (var i = 0; i < 1000; i++)   // up to 64 MB
                {
                    await response.OutputStream.WriteAsync(chunk);
                    Interlocked.Increment(ref chunksSent);
                }
            }
            catch (Exception)
            {
                // the client closed the connection: exactly what we expect
            }
        });
        var client = Client(server.BaseUri, o => o.MaxResponseBytes = 1_000_000);

        await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(() => client.Get<string>("widgets"));

        // 1 MB of limit plus what the socket buffers hold: nowhere near the 64 MB on offer.
        Assert.IsLessThan(20L * 1024 * 1024 / chunkSize, Interlocked.Read(ref chunksSent), "the client kept reading after the limit");
    }

    /// <summary>
    /// The server sends the status line and the headers, then stalls in the middle of the body. The timeout of an attempt covers the reading of the body
    /// too (the body is read after the headers now), so a stalled body cannot hang the call.
    /// </summary>
    [TestMethod]
    public async Task AServerThatStallsInTheMiddleOfTheBody_TriggersTheAttemptTimeout()
    {
        using var server = new LoopbackServer(async (_, _, response) =>
        {
            response.ContentType = "application/json";
            response.ContentLength64 = 10_000;
            await response.OutputStream.WriteAsync(new byte[100]);
            await response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
        });
        var client = Client(server.BaseUri, o =>
        {
            o.RequestTimeout = TimeSpan.FromMilliseconds(300);
            o.Retry.MaxRetries = 0;
        });

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<string>("widgets"));
    }

    #endregion
}
