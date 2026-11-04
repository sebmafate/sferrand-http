using System.Net;
using System.Net.Http.Headers;

namespace SFerrand.Http;

/// <summary>
/// Retry policy applied by <see cref="HttpClientBase"/>: exponential backoff with jitter,
/// honouring the <c>Retry-After</c> response header.
/// </summary>
/// <example>
/// <code>
/// services.Configure&lt;HttpClientOptions&gt;(o =>
/// {
///     o.Retry.MaxRetries = 5;                          // 6 attempts in total
///     o.Retry.BaseDelay = TimeSpan.FromSeconds(1);     // waits ~1 s, ~2 s, ~4 s... (each randomized between 50% and 100%)
///     o.Retry.RetryStatusCodes.Add(HttpStatusCode.Conflict);
/// });
/// </code>
/// </example>
/// <remarks>Network failures (<see cref="HttpRequestException"/>) and per-attempt timeouts are always retried, within <see cref="MaxRetries"/>.</remarks>
public class RetryOptions
{
    /// <summary>Number of retries after the first attempt. <c>0</c> disables retries. Default: 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Delay before the first retry; it doubles on each following retry. Default: 500 ms.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound of a single delay. A <c>Retry-After</c> longer than this is not waited for:
    /// the response is returned to the caller instead. Default: 30 s.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>HTTP status codes that trigger a retry. Default: 408, 429, 500, 502, 503, 504.</summary>
    public ISet<HttpStatusCode> RetryStatusCodes { get; } = new HashSet<HttpStatusCode>
    {
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    };

    /// <summary>
    /// Also retry <c>POST</c> and <c>PATCH</c>. Off by default because replaying them can duplicate a side effect
    /// unless the API is idempotent. <c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>, <c>PUT</c> and <c>DELETE</c> are always eligible.
    /// </summary>
    public bool RetryNonIdempotentMethods { get; set; }

    /// <summary>Whether <paramref name="method"/> may be replayed at all.</summary>
    internal bool IsEligible(HttpMethod method)
        => RetryNonIdempotentMethods
           || method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
           || method == HttpMethod.Put || method == HttpMethod.Delete;

    /// <summary>
    /// Computes how long to wait before retry number <paramref name="attempt"/> (1 for the first retry).
    /// </summary>
    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="retryAfter">The <c>Retry-After</c> header of the response, if any. A positive value takes precedence over the backoff.</param>
    /// <param name="random">Source of jitter in [0, 1); injectable so tests are deterministic. Default: <see cref="Random.Shared"/>.</param>
    /// <param name="now">Reference time for a <c>Retry-After</c> given as a date. Default: the current UTC time.</param>
    /// <returns>The delay, or <see langword="null"/> when the server asked for a wait longer than <see cref="MaxDelay"/>.</returns>
    /// <remarks>
    /// <c>BaseDelay × 2^(attempt-1)</c>, capped at <see cref="MaxDelay"/>, then "equal jitter": the result lies in
    /// [delay/2, delay]. Compared with full jitter this keeps a guaranteed minimum wait while still desynchronizing clients.
    /// A <c>Retry-After</c> that is zero, negative or already in the past is ignored in favour of the backoff.
    /// </remarks>
    internal TimeSpan? GetDelay(int attempt, RetryConditionHeaderValue? retryAfter = null, Func<double>? random = null, DateTimeOffset? now = null)
    {
        if (retryAfter is not null)
        {
            var wait = retryAfter.Delta ?? retryAfter.Date - (now ?? DateTimeOffset.UtcNow);
            if (wait is { } requested && requested > TimeSpan.Zero)
                return requested <= MaxDelay ? requested : null;
        }

        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);

        // "Equal jitter": half the delay is fixed, the other half random, so concurrent clients spread out.
        var jitter = (random ?? Random.Shared.NextDouble)();
        return TimeSpan.FromMilliseconds(capped / 2 + capped / 2 * jitter);
    }
}
