namespace SFerrand.Http.Internal;

/// <summary>
/// What a header name and a header value must look like to be accepted. Values are added to requests without the framework's validation (so unusual
/// formats work), which does not stop a line break from becoming a second header on the wire: this is the check that does.
/// </summary>
internal static class HeaderRules
{
    // Headers that frame the message or manage the connection. They belong to the HTTP stack: letting a caller set them is how request
    // smuggling and cache poisoning start, and nothing legitimate needs it.
    private static readonly HashSet<string> StackManaged = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection", "Keep-Alive", "Upgrade", "TE", "Trailer",
    };

    /// <summary>Why <paramref name="name"/> cannot be used as a header name, or <see langword="null"/> when it can.</summary>
    public static string? NameError(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(IsTokenCharacter))
            return "A header name is not a valid HTTP token (letters, digits and !#$%&'*+-.^_`|~ only).";

        return StackManaged.Contains(name) ? $"The header '{name}' is managed by the HTTP stack and cannot be set." : null;
    }

    /// <summary>
    /// Why <paramref name="value"/> cannot be used as the value of <paramref name="headerName"/>, or <see langword="null"/>. The value is never repeated in the
    /// message: it may be a credential. The name is safe to repeat: callers validate it first, or pass a literal.
    /// </summary>
    public static string? ValueError(string value, string headerName)
        => value.Any(c => c is (< ' ' and not '\t') or '\u007f')
            ? $"The value of the header '{headerName}' contains a control character (line break, NUL...)."
            : null;

    private static bool IsTokenCharacter(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
