using System.Diagnostics;
using System.Net;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// The overall deadline of a call, the default <c>Accept</c> header and idempotency keys (which make <c>POST</c> and <c>PATCH</c> safe to retry).
/// </summary>
[TestClass]
public class ResilienceTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    #region Total timeout

    [TestMethod]
    public async Task TheTotalTimeout_BoundsTheWholeCall_AndIsReportedAsATimeout()
    {
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler, o =>
        {
            o.RequestTimeout = TimeSpan.FromSeconds(30);   // the attempt alone would wait far longer
            o.TotalTimeout = TimeSpan.FromMilliseconds(150);
        });
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<Widget>("widgets"));

        Assert.IsLessThan(5_000.0, clock.Elapsed.TotalMilliseconds);
        StringAssert.Contains(ex.Message, "total timeout");
    }

    /// <summary>The deadline also covers the wait between two attempts: a long backoff must not outlive it.</summary>
    [TestMethod]
    public async Task TheTotalTimeout_AlsoCoversTheWaitBeforeARetry()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable);
        var client = TestClients.Create(handler, o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromMinutes(1);
            o.Retry.MaxDelay = TimeSpan.FromMinutes(1);
            o.TotalTimeout = TimeSpan.FromMilliseconds(200);
        });
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<Widget>("widgets"));

        Assert.IsLessThan(10_000.0, clock.Elapsed.TotalMilliseconds);
        StringAssert.Contains(ex.Message, "waiting to retry");
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task TheCallersCancellation_IsStillACancellation_NotATimeout()
    {
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler, o => o.TotalTimeout = TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => client.Get<Widget>("widgets", ct: cts.Token));

        Assert.IsNotInstanceOfType<TimeoutException>(ex);
    }

    [TestMethod]
    public async Task ACallThatFinishesInTime_IsUnaffected()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TotalTimeout = TimeSpan.FromSeconds(30));

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task WithoutATotalTimeout_ThereIsNoOverallDeadline()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TotalTimeout = null);

        Assert.IsTrue((await client.Get<Widget>("widgets")).IsSuccess);
    }

    [TestMethod]
    public async Task AnInfiniteTotalTimeout_MeansNoDeadline()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TotalTimeout = Timeout.InfiniteTimeSpan);

        Assert.IsTrue((await client.Get<Widget>("widgets")).IsSuccess);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public void ANonPositiveTotalTimeout_IsRefusedWhenTheClientIsBuilt(int seconds)
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.TotalTimeout = TimeSpan.FromSeconds(seconds)));

    #endregion

    #region Accept

    [TestMethod]
    public async Task Accept_DefaultsToJson()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        await client.Get<Widget>("widgets");

        Assert.AreEqual("application/json", handler.Requests.Single().Header("Accept"));
    }

    [TestMethod]
    public async Task Accept_CanBeChanged_Removed_AndOverriddenPerCall()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);

        await TestClients.Create(handler, o => o.Accept = "application/vnd.api+json").Get<Widget>("widgets");
        await TestClients.Create(handler, o => o.Accept = null).Get<Widget>("widgets");
        await TestClients.Create(handler).Get<Widget>("widgets", headers: new Dictionary<string, string> { ["Accept"] = "text/plain" });
        await TestClients.Create(handler, o => o.Headers["Accept"] = () => "text/csv").Get<Widget>("widgets");

        Assert.AreEqual("application/vnd.api+json", handler.Requests[0].Header("Accept"));
        Assert.IsNull(handler.Requests[1].Header("Accept"));
        Assert.AreEqual("text/plain", handler.Requests[2].Header("Accept"));
        Assert.AreEqual("text/csv", handler.Requests[3].Header("Accept"));
    }

    [TestMethod]
    public void AnAcceptValueWithAControlCharacter_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.Accept = "text/plain\r\nX-Injected: 1"));

    #endregion

    #region Idempotency keys

    private static (TestClient Client, StubHandler Handler) WithKeys(Action<HttpClientOptions>? configure = null, params HttpStatusCode[] statuses)
    {
        var handler = new StubHandler();
        foreach (var status in statuses)
            handler.Respond(status, status == HttpStatusCode.OK ? WidgetJson : null);
        return (TestClients.Create(handler, o => { o.Retry.IdempotencyKeyHeader = "Idempotency-Key"; configure?.Invoke(o); }), handler);
    }

    /// <summary>The whole point: the replay carries the same key as the first attempt, so the server can recognize it.</summary>
    [TestMethod]
    public async Task APost_WithAnIdempotencyKey_IsRetried_WithTheSameKeyOnEveryAttempt()
    {
        var (client, handler) = WithKeys(null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.OK);

        var result = await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, handler.Requests);
        var keys = handler.Requests.Select(r => r.Header("Idempotency-Key")).ToList();
        Assert.IsNotNull(keys[0]);
        Assert.AreEqual(1, keys.Distinct().Count());
        Assert.IsTrue(Guid.TryParse(keys[0], out _));
    }

    [TestMethod]
    public async Task EachCall_GetsItsOwnKey()
    {
        var (client, handler) = WithKeys(null, HttpStatusCode.OK);

        await client.Post<Widget>("widgets", new Widget("w", 1));
        await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreNotEqual(handler.Requests[0].Header("Idempotency-Key"), handler.Requests[1].Header("Idempotency-Key"));
    }

    [TestMethod]
    public async Task APatch_IsKeyedAndRetriedToo()
    {
        var (client, handler) = WithKeys(null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        await client.Patch<Widget>("widgets", new Widget("w", 1), "42");

        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(handler.Requests[0].Header("Idempotency-Key"), handler.Requests[1].Header("Idempotency-Key"));
    }

    [TestMethod]
    public async Task OtherMethods_DoNotGetAKey()
    {
        var (client, handler) = WithKeys(null, HttpStatusCode.OK);

        await client.Get<Widget>("widgets");
        await client.Put<Widget>("widgets", new Widget("w", 1), "42");
        await client.Delete("widgets", "42");

        Assert.IsTrue(handler.Requests.All(r => r.Header("Idempotency-Key") is null));
    }

    [TestMethod]
    public async Task AKeySuppliedByTheCaller_IsUsedAsIs_AndTheCallIsStillRetried()
    {
        var (client, handler) = WithKeys(null, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        await client.Post<Widget>("widgets", new Widget("w", 1), new Dictionary<string, string> { ["idempotency-key"] = "my-own-key" });

        Assert.HasCount(2, handler.Requests);
        Assert.IsTrue(handler.Requests.All(r => r.Header("Idempotency-Key") == "my-own-key"));
    }

    [TestMethod]
    public async Task WithoutTheOption_APostIsNotKeyedNorRetried()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
        Assert.IsNull(handler.Requests[0].Header("Idempotency-Key"));
    }

    [TestMethod]
    public async Task TheKeyFactory_IsUsed_OncePerCall()
    {
        var calls = 0;
        var (client, handler) = WithKeys(o => o.Retry.IdempotencyKeyFactory = () => $"key-{++calls}", HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(1, calls);
        Assert.IsTrue(handler.Requests.All(r => r.Header("Idempotency-Key") == "key-1"));
    }

    [TestMethod]
    public async Task AKeyFromAFactory_WithAControlCharacter_IsRefusedBeforeSending()
    {
        var (client, handler) = WithKeys(o => o.Retry.IdempotencyKeyFactory = () => "a\r\nb", HttpStatusCode.OK);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Post<Widget>("widgets", new Widget("w", 1)));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public void AnInvalidKeyHeaderName_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.Retry.IdempotencyKeyHeader = "Bad Name"));

    [TestMethod]
    public async Task AKeyedPost_IsKeptAcrossARedirectOnTheSameOrigin()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.TemporaryRedirect, null, r => r.Headers.Location = new Uri("/v1/moved", UriKind.Relative))
            .Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.Retry.IdempotencyKeyHeader = "Idempotency-Key");

        await client.Post<Widget>("widgets", new Widget("w", 1));

        Assert.AreEqual(handler.Requests[0].Header("Idempotency-Key"), handler.Requests[1].Header("Idempotency-Key"));
    }

    #endregion
}
