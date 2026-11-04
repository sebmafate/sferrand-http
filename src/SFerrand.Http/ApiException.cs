using System.Net;

namespace SFerrand.Http;

/// <summary>
/// The API answered with an error status. Thrown by <see cref="HttpResult{TResult}.EnsureSuccess"/> when you prefer an exception to checking <c>IsSuccess</c>.
/// </summary>
/// <remarks>
/// The message holds the status and, when the body is a <see cref="ProblemDetails"/>, its title or detail (shortened, on one line). The raw body is available in
/// <see cref="Content"/> but is <b>not</b> put in the message: it can contain personal data or secrets that an exception message would carry into logs.
/// </remarks>
public sealed class ApiException : Exception
{
    private const int MaxMessageDetail = 200;

    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">The HTTP status (0 when there was no response).</param>
    /// <param name="reasonPhrase">The reason phrase sent by the server.</param>
    /// <param name="content">The raw response body.</param>
    /// <param name="problem">The body read as a problem document, if it is one.</param>
    public ApiException(HttpStatusCode statusCode, string? reasonPhrase, string? content, ProblemDetails? problem)
        : base(BuildMessage(statusCode, reasonPhrase, problem))
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Content = content;
        Problem = problem;
    }

    /// <summary>The HTTP status; 0 when there was no response.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The reason phrase sent by the server.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>The raw response body, as received.</summary>
    public string? Content { get; }

    /// <summary>The body read as a problem document, or <see langword="null"/> when it is not one.</summary>
    public ProblemDetails? Problem { get; }

    private static string BuildMessage(HttpStatusCode status, string? reason, ProblemDetails? problem)
    {
        var head = status == 0 ? "The API did not answer" : $"The API answered {(int)status} {OneLine(reason)}".TrimEnd();
        var detail = problem?.Title ?? problem?.Detail;
        return detail is null ? head : $"{head}: {Shorten(OneLine(detail))}";
    }

    // The message goes to logs: no line break, whatever the server wrote.
    private static string OneLine(string? text)
        => text is null ? string.Empty : string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));

    private static string Shorten(string text) => text.Length <= MaxMessageDetail ? text : text[..MaxMessageDetail] + "...";
}
