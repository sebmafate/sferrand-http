using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// Spans and metrics, observed with a real <see cref="ActivityListener"/> and <see cref="MeterListener"/>. The sources are process-wide and the tests run in
/// parallel, so every test calls its own host name and only keeps what concerns it.
/// </summary>
[TestClass]
public class TelemetryTests
{
    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class Observation : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly string _host;

        public List<Activity> Activities { get; } = new();
        public List<Measurement> Measurements { get; } = new();

        public Observation(string host)
        {
            _host = host;
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == SFerrandHttpTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if ((string?)activity.GetTagItem("server.address") == _host)
                        lock (Activities) Activities.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SFerrandHttpTelemetry.Name)
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meters.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if ((string?)map.GetValueOrDefault("server.address") == _host)
                lock (Measurements) Measurements.Add(new Measurement(instrument.Name, value, map));
        }

        public IEnumerable<Measurement> Of(string instrument) => Measurements.Where(m => m.Instrument == instrument);

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    private static TestClient ClientFor(string host, StubHandler handler, Action<HttpClientOptions>? configure = null)
        => TestClients.Create(handler, o => { o.BaseUri = $"https://{host}/v1"; configure?.Invoke(o); });

    [TestMethod]
    public async Task ACall_ProducesOneSpan_WithTheDocumentedTags()
    {
        const string host = "span.telemetry.test";
        using var observation = new Observation(host);
        var client = ClientFor(host, new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}"""));

        await client.Get<Widget>("widgets", "42", new Dictionary<string, string> { ["api_key"] = "s3cr3t" });

        var span = observation.Activities.Single();
        Assert.AreEqual("GET", span.OperationName);
        Assert.AreEqual(ActivityKind.Client, span.Kind);
        Assert.AreEqual("GET", span.GetTagItem("http.request.method"));
        Assert.AreEqual(443, span.GetTagItem("server.port"));
        Assert.AreEqual(200, span.GetTagItem("http.response.status_code"));
        Assert.AreEqual($"https://{host}/v1/widgets/42", span.GetTagItem("url.full"));
        Assert.IsNull(span.GetTagItem("error.type"));
        Assert.IsNull(span.GetTagItem("http.request.resend_count"));
        Assert.AreNotEqual(ActivityStatusCode.Error, span.Status);
    }

    /// <summary>The query string can carry secrets: it is in neither the span nor the metric tags.</summary>
    [TestMethod]
    public async Task NothingSecretIsRecorded()
    {
        const string host = "secrets.telemetry.test";
        using var observation = new Observation(host);
        var client = ClientFor(host, new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}"""));

        await client.Get<Widget>("widgets", parameters: new Dictionary<string, string> { ["access_token"] = "s3cr3t" });

        var everything = string.Join(" ", observation.Activities.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}"))
                         + string.Join(" ", observation.Measurements.SelectMany(m => m.Tags).Select(t => $"{t.Key}={t.Value}"));
        Assert.DoesNotContain("s3cr3t", everything);
        Assert.DoesNotContain("access_token", everything);
    }

    [TestMethod]
    public async Task Retries_AreCountedOnTheSpan_AndInTheRetryCounter()
    {
        const string host = "retries.telemetry.test";
        using var observation = new Observation(host);
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.BadGateway).Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");

        await ClientFor(host, handler).Get<Widget>("widgets");

        var span = observation.Activities.Single();
        Assert.AreEqual(2, span.GetTagItem("http.request.resend_count"));
        Assert.AreEqual(200, span.GetTagItem("http.response.status_code"));
        Assert.AreEqual(2, observation.Of(SFerrandHttpTelemetry.RetriesMetric).Sum(m => m.Value));
        Assert.HasCount(1, observation.Of(SFerrandHttpTelemetry.CallDurationMetric).ToList());   // one logical call, one duration
    }

    [TestMethod]
    public async Task AFailingStatus_MarksTheSpanAndTheMetricAsAnError_WithoutThrowing()
    {
        const string host = "status.telemetry.test";
        using var observation = new Observation(host);

        var result = await ClientFor(host, new StubHandler().Respond(HttpStatusCode.NotFound), o => o.Retry.MaxRetries = 0).Get<Widget>("widgets");

        Assert.IsFalse(result.IsSuccess);
        var span = observation.Activities.Single();
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual("404", span.GetTagItem("error.type"));
        var duration = observation.Of(SFerrandHttpTelemetry.CallDurationMetric).Single();
        Assert.AreEqual("404", duration.Tags["error.type"]);
        Assert.AreEqual(404, duration.Tags["http.response.status_code"]);
    }

    [TestMethod]
    public async Task ATimeout_IsRecordedAsErrorTypeTimeout()
    {
        const string host = "timeout.telemetry.test";
        using var observation = new Observation(host);
        var client = ClientFor(host, new StubHandler().Hang(), o => { o.RequestTimeout = TimeSpan.FromMilliseconds(40); o.Retry.MaxRetries = 0; });

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<Widget>("widgets"));

        Assert.AreEqual("timeout", observation.Activities.Single().GetTagItem("error.type"));
        Assert.AreEqual("timeout", observation.Of(SFerrandHttpTelemetry.CallDurationMetric).Single().Tags["error.type"]);
    }

    [TestMethod]
    public async Task ARefusedCall_IsStillRecorded_WithTheTypeOfTheException()
    {
        const string host = "refused.telemetry.test";
        using var observation = new Observation(host);
        var client = ClientFor(host, new StubHandler().Respond(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Bad"] = "a\r\nb" }));

        // The address was valid, so the span exists, and it records the failure.
        Assert.AreEqual(typeof(ArgumentException).FullName, observation.Activities.Single().GetTagItem("error.type"));
    }

    [TestMethod]
    public async Task Redirects_AreCounted()
    {
        const string host = "redirects.telemetry.test";
        using var observation = new Observation(host);
        var handler = new StubHandler()
            .Respond(HttpStatusCode.Found, null, r => r.Headers.Location = new Uri("/v1/a", UriKind.Relative))
            .Respond(HttpStatusCode.Found, null, r => r.Headers.Location = new Uri("/v1/b", UriKind.Relative))
            .Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");

        await ClientFor(host, handler).Get<Widget>("widgets");

        Assert.AreEqual(2, observation.Of(SFerrandHttpTelemetry.RedirectsMetric).Sum(m => m.Value));
        Assert.HasCount(1, observation.Activities);   // redirects belong to the same logical call
    }

    [TestMethod]
    public async Task TheCallDuration_IsAPositiveNumberOfSeconds()
    {
        const string host = "duration.telemetry.test";
        using var observation = new Observation(host);

        await ClientFor(host, new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}""")).Get<Widget>("widgets");

        var seconds = observation.Of(SFerrandHttpTelemetry.CallDurationMetric).Single().Value;
        Assert.IsGreaterThan(0.0, seconds);
        Assert.IsLessThan(30.0, seconds);
    }

    /// <summary>These names are what users write in their OpenTelemetry configuration: changing one is a breaking change. (Read by reflection: they are constants.)</summary>
    [TestMethod]
    [DataRow("Name", "SFerrand.Http")]
    [DataRow("CallDurationMetric", "sferrand.http.client.call.duration")]
    [DataRow("RetriesMetric", "sferrand.http.client.retries")]
    [DataRow("RedirectsMetric", "sferrand.http.client.redirects")]
    public void TheNamesAreStable(string constant, string expected)
        => Assert.AreEqual(expected, typeof(SFerrandHttpTelemetry).GetField(constant)!.GetRawConstantValue());
}
