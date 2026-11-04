using System.Net.Http.Headers;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

/// <summary>
/// A <c>multipart/form-data</c> body: text fields and files, built in memory so that it can be replayed on a retry or a redirect.
/// </summary>
/// <remarks>
/// For large uploads, which should be streamed and not held in memory, use an <see cref="HttpClient"/> directly.
/// Field and file names are written into the body's headers between quotes without escaping, so a name containing a quote or a control character could
/// forge headers of the part: such names are refused.
/// </remarks>
/// <example>
/// <code>
/// var form = new MultipartForm()
///     .AddField("title", "Quarterly report")
///     .AddFile("file", "report.pdf", pdfBytes, "application/pdf");
/// var result = await PostMultipartAsync&lt;Document&gt;("documents", form);
/// </code>
/// </example>
public sealed class MultipartForm
{
    private readonly List<(string Name, string? FileName, byte[] Content, string? ContentType)> _parts = new();

    /// <summary>Adds a text field (sent as UTF-8).</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This form, to chain.</returns>
    /// <exception cref="ArgumentException">The name is empty, or contains a quote or a control character.</exception>
    public MultipartForm AddField(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateName(name, nameof(name));
        _parts.Add((name, null, System.Text.Encoding.UTF8.GetBytes(value), null));
        return this;
    }

    /// <summary>Adds a file.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="fileName">The file name announced to the server (not a path).</param>
    /// <param name="content">The bytes of the file.</param>
    /// <param name="contentType">The media type of the file. Default: <c>application/octet-stream</c>.</param>
    /// <returns>This form, to chain.</returns>
    /// <exception cref="ArgumentException">A name is empty, or contains a quote or a control character, or <paramref name="contentType"/> is not a media type.</exception>
    public MultipartForm AddFile(string name, string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateName(name, nameof(name));
        ValidateName(fileName, nameof(fileName));
        if (!MediaTypeHeaderValue.TryParse(contentType, out _))
            throw new ArgumentException("Not a valid media type.", nameof(contentType));

        _parts.Add((name, fileName, content, contentType));
        return this;
    }

    private static void ValidateName(string name, string parameter)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("A name is required.", parameter);

        if (name.Any(c => char.IsControl(c) || c == '"' || c == '\\'))
            throw new ArgumentException("A name must not contain a quote, a backslash or a control character.", parameter);
    }

    internal async Task<RequestBody> BuildAsync(CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent(Guid.NewGuid().ToString("N"));
        foreach (var (name, fileName, bytes, contentType) in _parts)
        {
            var part = new ByteArrayContent(bytes);
            if (contentType is not null)
                part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            if (fileName is null)
                content.Add(part, name);
            else
                content.Add(part, name, fileName);
        }

        var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new RequestBody(body, content.Headers.ContentType!.ToString());
    }
}
