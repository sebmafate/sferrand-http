using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

[TestClass]
public class HttpClientBaseEdgeCaseTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    private sealed class NullOptions : IOptions<HttpClientOptions>
    {
        public HttpClientOptions Value => null!;
    }

    private sealed class ThrowingClient(IOptions<HttpClientOptions> options) : HttpClientBase(options, new SimpleHttpClientFactory(), null);

    [TestMethod]
    public void Constructor_RejectsAnOptionsWrapperWithoutValue()
        => Assert.ThrowsExactly<ArgumentException>(() => new ThrowingClient(new NullOptions()));

    [TestMethod]
    public void Constructor_RejectsNullOptionsWrapper()
        => Assert.ThrowsExactly<ArgumentNullException>(() => new ThrowingClient(null!));

    [TestMethod]
    public async Task CallerCancellation_IsNeverReportedAsATimeout_EvenWhenNothingWouldBeRetried()
    {
        // POST is not retried by default, so there is no retry wait that could mask a mis-classified cancellation.
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => client.Post<Widget>("widgets", new Widget("w", 1), ct: cts.Token));

        Assert.IsNotInstanceOfType<TimeoutException>(ex);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task CallerCancellation_WithRetriesDisabled_IsStillACancellation()
    {
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Get<Widget>("widgets", ct: cts.Token));
    }

    [TestMethod]
    public async Task InfiniteRequestTimeout_DisablesTheAttemptTimeout()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.RequestTimeout = Timeout.InfiniteTimeSpan);

        Assert.IsTrue((await client.Get<Widget>("widgets")).IsSuccess);
    }

    [TestMethod]
    public async Task ZeroRequestTimeout_DisablesTheAttemptTimeout()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.RequestTimeout = TimeSpan.Zero);

        Assert.IsTrue((await client.Get<Widget>("widgets")).IsSuccess);
    }

    [TestMethod]
    public async Task ATokenFactoryThatThrows_PropagatesAndSendsNothing()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TokenFactory = () => throw new InvalidOperationException("no token"));

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.AreEqual("no token", ex.Message);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task AnAsyncTokenFactoryThatThrows_PropagatesAndSendsNothing()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.AsyncTokenFactory = _ => throw new InvalidOperationException("no async token"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task RetryAfterInSeconds_IsActuallyWaitedFor()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.TooManyRequests, null, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
            .Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.Retry.MaxDelay = TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        var result = await client.Get<Widget>("widgets");
        clock.Stop();

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
        Assert.IsGreaterThanOrEqualTo(900.0, clock.Elapsed.TotalMilliseconds); // backoff alone would have been ~1 ms
    }

    [TestMethod]
    public async Task RetryAfterAsADate_FarInTheFuture_ReturnsTheResponseWithoutWaiting()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable, null, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(2)));
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task CustomRetryStatusCodes_AreHonoured()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.Conflict).Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.Retry.RetryStatusCodes.Add(HttpStatusCode.Conflict));

        Assert.IsTrue((await client.Get<Widget>("widgets")).IsSuccess);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ConcurrentCalls_OnASharedFactoryClient_DoNotInterfere()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var next = 0;
        var client = TestClients.Create(handler, o => o.TokenFactory = () => $"t{Interlocked.Increment(ref next)}");

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => client.Get<Widget>("widgets")));

        Assert.IsTrue(results.All(r => r.IsSuccess && r.Result == new Widget("w", 3)));
        Assert.HasCount(100, handler.Requests);
        Assert.IsTrue(handler.Requests.All(r => r.Header("Authorization")!.StartsWith("Bearer t")));
        Assert.HasCount(100, handler.Requests.Select(r => r.Header("Authorization")).Distinct().ToList());
    }
}
