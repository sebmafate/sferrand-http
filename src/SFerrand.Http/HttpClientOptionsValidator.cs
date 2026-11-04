using Microsoft.Extensions.Options;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

/// <summary>
/// Validates <see cref="HttpClientOptions"/> with the options pattern, so that a host can refuse to start with a bad configuration (an address with credentials,
/// clear-text http, a negative timeout...) instead of failing at the first call. <c>AddSFerrandHttpClient</c> registers it and asks for validation at start-up.
/// </summary>
/// <remarks>The rules are the ones applied when a client is built. Messages never repeat more than the scheme, host and port of an address: it may carry secrets.</remarks>
public sealed class HttpClientOptionsValidator : IValidateOptions<HttpClientOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = HttpClientOptionsValidation.Validate(options, out _);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
