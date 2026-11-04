using System.Runtime.CompilerServices;
using SFerrand.Http.Internal;

namespace SFerrand.Http;

// Reading paginated resources.
public abstract partial class HttpClientBase
{
    /// <summary>
    /// Reads a paginated resource page by page, giving each page as a result. The pages are fetched as you enumerate: stopping early stops the requests.
    /// </summary>
    /// <typeparam name="TPage">The shape of one page (the body of one response).</typeparam>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="parameters">Optional query string parameters of the first page.</param>
    /// <param name="headers">Optional headers for every request of the walk.</param>
    /// <param name="baseUri">Optional base address; its origin must be trusted.</param>
    /// <param name="next">
    /// Tells, from a page just read, where the next one is: a <see cref="PageCursor"/>, or <see langword="null"/> on the last page. Default: the
    /// <c>Link: rel="next"</c> header.
    /// </param>
    /// <param name="maxPages">Pages read before giving up with an exception. Default: 1000.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>
    /// Every page, including a failing one (an error status is a result like any other), after which the walk ends.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The next address comes from the server, so it is untrusted input: it goes through the same checks as any call (trusted origin, no credentials, no <c>..</c>),
    /// and a relative one is resolved against the page just requested.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The walk loops back to a page it already read, or goes past <paramref name="maxPages"/> pages.</exception>
    /// <example>
    /// <code>
    /// // The next page is in the body: { "items": [...], "nextCursor": "abc" }
    /// await foreach (var page in GetPagesAsync&lt;Page&gt;("widgets",
    ///     next: r => r.Result?.NextCursor is { } c ? PageCursor.WithParameters(new Dictionary&lt;string, string&gt; { ["cursor"] = c }) : null))
    /// {
    ///     foreach (var item in page.EnsureSuccess().Result!.Items) { ... }
    /// }
    /// </code>
    /// </example>
    protected async IAsyncEnumerable<HttpResult<TPage>> GetPagesAsync<TPage>(
        string resourceUri,
        string? id = null,
        IDictionary<string, string>? parameters = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        Func<HttpResult<TPage>, PageCursor?>? next = null,
        int maxPages = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        next ??= static page => PageCursor.FromLinkHeader(page.Headers);

        var resource = resourceUri;
        var identifier = id;
        var query = parameters is null ? null : new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var number = 1; ; number++)
        {
            // Built here too, to resolve a relative link against the page that was requested, and to recognize a page already read.
            var requested = RequestUri.Build(baseUri ?? _options.BaseUri, resource, identifier, query);
            if (!seen.Add(requested.AbsoluteUri))
                throw new InvalidOperationException($"The pagination loops back to a page that was already read ({Describe(requested)}).");

            var page = await GetAsync<TPage>(resource, identifier, query, headers, baseUri, cancellationToken).ConfigureAwait(false);
            yield return page;

            if (!page.IsSuccess)
                yield break;

            var cursor = next(page);
            if (cursor is null)
                yield break;

            if (number >= maxPages)
                throw new InvalidOperationException($"The resource has more than {maxPages} pages. Raise the limit if this is expected.");

            if (cursor.Url is { } url)
            {
                var target = new Uri(requested, url);
                resource = target.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
                identifier = null;
                query = null;   // the address carries its own query
            }
            else
            {
                query = new Dictionary<string, string>(query ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                foreach (var (name, value) in cursor.Parameters!)
                    query[name] = value;
            }
        }
    }

    /// <summary>
    /// Reads every item of a paginated list, as a stream: pages are fetched as you enumerate, and stopping early stops the requests.
    /// </summary>
    /// <typeparam name="TItem">The type of an item; each page is a JSON array of them.</typeparam>
    /// <param name="resourceUri">Path of the resource, appended to the base address.</param>
    /// <param name="id">Optional identifier appended as a path segment.</param>
    /// <param name="parameters">Optional query string parameters of the first page.</param>
    /// <param name="headers">Optional headers for every request of the walk.</param>
    /// <param name="baseUri">Optional base address; its origin must be trusted.</param>
    /// <param name="paging">How the pages are announced and how many to read at most. Default: the <c>Link</c> header, 1000 pages.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <exception cref="ApiException">A page answered with an error status.</exception>
    /// <exception cref="InvalidOperationException">The walk loops, or goes past <see cref="PaginationOptions.MaxPages"/> pages.</exception>
    /// <example>
    /// <code>
    /// await foreach (var repo in ListAllAsync&lt;Repo&gt;("orgs/acme/repos", paging: new PaginationOptions { PageSize = 100 }))
    ///     Console.WriteLine(repo.Name);
    /// </code>
    /// </example>
    protected async IAsyncEnumerable<TItem> ListAllAsync<TItem>(
        string resourceUri,
        string? id = null,
        IDictionary<string, string>? parameters = null,
        IDictionary<string, string>? headers = null,
        string? baseUri = null,
        PaginationOptions? paging = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        paging ??= new PaginationOptions();   // MaxPages is validated by GetPagesAsync
        if (paging.PageSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(paging), "PageSize must be positive.");

        var first = parameters is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        if (paging.PageSize is { } size)
            first[paging.PageSizeParameter] = size.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var pageNumber = paging.FirstPage;
        Func<HttpResult<List<TItem>>, PageCursor?> next;
        if (paging.Style == PaginationStyle.PageNumber)
        {
            first[paging.PageParameter] = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            next = page =>
            {
                var count = page.Result?.Count ?? 0;
                if (count == 0 || (paging.PageSize is { } expected && count < expected))
                    return null;

                pageNumber++;
                return PageCursor.WithParameters(new[] { KeyValuePair.Create(paging.PageParameter, pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
            };
        }
        else
        {
            next = static page => PageCursor.FromLinkHeader(page.Headers);
        }

        await foreach (var page in GetPagesAsync(resourceUri, id, first, headers, baseUri, next, paging.MaxPages, cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in page.EnsureSuccess().Result ?? new List<TItem>())
                yield return item;
        }
    }
}
