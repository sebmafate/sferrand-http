using System.Text.Json.Serialization;

namespace SFerrand.Http;

/// <summary>
/// The JSON body of a GraphQL request.
/// </summary>
/// <typeparam name="TVariables">Type of the variables object.</typeparam>
public class GraphQLQuery<TVariables>
{
    /// <summary>The GraphQL document.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>The values of the variables referenced by the document.</summary>
    [JsonPropertyName("variables")]
    public TVariables? Variables { get; set; }
}
