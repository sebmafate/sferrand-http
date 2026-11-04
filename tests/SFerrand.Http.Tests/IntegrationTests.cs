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
}
