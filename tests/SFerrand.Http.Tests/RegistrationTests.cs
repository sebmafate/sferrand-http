using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SFerrand.Http.Tests.Support;
using SFerrand.Http.Testing;

namespace SFerrand.Http.Tests;

// Source-generated metadata: the way a trimmed or native-AOT application tells the library how to read and write its types.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(IEnumerable<Widget>))]   // what ListAsync<Widget> reads: a context must declare the exact type
internal sealed partial class WidgetsJsonContext : JsonSerializerContext;

/// <summary>Registration with <c>AddSFerrandHttpClient</c>, the options validator (including a failure at start-up) and use with a source-generated JSON context.</summary>
[TestClass]
public class RegistrationTests
{
    private sealed class WidgetsClient(IOptions<HttpClientOptions> options, IHttpClientFactory factory, ILogger<WidgetsClient> logger) : HttpClientBase(options, factory, logger)
    {
        public Task<HttpResult<Widget>> Get(string id) => GetAsync<Widget>("widgets", id);

        public Task<HttpResult<Widget>> Create(Widget widget) => PostAsync<Widget>("widgets", widget);

        public string? HttpClientName => null;
    }

    private sealed class OtherClient(IOptions<HttpClientOptions> options, IHttpClientFactory factory, ILogger<OtherClient> logger) : HttpClientBase(options, factory, logger)
    {
        public Task<HttpResult<string>> Ping() => GetAsync<string>("ping");
    }

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        register(services);
        return services.BuildServiceProvider();
    }

    #region Registration

    [TestMethod]
    public async Task ARegisteredClient_IsResolved_AndWorks()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");
        using var provider = Build(services => services
            .AddSFerrandHttpClient<WidgetsClient>(o => { o.BaseUri = "https://api.test/v1"; o.TokenFactory = () => "tok"; })
            .ConfigurePrimaryHttpMessageHandler(() => handler));

        var result = await provider.GetRequiredService<WidgetsClient>().Get("42");

        Assert.AreEqual(new Widget("w", 3), result.Result);
        Assert.AreEqual("https://api.test/v1/widgets/42", handler.Requests.Single().Uri.AbsoluteUri);
        Assert.AreEqual("Bearer tok", handler.Requests.Single().Header("Authorization"));
    }

    [TestMethod]
    public async Task EachClient_HasItsOwnOptionsAndItsOwnHttpClient()
    {
        var first = new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");
        var second = new StubHandler().Respond(HttpStatusCode.OK, "pong");
        using var provider = Build(services =>
        {
            services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = "https://widgets.test/v1").ConfigurePrimaryHttpMessageHandler(() => first);
            services.AddSFerrandHttpClient<OtherClient>(o => { o.BaseUri = "https://other.test/api"; o.Headers["X-Other"] = () => "yes"; }).ConfigurePrimaryHttpMessageHandler(() => second);
        });

        await provider.GetRequiredService<WidgetsClient>().Get("1");
        await provider.GetRequiredService<OtherClient>().Ping();

        Assert.AreEqual("https://widgets.test/v1/widgets/1", first.Requests.Single().Uri.AbsoluteUri);
        Assert.IsNull(first.Requests.Single().Header("X-Other"));
        Assert.AreEqual("https://other.test/api/ping", second.Requests.Single().Uri.AbsoluteUri);
        Assert.AreEqual("yes", second.Requests.Single().Header("X-Other"));
    }

    [TestMethod]
    public void TheHttpClientName_DefaultsToTheClientType_AndCanBeChosen()
    {
        using var provider = Build(services =>
        {
            services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = "https://a.test");
            services.AddSFerrandHttpClient<OtherClient>(o => { o.BaseUri = "https://b.test"; o.HttpClientName = "mine"; });
        });
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpClientOptions>>();

        Assert.AreEqual(typeof(WidgetsClient).FullName, monitor.Get(typeof(WidgetsClient).FullName).HttpClientName);
        Assert.AreEqual("mine", monitor.Get(typeof(OtherClient).FullName).HttpClientName);
    }

    /// <summary>
    /// The primary handler registered by the extension must not follow redirects: those are followed by the client, which checks every hop. Proved with a real
    /// server that redirects to another origin: if the handler followed it, the second server would be contacted before the client could object.
    /// </summary>
    [TestMethod]
    public async Task TheDefaultHandler_DoesNotFollowRedirects_ByItself()
    {
        var contacted = 0;
        using var elsewhere = new TinyServer(response => { Interlocked.Increment(ref contacted); response.StatusCode = 200; });
        using var api = new TinyServer(response => { response.StatusCode = 302; response.RedirectLocation = elsewhere.Url + "landing"; });
        using var provider = Build(services => services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = api.Url + "v1"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetRequiredService<WidgetsClient>().Get("1"));

        Assert.AreEqual(0, contacted);
    }

    [TestMethod]
    public void NullArguments_AreRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IServiceCollection)null!).AddSFerrandHttpClient<WidgetsClient>(_ => { }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ServiceCollection().AddSFerrandHttpClient<WidgetsClient>(null!));
    }

    private sealed class TinyServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public string Url { get; }

        public TinyServer(Action<HttpListenerResponse> respond)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    respond(context.Response);
                    context.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    #endregion

    #region Validation

    [TestMethod]
    public void ABadConfiguration_IsReportedWhenTheClientIsResolved_WithoutRepeatingSecrets()
    {
        using var provider = Build(services => services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = "https://admin:hunter2@api.test/v1?token=abc123"));

        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => provider.GetRequiredService<WidgetsClient>());

        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("abc123", ex.Message);
    }

    /// <summary>
    /// The point of <c>ValidateOnStart</c>: with the generic host, the start-up validation runs before the application serves anything. The same step is run here.
    /// </summary>
    [TestMethod]
    public void ABadConfiguration_FailsAtStartUp_NotAtTheFirstCall()
    {
        using var provider = Build(services => services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = "http://api.example.com/v1"));

        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        StringAssert.Contains(ex.Message, "clear-text");
    }

    [TestMethod]
    public void AGoodConfiguration_PassesTheStartUpValidation()
    {
        using var provider = Build(services => services.AddSFerrandHttpClient<WidgetsClient>(o => o.BaseUri = "https://api.example.com/v1"));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static ValidateOptionsResult Validate(Action<HttpClientOptions> configure)
    {
        var options = new HttpClientOptions { BaseUri = "https://api.test/v1" };
        configure(options);
        return new HttpClientOptionsValidator().Validate(null, options);
    }

    [TestMethod]
    public void TheValidator_AcceptsDefaultOptions() => Assert.IsTrue(Validate(_ => { }).Succeeded);

    [TestMethod]
    public void TheValidator_ReportsEveryProblem_NotJustTheFirst()
    {
        var result = Validate(o =>
        {
            o.BaseUri = "ftp://nope.test";
            o.MaxRedirects = -1;
            o.MaxResponseBytes = 0;
            o.Retry.MaxRetries = -3;
        });

        Assert.IsTrue(result.Failed);
        Assert.IsGreaterThanOrEqualTo(4, result.Failures!.Count());
    }

    [TestMethod]
    [DataRow("BaseUri", "")]
    [DataRow("BaseUri", "/relative")]
    [DataRow("BaseUri", "ftp://x.test")]
    [DataRow("BaseUri", "https://u:p@x.test")]
    [DataRow("BaseUri", "http://x.test")]
    public void TheValidator_RejectsABadBaseUri(string _, string value)
        => Assert.IsTrue(Validate(o => o.BaseUri = value).Failed);

    [TestMethod]
    public void TheValidator_Rejects_BadOtherAddresses_Headers_Limits_AndDelays()
    {
        Assert.IsTrue(Validate(o => o.GraphQLUri = "http://x.test/graphql").Failed);
        Assert.IsTrue(Validate(o => o.TrustedOrigins.Add("not a url")).Failed);
        Assert.IsTrue(Validate(o => o.Headers["Bad Name"] = () => "v").Failed);
        Assert.IsTrue(Validate(o => o.Headers["Host"] = () => "v").Failed);
        Assert.IsTrue(Validate(o => o.UserAgent = "a\r\nb").Failed);
        Assert.IsTrue(Validate(o => o.Accept = "a\r\nb").Failed);
        Assert.IsTrue(Validate(o => o.Retry.IdempotencyKeyHeader = "Bad Name").Failed);
        Assert.IsTrue(Validate(o => o.MaxDownloadBytes = 0).Failed);
        Assert.IsTrue(Validate(o => o.TotalTimeout = TimeSpan.Zero).Failed);
        Assert.IsTrue(Validate(o => o.RequestTimeout = TimeSpan.FromSeconds(-2)).Failed);
        Assert.IsTrue(Validate(o => o.Retry.BaseDelay = TimeSpan.FromSeconds(-1)).Failed);
        Assert.IsTrue(Validate(o => o.Retry.MaxDelay = TimeSpan.FromSeconds(-1)).Failed);
    }

    [TestMethod]
    public void TheValidator_Accepts_TheValuesThatMeanNoLimit()
    {
        Assert.IsTrue(Validate(o => { o.MaxResponseBytes = null; o.MaxDownloadBytes = null; o.TotalTimeout = null; o.Accept = null; }).Succeeded);
        Assert.IsTrue(Validate(o => { o.RequestTimeout = Timeout.InfiniteTimeSpan; o.TotalTimeout = Timeout.InfiniteTimeSpan; }).Succeeded);
        Assert.IsTrue(Validate(o => { o.BaseUri = "http://localhost:5000"; o.GraphQLUri = "http://127.0.0.1/g"; }).Succeeded);
        Assert.IsTrue(Validate(o => { o.BaseUri = "http://api.internal"; o.AllowInsecureHttp = true; }).Succeeded);
    }

    [TestMethod]
    public void TheValidator_RefusesNullOptions()
        => Assert.ThrowsExactly<ArgumentNullException>(() => new HttpClientOptionsValidator().Validate(null, null!));

    #endregion

    #region Source-generated JSON

    private static TestClient ClientWithContext(StubHandler handler)
        => TestClients.Create(handler, o => o.JsonSerializerOptions.TypeInfoResolver = WidgetsJsonContext.Default);

    [TestMethod]
    public async Task WithASourceGeneratedContext_BodiesAreWrittenAndRead_WithoutReflection()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");
        var client = ClientWithContext(handler);

        var result = await client.Post<Widget>("widgets", new Widget("w", 3));

        Assert.AreEqual(new Widget("w", 3), result.Result);
        Assert.AreEqual("""{"name":"w","size":3}""", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task WithASourceGeneratedContext_AListIsRead()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """[{"name":"a","size":1},{"name":"b","size":2}]""");

        var result = await ClientWithContext(handler).List<Widget>("widgets");

        Assert.HasCount(2, result.Result!.ToList());
    }

    /// <summary>A type the context does not know is an error, not a silent fall back to reflection: that is what keeps a trimmed application correct.</summary>
    [TestMethod]
    public async Task ATypeUnknownToTheContext_IsRefused()
    {
        var client = ClientWithContext(new StubHandler().Respond(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.Post<Widget>("widgets", new { Surprise = 1 }));
    }

    #endregion
}
