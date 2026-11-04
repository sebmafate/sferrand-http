using System.Text.Json;

namespace SFerrand.Http.Internal;

/// <summary>
/// A request body already turned into bytes, with its media type. The bytes are computed once per call and replayed on every attempt
/// and on a redirect that keeps the body.
/// </summary>
internal sealed class RequestBody(byte[] bytes, string contentType)
{
    public byte[] Bytes { get; } = bytes;

    public string ContentType { get; } = contentType;

    /// <summary>
    /// <see langword="null"/> gives no body; an already prepared body (form, multipart) is used as is; anything else is serialized as JSON with
    /// the <b>runtime type</b> of the object (serializing as <c>object</c> would write <c>{}</c>).
    /// </summary>
    /// <remarks>
    /// The metadata comes from <see cref="JsonMetadata"/>, so the call works with a source-generated context
    /// (trimming and native AOT) as well as with reflection.
    /// </remarks>
    public static RequestBody? From(object? body, JsonSerializerOptions options)
        => body switch
        {
            null => null,
            RequestBody prepared => prepared,
            _ => new RequestBody(
                JsonSerializer.SerializeToUtf8Bytes(body, JsonMetadata.Get(options, body.GetType())),
                "application/json; charset=utf-8"),
        };
}
