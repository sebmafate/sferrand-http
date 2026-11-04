using Microsoft.Extensions.Logging;

namespace SFerrand.Http.Internal;

/// <summary>
/// Log messages, generated at compile time (no boxing, no template parsing per call). Addresses are always logged without their query string and
/// user info, and bodies and credentials are never logged.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "HTTP {Method} {Uri} -> {StatusCode} in {ElapsedMs} ms")]
    public static partial void Succeeded(ILogger logger, HttpMethod method, string uri, int statusCode, long elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "HTTP {Method} {Uri} -> {StatusCode} {ReasonPhrase} in {ElapsedMs} ms")]
    public static partial void Failed(ILogger logger, HttpMethod method, string uri, int statusCode, string? reasonPhrase, long elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "HTTP {Method} {Uri} -> {StatusCode}; retry {Attempt}/{MaxRetries} in {DelayMs} ms")]
    public static partial void RetryAfterStatus(ILogger logger, HttpMethod method, string uri, int statusCode, int attempt, int maxRetries, int delayMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "HTTP {Method} {Uri} timed out; retry {Attempt}/{MaxRetries} in {DelayMs} ms")]
    public static partial void RetryAfterTimeout(ILogger logger, HttpMethod method, string uri, int attempt, int maxRetries, int delayMs);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "HTTP {Method} {Uri} failed; retry {Attempt}/{MaxRetries} in {DelayMs} ms")]
    public static partial void RetryAfterException(ILogger logger, Exception exception, HttpMethod method, string uri, int attempt, int maxRetries, int delayMs);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "HTTP {Method} {From} redirected ({StatusCode}) to {To}")]
    public static partial void Redirected(ILogger logger, HttpMethod method, string from, int statusCode, string to);
}
