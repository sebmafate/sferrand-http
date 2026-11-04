using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace SFerrand.Http.Internal;

/// <summary>The trace source and the instruments of the library (see <see cref="SFerrandHttpTelemetry"/>).</summary>
internal static class HttpClientTelemetry
{
    private static readonly string Version = typeof(HttpClientTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static readonly ActivitySource Source = new(SFerrandHttpTelemetry.Name, Version);

    private static readonly Meter Meter = new(SFerrandHttpTelemetry.Name, Version);

    public static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        SFerrandHttpTelemetry.CallDurationMetric, unit: "s",
        description: "Duration of a logical call: every attempt, retry wait and redirect included.");

    public static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        SFerrandHttpTelemetry.RetriesMetric, unit: "{retry}", description: "Attempts made again after a transient failure.");

    public static readonly Counter<long> Redirects = Meter.CreateCounter<long>(
        SFerrandHttpTelemetry.RedirectsMetric, unit: "{redirect}", description: "Redirects followed by the client.");

    /// <summary>Starts the span of a logical call. Addresses are recorded without query string or user info.</summary>
    public static Activity? StartCall(HttpMethod method, Uri uri, string loggedUri)
    {
        var activity = Source.StartActivity(method.Method, ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("http.request.method", method.Method);
            activity.SetTag("server.address", uri.Host);
            activity.SetTag("server.port", uri.Port);
            activity.SetTag("url.full", loggedUri);
        }

        return activity;
    }

    public static TagList Tags(HttpMethod method, Uri uri, int? statusCode, string? errorType)
    {
        var tags = new TagList
        {
            { "http.request.method", method.Method },
            { "server.address", uri.Host },
            { "server.port", uri.Port },
        };
        if (statusCode is { } status)
            tags.Add("http.response.status_code", status);
        if (errorType is not null)
            tags.Add("error.type", errorType);
        return tags;
    }
}
