using System.Text;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

// Other kinds of body (form, multipart) and streamed downloads.
public abstract partial class HttpClientBase
{
    /// <summary>
    /// Sends a <c>POST</c> with an <c>application/x-www-form-urlencoded</c> body, the format of OAuth token endpoints and many classic APIs.
    /// </summary>
    /// <typeparam name="TResult">Type the response body is deserialized into; <see cref="string"/> keeps it raw.</typeparam>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="fields">Names and values; they are URL-encoded (a space becomes <c>+</c>).</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only; its origin must be trusted.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    /// <remarks>Like any <c>POST</c>, it is retried only when <see cref="RetryOptions.RetryNonIdempotentMethods"/> or an idempotency key says it may be.</remarks>
    protected virtual Task<HttpResult<TResult>> PostFormAsync<TResult>(
        string resourceUri,
        IEnumerable<KeyValuePair<string, string>> fields,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var encoded = string.Join('&', fields.Select(f => $"{FormEncode(f.Key)}={FormEncode(f.Value ?? string.Empty)}"));
        return SendAsync<TResult>(HttpMethod.Post, resourceUri, id: null, parameters: null,
            new RequestBody(Encoding.UTF8.GetBytes(encoded), "application/x-www-form-urlencoded"), headers, baseUri, cancellationToken);
    }

    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>Sends a <c>POST</c> with a <c>multipart/form-data</c> body (fields and files).</summary>
    /// <typeparam name="TResult">Type the response body is deserialized into; <see cref="string"/> keeps it raw.</typeparam>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="form">The parts. The form is read once, when the call starts.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only; its origin must be trusted.</param>
    /// <param name="cancellationToken">Cancels the call, including retry waits.</param>
    protected virtual async Task<HttpResult<TResult>> PostMultipartAsync<TResult>(
        string resourceUri,
        MultipartForm form,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);

        var body = await form.BuildAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync<TResult>(HttpMethod.Post, resourceUri, id: null, parameters: null, body, headers, baseUri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a resource into a stream, without holding it in memory. The result carries the number of bytes written; an error status is a result like
    /// any other, and the stream is then left untouched.
    /// </summary>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="destination">A writable stream (a file, a response body...). It is not closed.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="parameters">Optional query string parameters.</param>
    /// <param name="headers">Optional headers for this call only.</param>
    /// <param name="baseUri">Optional base address for this call only; its origin must be trusted.</param>
    /// <param name="maxBytes">Largest download accepted. Default: <see cref="HttpClientOptions.MaxDownloadBytes"/>.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The status, headers and, in <see cref="HttpResult{TResult}.Result"/>, the number of bytes written.</returns>
    /// <remarks>
    /// <para>
    /// The request is retried like a <c>GET</c> as long as nothing has been written. Once the first byte is in <paramref name="destination"/> a failure is final
    /// (a <see cref="DownloadInterruptedException"/> that says how far it got): the stream cannot be rewound, and writing the same bytes twice would corrupt it.
    /// </para>
    /// <para>A body that announces, or turns out to have, more than <paramref name="maxBytes"/> raises <see cref="HttpResponseTooLargeException"/>.</para>
    /// </remarks>
    /// <exception cref="HttpResponseTooLargeException">The download exceeds the limit.</exception>
    /// <exception cref="DownloadInterruptedException">The download failed after it started.</exception>
    protected virtual Task<HttpResult<long>> DownloadAsync(
        string resourceUri,
        Stream destination,
        string? id = null,
        IDictionary<string, string>? parameters = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        if (maxBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The limit must be positive.");

        var limit = maxBytes ?? _options.MaxDownloadBytes;
        var written = 0L;

        return SendCoreAsync(
            HttpMethod.Get, resourceUri, id, parameters, body: null, headers, baseUri,
            async (response, token) =>
            {
                // An error answer is read like any other (bounded, as text); the destination is for the payload only.
                if (!response.IsSuccessStatusCode)
                    return await HttpResult<long>.FromResponseAsync(response, JsonSerializerOptions, _options.MaxResponseBytes, token).ConfigureAwait(false);

                if (limit is { } max && response.Content.Headers.ContentLength > max)
                    throw new HttpResponseTooLargeException(max);

                try
                {
                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81_920);
                    try
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                        {
                            if (limit is { } cap && written + read > cap)
                                throw new HttpResponseTooLargeException(cap);

                            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            written += read;
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (Exception ex) when (written > 0 && ex is not HttpResponseTooLargeException && !cancellationToken.IsCancellationRequested)
                {
                    // Bytes are already in the destination: this must not look like a transient failure the engine would replay.
                    throw new DownloadInterruptedException(written, ex);
                }

                return HttpResult<long>.FromStreamed(response, written);
            },
            cancellationToken);
    }
}
