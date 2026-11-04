using System.Net;
using System.Text;

namespace SFerrand.Http.Testing;

/// <summary>
/// An <see cref="HttpMessageHandler"/> for tests: it records every request and replays scripted outcomes in order, the last one repeating once the script is
/// exhausted (so <c>Respond(503)</c> alone means "always 503"). Give it to <c>new SimpleHttpClientFactory(handler)</c>, or to
/// <c>ConfigurePrimaryHttpMessageHandler</c>, to test an API client end to end without a network.
/// </summary>
/// <example>
/// <code>
/// var handler = new StubHandler()
///     .Respond(HttpStatusCode.ServiceUnavailable)            // first attempt fails...
///     .Respond(HttpStatusCode.OK, """{"name":"w","size":3}"""); // ...the retry succeeds
/// var client = new WidgetsClient(Options.Create(new HttpClientOptions { BaseUri = "https://api.test" }), new SimpleHttpClientFactory(handler), null);
///
/// var result = await client.GetAsync("42");
///
/// Assert.AreEqual(2, handler.Requests.Count);
/// Assert.AreEqual("Bearer tok", handler.Requests[1].Header("Authorization"));
/// </code>
/// </example>
/// <remarks>
/// With no script at all it answers <c>200 OK</c> with no body. It is safe to use from several threads: recording and scripting are serialized. Disposing a
/// factory that was given the handler does not dispose the handler; <see cref="IsDisposed"/> says whether you did.
/// </remarks>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _gate = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _last;

    /// <summary>The requests received so far, oldest first. A snapshot: it does not change while you read it.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToArray();
        }
    }

    /// <summary>Whether the handler itself was disposed (a factory must not dispose a handler it was given).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Scripts a response.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The body, sent as <c>application/json</c> (UTF-8); <see langword="null"/> sends none.</param>
    /// <param name="configure">Adjusts the response: headers (<c>Retry-After</c>, <c>Location</c>, <c>Link</c>...), another content, <c>RequestMessage</c>...</param>
    /// <returns>This handler, to chain.</returns>
    public StubHandler Respond(HttpStatusCode status, string? body = null, Action<HttpResponseMessage>? configure = null)
        => Script((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    /// <summary>Scripts a response computed from the request.</summary>
    /// <param name="respond">Builds the response from the request it answers. The request is still readable at that point.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return Script((request, _) => Task.FromResult(respond(request)));
    }

    /// <summary>Scripts a transport failure: the call throws <paramref name="exception"/>, like a refused or reset connection.</summary>
    /// <param name="exception">The exception to throw, usually an <see cref="HttpRequestException"/>.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHandler Throw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Script((_, _) => Task.FromException<HttpResponseMessage>(exception));
    }

    /// <summary>Scripts a server that never answers: the call only ends through a timeout or a cancellation.</summary>
    /// <returns>This handler, to chain.</returns>
    public StubHandler Hang() => Script(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    });

    private StubHandler Script(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (_gate)
            _script.Enqueue(step);
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = header.Value.ToArray();
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? step;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

            if (_script.Count > 0)
                _last = _script.Dequeue();
            step = _last;
        }

        return step is null
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : await step(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
