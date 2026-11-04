using System.Text.Json;

namespace SFerrand.Http;

/// <summary>
/// An error described the standard way, <c>application/problem+json</c> (RFC 9457, which replaces RFC 7807). Many APIs return it for their 4xx and 5xx answers.
/// </summary>
/// <remarks>
/// It is read with <see cref="JsonDocument"/>, not with the serializer, so it works the same trimmed and ahead-of-time compiled, and a body that is not a
/// problem document is simply not a problem (<see cref="TryParse"/> returns <see langword="false"/>, it never throws).
/// </remarks>
public sealed class ProblemDetails
{
    /// <summary>A URI that identifies the kind of problem.</summary>
    public string? Type { get; init; }

    /// <summary>A short, human-readable summary of the kind of problem.</summary>
    public string? Title { get; init; }

    /// <summary>The HTTP status the server says it generated.</summary>
    public int? Status { get; init; }

    /// <summary>A human-readable explanation of this occurrence of the problem.</summary>
    public string? Detail { get; init; }

    /// <summary>A URI that identifies this occurrence of the problem.</summary>
    public string? Instance { get; init; }

    /// <summary>Every other member of the document (validation errors, trace identifiers...), by name.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Reads a problem document. The body must be a JSON object with at least one of the standard members (<c>type</c>, <c>title</c>, <c>status</c>,
    /// <c>detail</c>, <c>instance</c>): an arbitrary JSON error object is not mistaken for one.
    /// </summary>
    /// <param name="content">The body.</param>
    /// <param name="problem">The problem, when the body is one.</param>
    /// <returns><see langword="true"/> when <paramref name="content"/> is a problem document.</returns>
    public static bool TryParse(string? content, out ProblemDetails? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(content) || content.AsSpan().TrimStart()[0] != '{')
            return false;

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            string? type = null, title = null, detail = null, instance = null;
            int? status = null;
            var standard = false;
            var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            foreach (var member in document.RootElement.EnumerateObject())
            {
                switch (member.Name.ToLowerInvariant())
                {
                    case "type": type = AsString(member.Value); standard = true; break;
                    case "title": title = AsString(member.Value); standard = true; break;
                    case "detail": detail = AsString(member.Value); standard = true; break;
                    case "instance": instance = AsString(member.Value); standard = true; break;
                    case "status":
                        status = member.Value.ValueKind == JsonValueKind.Number && member.Value.TryGetInt32(out var number) ? number : null;
                        standard = true;
                        break;
                    default: extensions[member.Name] = member.Value.Clone(); break;
                }
            }

            if (!standard)
                return false;

            problem = new ProblemDetails { Type = type, Title = title, Status = status, Detail = detail, Instance = instance, Extensions = extensions };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? AsString(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
