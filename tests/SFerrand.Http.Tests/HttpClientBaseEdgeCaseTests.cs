using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// Less common paths of <see cref="HttpClientBase"/>: invalid construction, cancellation versus timeout classification,
/// failing token factories, <c>Retry-After</c> handling with real waiting, custom retry statuses and concurrency.
/// </summary>
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

    /// <summary>
    /// The classification of an <see cref="OperationCanceledException"/> as "timeout" or "cancelled by the caller" depends on the caller's token,
    /// not on whether retries happen to be enabled.
    /// </summary>
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

    /// <summary>
    /// <see cref="Timeout.InfiniteTimeSpan"/> and zero both mean "no attempt timeout": the call is then bounded only by the caller's token.
    /// </summary>
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

    /// <summary>
    /// A failing credential provider must fail the call loudly, never degrade to an anonymous request.
    /// </summary>
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

    /// <summary>
    /// Timing test: the exponential backoff with the test settings would be about 1 ms, so an elapsed time close to one second proves that the
    /// <c>Retry-After</c> value was used. The threshold leaves 100 ms of tolerance for timer granularity.
    /// </summary>
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

    /// <summary>
    /// 100 simultaneous calls through one shared <see cref="HttpClient"/>: each must carry its own freshly computed token, which would not
    /// hold if headers were stored on the client or on shared mutable state.
    /// </summary>
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

    /// <summary>
    /// The body is read after the headers, so a connection dropped in the middle of it is an <see cref="IOException"/> (not an <see cref="HttpRequestException"/>
    /// any more, as when the framework buffered the body). It is the same transient failure and must keep being retried.
    /// </summary>
    [TestMethod]
    public async Task AConnectionDroppedWhileReadingTheBody_IsRetried()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new ThrowingStream()))
            .Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task AConnectionDroppedWhileReadingTheBody_OnEveryAttempt_ThrowsAnHttpRequestException()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new ThrowingStream()));
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 2);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.Get<Widget>("widgets"));

        Assert.IsInstanceOfType<IOException>(ex.InnerException);
        Assert.HasCount(3, handler.Requests);
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
