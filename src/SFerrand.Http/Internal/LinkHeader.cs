using System.Net.Http.Headers;

namespace SFerrand.Http.Internal;

/// <summary>Reads the <c>Link</c> header (RFC 8288), the way paginated APIs announce the next page: <c>&lt;https://api/x?page=2&gt;; rel="next"</c>.</summary>
internal static class LinkHeader
{
    /// <summary>The target of the first link whose <c>rel</c> contains <c>next</c>, or <see langword="null"/>.</summary>
    public static string? FindNext(HttpResponseHeaders? headers)
        => headers is not null && headers.TryGetValues("Link", out var values) ? FindNext(values) : null;

    public static string? FindNext(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var index = 0;
            while (index < value.Length)
            {
                var open = value.IndexOf('<', index);
                if (open < 0)
                    break;

                // A URL may contain commas and semicolons: it ends at the first '>' and nowhere else.
                var close = value.IndexOf('>', open + 1);
                if (close < 0)
                    break;

                var target = value[(open + 1)..close].Trim();
                var end = EndOfParameters(value, close + 1);
                if (target.Length > 0 && HasRelNext(value.AsSpan(close + 1, end - close - 1)))
                    return target;

                index = end + 1;
            }
        }

        return null;
    }

    // The parameters of a link run to the next comma that is not inside quotes.
    private static int EndOfParameters(string value, int start)
    {
        var quoted = false;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '"')
                quoted = !quoted;
            else if (value[i] == ',' && !quoted)
                return i;
        }

        return value.Length;
    }

    private static bool HasRelNext(ReadOnlySpan<char> parameters)
    {
        var text = parameters.ToString();
        foreach (var parameter in SplitOutsideQuotes(text, ';'))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0 || !parameter[..equals].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase))
                continue;

            var relations = parameter[(equals + 1)..].Trim().Trim('"');
            if (relations.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(r => r.Equals("next", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitOutsideQuotes(string text, char separator)
    {
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
                quoted = !quoted;
            else if (text[i] == separator && !quoted)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }
}
