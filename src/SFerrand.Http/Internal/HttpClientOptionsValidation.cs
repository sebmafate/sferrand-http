namespace SFerrand.Http.Internal;

/// <summary>
/// The rules a <see cref="HttpClientOptions"/> must satisfy, in one place: used when a client is built (which throws) and by the
/// <c>IValidateOptions</c> implementation (which lets a host fail at start-up instead of at the first call).
/// </summary>
internal static class HttpClientOptionsValidation
{
    /// <summary>
    /// The problems of <paramref name="options"/>, one message each, none of which repeats more than scheme, host and port of an address (it may carry secrets).
    /// <paramref name="trustedOrigins"/> receives the origins the options allow: the base address, the GraphQL endpoint and the declared origins.
    /// </summary>
    public static List<string> Validate(HttpClientOptions options, out HashSet<string> trustedOrigins)
    {
        var errors = new List<string>();
        trustedOrigins = new HashSet<string>(StringComparer.Ordinal);

        Address(options.BaseUri, nameof(HttpClientOptions.BaseUri), options, errors, trustedOrigins);
        if (!string.IsNullOrEmpty(options.GraphQLUri))
            Address(options.GraphQLUri, nameof(HttpClientOptions.GraphQLUri), options, errors, trustedOrigins);
        foreach (var origin in options.TrustedOrigins)
            Address(origin, nameof(HttpClientOptions.TrustedOrigins), options, errors, trustedOrigins);

        if (options.UserAgent is { } userAgent && HeaderRules.ValueError(userAgent, "User-Agent") is { } userAgentError)
            errors.Add(userAgentError);
        if (options.Accept is { } accept && HeaderRules.ValueError(accept, "Accept") is { } acceptError)
            errors.Add(acceptError);
        if (options.Retry.IdempotencyKeyHeader is { } keyHeader && HeaderRules.NameError(keyHeader) is { } keyHeaderError)
            errors.Add($"{nameof(RetryOptions.IdempotencyKeyHeader)}: {keyHeaderError}");

        if (options.MaxResponseBytes is <= 0)
            errors.Add($"{nameof(HttpClientOptions.MaxResponseBytes)} must be positive, or null for no limit.");
        if (options.MaxDownloadBytes is <= 0)
            errors.Add($"{nameof(HttpClientOptions.MaxDownloadBytes)} must be positive, or null for no limit.");
        if (options.MaxRedirects < 0)
            errors.Add($"{nameof(HttpClientOptions.MaxRedirects)} must not be negative.");
        if (options.TotalTimeout is { } total && total <= TimeSpan.Zero && total != Timeout.InfiniteTimeSpan)
            errors.Add($"{nameof(HttpClientOptions.TotalTimeout)} must be positive, or null for no deadline.");
        if (options.RequestTimeout < TimeSpan.Zero && options.RequestTimeout != Timeout.InfiniteTimeSpan)
            errors.Add($"{nameof(HttpClientOptions.RequestTimeout)} must not be negative.");
        if (options.Retry.MaxRetries < 0)
            errors.Add($"{nameof(RetryOptions.MaxRetries)} must not be negative.");
        if (options.Retry.BaseDelay < TimeSpan.Zero || options.Retry.MaxDelay < TimeSpan.Zero)
            errors.Add($"{nameof(RetryOptions.BaseDelay)} and {nameof(RetryOptions.MaxDelay)} must not be negative.");

        foreach (var name in options.Headers.Keys)
        {
            if (HeaderRules.NameError(name) is { } nameError)
                errors.Add($"{nameof(HttpClientOptions.Headers)}: {nameError}");
        }

        return errors;
    }

    private static void Address(string? value, string optionName, HttpClientOptions options, List<string> errors, HashSet<string> origins)
    {
        if (value is null || !RequestUri.TryParseHttp(value, out var uri))
            errors.Add($"{optionName} must be an absolute http(s) URI ({RequestUri.Describe(value)}).");
        else if (uri.UserInfo.Length > 0)
            errors.Add($"{optionName} must not contain credentials (user:password@host): use TokenFactory or a header.");
        else if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !options.AllowInsecureHttp)
            errors.Add($"{optionName} uses clear-text http ({RequestUri.Describe(value)}): use https, or set {nameof(HttpClientOptions.AllowInsecureHttp)}.");
        else
            origins.Add(RequestUri.Origin(uri));
    }
}
