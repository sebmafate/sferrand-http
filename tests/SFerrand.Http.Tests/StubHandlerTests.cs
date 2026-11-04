using System.Net;
using System.Text;
using SFerrand.Http.Testing;

namespace SFerrand.Http.Tests;

/// <summary>The published test double: its behaviour is a contract for the people who test their own clients with it.</summary>
[TestClass]
public class StubHandlerTests
{
    private static HttpClient ClientFor(StubHandler handler) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };

    [TestMethod]
    public async Task WithoutAScript_ItAnswers200WithNoBody()
    {
        using var client = ClientFor(new StubHandler());

        using var response = await client.GetAsync("x");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A step given no body answers without a <c>Content-Type</c> (a 204 does not announce JSON it does not carry).</summary>
    [TestMethod]
    public async Task AStepWithoutABody_HasNoContentType()
    {
        using var client = ClientFor(new StubHandler().Respond(HttpStatusCode.NoContent));

        using var response = await client.GetAsync("x");

        Assert.IsNull(response.Content.Headers.ContentType);
    }

    [TestMethod]
    public async Task TheScriptIsReplayedInOrder_AndTheLastStepRepeats()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.Created).Respond(HttpStatusCode.Accepted);
        using var client = ClientFor(handler);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await client.GetAsync("x")).StatusCode);

        CollectionAssert.AreEqual(new[] { HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted }, statuses);
    }

    [TestMethod]
    public async Task ABody_IsSentAsUtf8Json_AndTheResponseCanBeAdjusted()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"café":1}""", r => r.Headers.TryAddWithoutValidation("X-Trace", "abc"));
        using var client = ClientFor(handler);

        using var response = await client.GetAsync("x");

        Assert.AreEqual("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.AreEqual("""{"café":1}""", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("abc", response.Headers.GetValues("X-Trace").Single());
    }

    [TestMethod]
    public async Task AResponseCanBeComputedFromTheRequest()
    {
        var handler = new StubHandler().Respond(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.PathAndQuery) });
        using var client = ClientFor(handler);

        Assert.AreEqual("/a/b?c=1", await client.GetStringAsync("a/b?c=1"));
    }

    [TestMethod]
    public async Task AFailure_IsThrownToTheCaller()
    {
        var handler = new StubHandler().Throw(new HttpRequestException("connection reset"));
        using var client = ClientFor(handler);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("x"));

        Assert.AreEqual("connection reset", ex.Message);
        Assert.HasCount(1, handler.Requests);   // the request is recorded even though it failed
    }

    [TestMethod]
    public async Task AHang_EndsOnlyWithACancellation()
    {
        var handler = new StubHandler().Hang();
        using var client = ClientFor(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("x", cts.Token));
    }

    [TestMethod]
    public async Task Requests_RecordTheMethod_TheAddress_AllTheHeaders_AndTheBody()
    {
        var handler = new StubHandler();
        using var client = ClientFor(handler);
        using var request = new HttpRequestMessage(HttpMethod.Put, "items/42?x=1") { Content = new StringContent("héllo", Encoding.UTF8, "text/plain") };
        request.Headers.TryAddWithoutValidation("X-One", "1");
        request.Headers.TryAddWithoutValidation("X-Many", new[] { "a", "b" });

        await client.SendAsync(request);

        var seen = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Put, seen.Method);
        Assert.AreEqual("https://api.test/items/42?x=1", seen.Uri.AbsoluteUri);
        Assert.AreEqual("héllo", seen.Body);
        Assert.AreEqual("1", seen.Header("x-one"));            // the name is case-insensitive
        Assert.AreEqual("a,b", seen.Header("X-Many"));          // several values are joined
        StringAssert.StartsWith(seen.Header("Content-Type")!, "text/plain");   // content headers are included
        Assert.IsNull(seen.Header("X-Absent"));
    }

    [TestMethod]
    public async Task ARequestWithoutABody_HasANullBody()
    {
        var handler = new StubHandler();

        await ClientFor(handler).GetAsync("x");

        Assert.IsNull(handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task Requests_IsASnapshot()
    {
        var handler = new StubHandler();
        using var client = ClientFor(handler);
        await client.GetAsync("a");

        var before = handler.Requests;
        await client.GetAsync("b");

        Assert.HasCount(1, before);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ManyConcurrentCalls_AreAllRecorded()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK);
        using var client = ClientFor(handler);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => client.GetAsync($"x/{i}")));

        Assert.HasCount(100, handler.Requests);
        Assert.HasCount(100, handler.Requests.Select(r => r.Uri.AbsolutePath).Distinct().ToList());
    }

    [TestMethod]
    public void IsDisposed_ReportsTheDisposal()
    {
        var handler = new StubHandler();
        Assert.IsFalse(handler.IsDisposed);

        handler.Dispose();

        Assert.IsTrue(handler.IsDisposed);
    }

    [TestMethod]
    public void NullArguments_AreRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new StubHandler().Respond((Func<HttpRequestMessage, HttpResponseMessage>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new StubHandler().Throw(null!));
    }
}
