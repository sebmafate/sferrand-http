using System.Net;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// The client follows redirects itself (the handler must not): every hop is checked like a call, credentials stop at the first change of origin, and the method
/// and body follow the rules of the status code. See "Redirects" in the README.
/// </summary>
[TestClass]
public class RedirectTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    private static StubHandler Redirecting(HttpStatusCode status, string location, params StubStep[] then)
    {
        var handler = new StubHandler().Respond(status, null, r => r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute));
        foreach (var step in then)
            step(handler);
        return handler;
    }

    private delegate void StubStep(StubHandler handler);

    private static StubStep Ok(string body = WidgetJson) => h => h.Respond(HttpStatusCode.OK, body);

    private static StubStep Redirect(HttpStatusCode status, string location) => h => h.Respond(status, null, r => r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute));

    private static TestClient Client(StubHandler handler, Action<HttpClientOptions>? configure = null, Microsoft.Extensions.Logging.ILogger? logger = null)
        => TestClients.Create(handler, o => { o.TokenFactory = () => "tok"; o.Headers["X-Client"] = () => "me"; configure?.Invoke(o); }, logger);

    #region Following

    [TestMethod]
    [DataRow("/v1/moved", "https://api.test/v1/moved")]                  // relative to the origin
    [DataRow("moved", "https://api.test/v1/moved")]                      // relative to the requested path
    [DataRow("https://api.test/v1/elsewhere", "https://api.test/v1/elsewhere")]
    [DataRow("/v1/moved?page=2#frag", "https://api.test/v1/moved?page=2")]   // the fragment is never sent
    public async Task ARedirectOnTheSameOrigin_IsFollowed_WithItsCredentials(string location, string expected)
    {
        var handler = Redirecting(HttpStatusCode.Found, location, Ok());
        var client = Client(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.AreEqual(new Widget("w", 3), result.Result);
        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(expected, handler.Requests[1].Uri.AbsoluteUri);
        Assert.AreEqual("Bearer tok", handler.Requests[1].Header("Authorization"));
        Assert.AreEqual("me", handler.Requests[1].Header("X-Client"));
    }

    [TestMethod]
    public async Task ARelativeLocation_IsResolvedAgainstTheRequestedUrl_NotTheBase()
    {
        var handler = Redirecting(HttpStatusCode.Found, "other", Ok());
        var client = Client(handler);

        await client.Get<Widget>("widgets/42");

        Assert.AreEqual("https://api.test/v1/widgets/other", handler.Requests[1].Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task AChainOfRedirects_IsFollowedHopByHop()
    {
        var handler = Redirecting(HttpStatusCode.MovedPermanently, "/v1/a", Redirect(HttpStatusCode.Found, "/v1/b"), Redirect(HttpStatusCode.SeeOther, "/v1/c"), Ok());
        var client = Client(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "/v1/widgets", "/v1/a", "/v1/b", "/v1/c" }, handler.Requests.Select(r => r.Uri.AbsolutePath).ToArray());
    }

    #endregion

    #region Method and body

    [TestMethod]
    [DataRow(HttpStatusCode.MovedPermanently)]
    [DataRow(HttpStatusCode.Found)]
    public async Task A301Or302_TurnsAPostIntoAGet_WithoutBody(HttpStatusCode status)
    {
        var handler = Redirecting(status, "/v1/moved", Ok());
        var client = Client(handler);

        await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.IsNull(handler.Requests[1].Body);
    }

    [TestMethod]
    public async Task A301Or302_KeepsAPutAsItIs()
    {
        var handler = Redirecting(HttpStatusCode.Found, "/v1/moved", Ok());
        var client = Client(handler);

        await client.Put<Widget>("widgets", new Widget("w", 1), "42");

        Assert.AreEqual(HttpMethod.Put, handler.Requests[1].Method);
        Assert.AreEqual("""{"name":"w","size":1}""", handler.Requests[1].Body);
    }

    [TestMethod]
    public async Task A303_TurnsAnyMethodIntoAGet_ExceptHead()
    {
        var handler = Redirecting(HttpStatusCode.SeeOther, "/v1/result", Ok(), Redirect(HttpStatusCode.SeeOther, "/v1/other"), Ok());
        var client = Client(handler);

        await client.Put<Widget>("widgets", new Widget("w", 1), "42");
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.IsNull(handler.Requests[1].Body);

        var head = new StubHandler().Respond(HttpStatusCode.SeeOther, null, r => r.Headers.Location = new Uri("/v1/x", UriKind.Relative)).Respond(HttpStatusCode.OK);
        await Client(head).Head("widgets");
        Assert.AreEqual(HttpMethod.Head, head.Requests[1].Method);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TemporaryRedirect)]
    [DataRow(HttpStatusCode.PermanentRedirect)]
    public async Task A307Or308_KeepsTheMethodAndReplaysTheBody(HttpStatusCode status)
    {
        var handler = Redirecting(status, "/v1/moved", Ok());
        var client = Client(handler, o => o.Retry.RetryNonIdempotentMethods = false);

        await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        Assert.AreEqual("""{"name":"w","size":1}""", handler.Requests[1].Body);
        Assert.AreEqual("application/json; charset=utf-8", handler.Requests[1].Header("Content-Type"));
    }

    /// <summary>A body is never re-sent to another origin: the redirect comes back as the result, for the caller to decide.</summary>
    [TestMethod]
    [DataRow(HttpStatusCode.TemporaryRedirect)]
    [DataRow(HttpStatusCode.PermanentRedirect)]
    public async Task A307Or308WithABody_ToAnotherOrigin_IsReturnedNotFollowed(HttpStatusCode status)
    {
        var handler = Redirecting(status, "https://files.test/upload", Ok());
        var client = Client(handler, o => o.TrustedOrigins.Add("https://files.test"));

        var result = await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(status, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task A307WithoutABody_ToAnotherDeclaredOrigin_IsFollowed()
    {
        var handler = Redirecting(HttpStatusCode.TemporaryRedirect, "https://files.test/x", Ok());
        var client = Client(handler, o => o.TrustedOrigins.Add("https://files.test"));

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
    }

    #endregion

    #region Credentials

    /// <summary>The header names that matter: whatever the application sent to the first origin must not follow the request to the second one.</summary>
    [TestMethod]
    public async Task CredentialsAndCustomHeaders_StopAtTheFirstChangeOfOrigin()
    {
        var handler = Redirecting(HttpStatusCode.Found, "https://files.test/x", Ok());
        var client = Client(handler, o =>
        {
            o.TrustedOrigins.Add("https://files.test");
            o.Retry.IdempotencyKeyHeader = "Idempotency-Key";
        });

        await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Per-Call"] = "secret" });

        var first = handler.Requests[0];
        Assert.AreEqual("Bearer tok", first.Header("Authorization"));
        Assert.AreEqual("secret", first.Header("X-Per-Call"));

        var second = handler.Requests[1];
        Assert.IsNull(second.Header("Authorization"));
        Assert.IsNull(second.Header("X-Client"));
        Assert.IsNull(second.Header("X-Per-Call"));
        Assert.IsNotNull(second.Header("User-Agent"));
        Assert.AreEqual("application/json", second.Header("Accept"));
    }

    /// <summary>Leaving an origin is final: coming back to the first one later in the chain does not bring the credentials back.</summary>
    [TestMethod]
    public async Task CredentialsDoNotComeBack_WhenTheChainReturnsToTheFirstOrigin()
    {
        var handler = Redirecting(HttpStatusCode.Found, "https://files.test/x", Redirect(HttpStatusCode.Found, "https://api.test/v1/back"), Ok());
        var client = Client(handler, o => o.TrustedOrigins.Add("https://files.test"));

        await client.Get<Widget>("widgets");

        Assert.HasCount(3, handler.Requests);
        Assert.AreEqual("Bearer tok", handler.Requests[0].Header("Authorization"));
        Assert.IsNull(handler.Requests[2].Header("Authorization"));
        Assert.IsNull(handler.Requests[2].Header("X-Client"));
    }

    #endregion

    #region Refusals

    [TestMethod]
    public async Task ARedirectToAnUndeclaredOrigin_IsRefused_BeforeAnythingIsSentThere()
    {
        var handler = Redirecting(HttpStatusCode.Found, "https://evil.test/steal", Ok());
        var client = Client(handler);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.HasCount(1, handler.Requests);
        StringAssert.Contains(ex.Message, "evil.test");
    }

    [TestMethod]
    [DataRow("ftp://api.test/x")]
    [DataRow("file:///etc/passwd")]
    public async Task ARedirectToANonHttpScheme_IsRefused(string location)
    {
        var handler = Redirecting(HttpStatusCode.Found, location, Ok());
        var client = Client(handler);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task ARedirectToAddressWithCredentials_IsRefused_WithoutEchoingThem()
    {
        var handler = Redirecting(HttpStatusCode.Found, "https://admin:hunter2@api.test/v1/x", Ok());
        var client = Client(handler);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [TestMethod]
    public async Task ADowngradeToClearText_IsRefused_EvenToADeclaredLoopbackOrigin()
    {
        var handler = Redirecting(HttpStatusCode.Found, "http://localhost:5000/x", Ok());
        var client = Client(handler, o => o.TrustedOrigins.Add("http://localhost:5000"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        StringAssert.Contains(ex.Message, "downgrade");
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task ARefusedRedirect_IsNotRetried()
    {
        var handler = Redirecting(HttpStatusCode.Found, "https://evil.test/x");
        var client = Client(handler, o => o.Retry.MaxRetries = 3);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.HasCount(1, handler.Requests);
    }

    #endregion

    #region Limits and non-redirects

    [TestMethod]
    public async Task WhenTheLimitIsReached_TheLastRedirectIsReturnedAsTheResult()
    {
        var handler = Redirecting(HttpStatusCode.Found, "/v1/loop");   // the same answer forever
        var client = Client(handler, o => o.MaxRedirects = 2);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(HttpStatusCode.Found, result.StatusCode);
        Assert.HasCount(3, handler.Requests);   // the request plus two hops
    }

    [TestMethod]
    public async Task WithZeroMaxRedirects_EveryRedirectIsReturnedAsTheResult()
    {
        var handler = Redirecting(HttpStatusCode.Found, "/v1/moved", Ok());
        var client = Client(handler, o => o.MaxRedirects = 0);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(HttpStatusCode.Found, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public void ANegativeMaxRedirects_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.MaxRedirects = -1));

    [TestMethod]
    public async Task ARedirectWithoutALocation_IsReturnedAsTheResult()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.Found).Respond(HttpStatusCode.OK, WidgetJson);
        var client = Client(handler);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(HttpStatusCode.Found, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.MultipleChoices)]
    [DataRow(HttpStatusCode.NotModified)]
    [DataRow(HttpStatusCode.UseProxy)]
    public async Task StatusesThatAreNotRedirectsToFollow_AreReturnedAsTheResult(HttpStatusCode status)
    {
        var handler = new StubHandler().Respond(status, null, r => r.Headers.Location = new Uri("/v1/x", UriKind.Relative));
        var client = Client(handler);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(status, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task TheLogs_MentionTheRedirect_WithoutTheQueryString()
    {
        var logger = new ListLogger();
        var handler = Redirecting(HttpStatusCode.Found, "/v1/moved?token=s3cr3t", Ok());
        var client = Client(handler, logger: logger);

        await client.Get<Widget>("widgets", parameters: new Dictionary<string, string> { ["api_key"] = "also-secret" });

        Assert.IsTrue(logger.Messages.Any(m => m.Contains("redirected")));
        Assert.IsTrue(logger.Messages.All(m => !m.Contains("s3cr3t") && !m.Contains("also-secret")));
    }

    #endregion
}
