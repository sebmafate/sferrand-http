using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

/// <summary>
/// The outcome of an HTTP call: status, headers, raw body and, on success, the deserialized body.
/// HTTP error statuses are reported here instead of being thrown.
/// </summary>
/// <typeparam name="TResult">Type of the deserialized body. <see cref="string"/> keeps the raw body.</typeparam>
[DebuggerDisplay("[{StatusCode}] IsSuccess={IsSuccess}, ReasonPhrase={ReasonPhrase}, Content={Content}")]
public class HttpResult<TResult>
{
    // Shared and frozen at first use: nobody else holds a reference to it, so nothing can mutate it. Creating one per call would be wasteful.
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

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

    /// <summary>Unwraps the deserialized body, so <c>Widget? w = await client.GetAsync("1");</c> works.</summary>
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
    /// <param name="maxContentBytes">Largest body accepted, in bytes. <see langword="null"/> (the default) reads whatever comes.</param>
    /// <param name="cancellationToken">Cancels the body read.</param>
    /// <exception cref="HttpResponseTooLargeException">The body is larger than <paramref name="maxContentBytes"/>.</exception>
    /// <exception cref="JsonException">A successful body is not valid JSON for <typeparamref name="TResult"/>.</exception>
    public static async Task<HttpResult<TResult>> FromResponseAsync(
        HttpResponseMessage response,
        JsonSerializerOptions? options = null,
        long? maxContentBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var content = maxContentBytes is { } limit
            ? await ReadLimitedAsync(response.Content, limit, cancellationToken).ConfigureAwait(false)
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Wraps an already available object in a successful (<c>200 OK</c>) result, e.g. for a cache hit or a test double.</summary>
    /// <param name="result">The value exposed as <see cref="Result"/>.</param>
    /// <param name="options">JSON options used to fill <see cref="Content"/>. Default: <see cref="JsonSerializerDefaults.Web"/>.</param>
    public static HttpResult<TResult> CreateFromObject(TResult result, JsonSerializerOptions? options = null)
        => new()
        {
            IsSuccess = true,
            StatusCode = HttpStatusCode.OK,
            ReasonPhrase = "OK",
            Content = result is string text ? text : JsonSerializer.Serialize(result, JsonMetadata.Get<TResult>(options ?? DefaultJsonOptions)),
            Result = result,
        };

    /// <summary>
    /// Reads a body into a string without ever holding more than <paramref name="limit"/> bytes: a declared length over the limit is refused before
    /// reading, and a body that turns out larger than it announced (or announced nothing) is cut off as soon as it crosses the limit.
    /// </summary>
    private static async Task<string> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        limit = Math.Min(limit, Array.MaxLength);
        if (content.Headers.ContentLength is { } declared && declared > limit)
            throw new HttpResponseTooLargeException(limit);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new HttpResponseTooLargeException(limit);
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, ResolveEncoding(content.Headers.ContentType?.CharSet), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // An unknown or malformed charset must not fail the call: UTF-8 is what JSON APIs speak.
    private static System.Text.Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return System.Text.Encoding.UTF8;

        try
        {
            return System.Text.Encoding.GetEncoding(charset.Trim('"', ' '));
        }
        catch (ArgumentException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    // string: the raw body is the result. Empty body: no result (204, HEAD...). Otherwise JSON; invalid JSON throws JsonException.
    private static TResult? Deserialize(string content, JsonSerializerOptions? options)
    {
        if (typeof(TResult) == typeof(string))
            return (TResult)(object)content;

        return string.IsNullOrWhiteSpace(content)
            ? default
            : JsonSerializer.Deserialize(content, JsonMetadata.Get<TResult>(options ?? DefaultJsonOptions));
    }
}
