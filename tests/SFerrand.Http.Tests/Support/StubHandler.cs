using System.Net;
using System.Text;

namespace SFerrand.Http.Tests.Support;

/// <summary>A request as the server saw it: requests are disposed once sent, so everything is copied.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string[]> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records every request and replays scripted responses in order
/// (the last one repeats once the script is exhausted).
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _last;

    public List<RecordedRequest> Requests { get; } = new();

    public StubHandler Respond(HttpStatusCode status, string? body = null, Action<HttpResponseMessage>? configure = null)
        => Script((_, _) =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    public StubHandler Throw(Exception exception) => Script((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers: the call only ends through a timeout or a cancellation.</summary>
    public StubHandler Hang() => Script(async (_, ct) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("unreachable");
    });

    private StubHandler Script(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = header.Value.ToArray();
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        if (_script.Count > 0)
            _last = _script.Dequeue();

        return _last is null
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : await _last(request, cancellationToken);
    }
}
