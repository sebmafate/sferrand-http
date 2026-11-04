using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SFerrand.Http;

/// <summary>
/// The outcome of an HTTP call: status, headers, raw body and, on success, the deserialized body.
/// HTTP error statuses are reported here instead of being thrown.
/// </summary>
/// <typeparam name="TResult">Type of the deserialized body. <see cref="string"/> keeps the raw body.</typeparam>
[DebuggerDisplay("[{StatusCode}] IsSuccess={IsSuccess}, ReasonPhrase={ReasonPhrase}, Content={Content}")]
public class HttpResult<TResult>
{
    /// <summary>Creates an empty result. Use <see cref="FromResponseAsync"/> to build one from a response.</summary>
    protected HttpResult() { }

    /// <summary>
    /// A result representing a call that produced no response at all. <see cref="StatusCode"/> is <c>0</c>.
    /// </summary>
    public static HttpResult<TResult> Fail { get; } = new()
    {
        IsSuccess = false,
        ReasonPhrase = "No response",
    };

    /// <summary>A successful result without body (<c>204 No Content</c>).</summary>
    public static HttpResult<TResult> Empty { get; } = new()
    {
        IsSuccess = true,
        ReasonPhrase = "No Content",
        StatusCode = HttpStatusCode.NoContent,
    };

    /// <summary>The HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; protected init; }

    /// <summary>The response headers, or <see langword="null"/> for synthetic results (<see cref="Fail"/>, <see cref="Empty"/>).</summary>
    public HttpResponseHeaders? Headers { get; protected init; }

    /// <summary>The reason phrase sent by the server.</summary>
    public string? ReasonPhrase { get; protected init; }

    /// <summary><see langword="true"/> for a 2xx status.</summary>
    public bool IsSuccess { get; protected init; }

    /// <summary>The raw response body, kept for error statuses too.</summary>
    public string? Content { get; protected init; }

    /// <summary>The deserialized body; <see langword="null"/> on error statuses and empty bodies.</summary>
    public TResult? Result { get; protected init; }

    /// <summary>Unwraps the deserialized body.</summary>
    public static implicit operator TResult?(HttpResult<TResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Result;
    }

    /// <summary>
    /// Reads a response into a result. The response is not disposed.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="options">JSON options used to deserialize a successful body. Default: <see cref="JsonSerializerDefaults.Web"/>.</param>
    /// <param name="cancellationToken">Cancels the body read.</param>
    /// <exception cref="JsonException">A successful body is not valid JSON for <typeparamref name="TResult"/>.</exception>
    public static async Task<HttpResult<TResult>> FromResponseAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var isSuccess = response.IsSuccessStatusCode;

        return new HttpResult<TResult>
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            IsSuccess = isSuccess,
            Content = content,
            Headers = response.Headers,
            Result = isSuccess ? Deserialize(content, options) : default,
        };
    }

    /// <summary>Wraps an already available object in a successful (<c>200 OK</c>) result.</summary>
    public static HttpResult<TResult> CreateFromObject(TResult result, JsonSerializerOptions? options = null)
        => new()
        {
            IsSuccess = true,
            StatusCode = HttpStatusCode.OK,
            ReasonPhrase = "OK",
            Content = result is string text ? text : JsonSerializer.Serialize(result, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Result = result,
        };

    private static TResult? Deserialize(string content, JsonSerializerOptions? options)
    {
        if (typeof(TResult) == typeof(string))
            return (TResult)(object)content;

        return string.IsNullOrWhiteSpace(content)
            ? default
            : JsonSerializer.Deserialize<TResult>(content, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
