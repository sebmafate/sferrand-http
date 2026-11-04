using System.Text.Json;
using System.Text.Json.Serialization;

namespace SFerrand.Http;

/// <summary>
/// The standard envelope of a GraphQL answer: <c>data</c> and <c>errors</c>. A GraphQL server usually answers <c>200 OK</c> even when a field failed, with the
/// failures in <c>errors</c>, so <c>IsSuccess</c> alone is not enough.
/// </summary>
/// <typeparam name="TData">The shape of <c>data</c>.</typeparam>
/// <example>
/// <code>
/// var result = await QueryGraphQLAsync&lt;WidgetData, object&gt;(query);
/// var data = result.EnsureSuccess().Result!.EnsureNoErrors();
/// </code>
/// </example>
public sealed class GraphQLResponse<TData>
{
    /// <summary>The requested data. Partial or <see langword="null"/> when <see cref="Errors"/> is not empty.</summary>
    [JsonPropertyName("data")]
    public TData? Data { get; set; }

    /// <summary>The errors reported by the server, if any.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<GraphQLError>? Errors { get; set; }

    /// <summary>Server-specific additional information (cost, tracing...).</summary>
    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether the server reported at least one error.</summary>
    [JsonIgnore]
    public bool HasErrors => Errors is { Count: > 0 };

    /// <summary>Returns <see cref="Data"/>, or throws when the server reported errors.</summary>
    /// <exception cref="GraphQLException">There is at least one error.</exception>
    public TData? EnsureNoErrors() => HasErrors ? throw new GraphQLException(Errors!) : Data;
}

/// <summary>One error of a GraphQL answer.</summary>
public sealed class GraphQLError
{
    /// <summary>A description of the error.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Where in the document the error was found.</summary>
    [JsonPropertyName("locations")]
    public IReadOnlyList<GraphQLLocation>? Locations { get; set; }

    /// <summary>The path of the response field that failed: strings (field names) and numbers (list indexes).</summary>
    [JsonPropertyName("path")]
    public IReadOnlyList<JsonElement>? Path { get; set; }

    /// <summary>Server-specific additional information (error codes...).</summary>
    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>A position in a GraphQL document.</summary>
public sealed class GraphQLLocation
{
    /// <summary>The line, starting at 1.</summary>
    [JsonPropertyName("line")]
    public int Line { get; set; }

    /// <summary>The column, starting at 1.</summary>
    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>A GraphQL answer carried errors. Thrown by <see cref="GraphQLResponse{TData}.EnsureNoErrors"/>.</summary>
public sealed class GraphQLException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="errors">The errors reported by the server.</param>
    public GraphQLException(IReadOnlyList<GraphQLError> errors)
        : base(BuildMessage(errors)) => Errors = errors;

    /// <summary>The errors reported by the server.</summary>
    public IReadOnlyList<GraphQLError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<GraphQLError> errors)
    {
        var first = errors.Count > 0 ? errors[0].Message : null;
        var text = first is null ? "no message" : string.Concat(first.Select(c => char.IsControl(c) ? ' ' : c));
        if (text.Length > 200)
            text = text[..200] + "...";
        return errors.Count > 1 ? $"The GraphQL server reported {errors.Count} errors; the first: {text}" : $"The GraphQL server reported an error: {text}";
    }
}
