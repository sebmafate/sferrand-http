namespace SFerrand.Http;

/// <summary>
/// Names to subscribe to, to observe the calls made by the clients of this library with OpenTelemetry or <c>dotnet-counters</c>.
/// </summary>
/// <example>
/// <code>
/// services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(SFerrandHttpTelemetry.Name))
///     .WithMetrics(m => m.AddMeter(SFerrandHttpTelemetry.Name));
/// </code>
/// </example>
/// <remarks>
/// A span covers a whole <b>logical call</b>, retries and redirects included; the HTTP stack records its own span for every request underneath,
/// so the retries show up as children. Tags: <c>http.request.method</c>, <c>server.address</c>, <c>server.port</c>, <c>url.full</c> (scheme, host and path
/// only: never the query string or user info), <c>http.response.status_code</c>, <c>http.request.resend_count</c> and <c>error.type</c>.
/// </remarks>
public static class SFerrandHttpTelemetry
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> and of the <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string Name = "SFerrand.Http";

    /// <summary>Histogram, in seconds: the duration of a logical call, retries and redirects included.</summary>
    public const string CallDurationMetric = "sferrand.http.client.call.duration";

    /// <summary>Counter: attempts made again after a transient failure.</summary>
    public const string RetriesMetric = "sferrand.http.client.retries";

    /// <summary>Counter: redirects followed by the client.</summary>
    public const string RedirectsMetric = "sferrand.http.client.redirects";
}
