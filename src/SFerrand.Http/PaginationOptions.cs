namespace SFerrand.Http;

/// <summary>How a paginated list announces its pages.</summary>
public enum PaginationStyle
{
    /// <summary>The response carries a <c>Link: &lt;...&gt;; rel="next"</c> header (GitHub, GitLab, many others). The default.</summary>
    LinkHeader,

    /// <summary>The request carries a page number; the list ends with an empty page, or a page shorter than <see cref="PaginationOptions.PageSize"/>.</summary>
    PageNumber,
}

/// <summary>Settings of a paginated read (<c>ListAllAsync</c>).</summary>
public sealed class PaginationOptions
{
    /// <summary>How the pages are announced. Default: <see cref="PaginationStyle.LinkHeader"/>.</summary>
    public PaginationStyle Style { get; init; } = PaginationStyle.LinkHeader;

    /// <summary>
    /// Pages read before giving up with an <see cref="InvalidOperationException"/>. A guard against a server that never ends, or that loops; hitting it never
    /// truncates the list silently. Default: 1000.
    /// </summary>
    public int MaxPages { get; init; } = 1000;

    /// <summary>Items per page to ask for, sent as <see cref="PageSizeParameter"/>. With <see cref="PaginationStyle.PageNumber"/> a shorter page ends the list. <see langword="null"/> asks nothing.</summary>
    public int? PageSize { get; init; }

    /// <summary>Name of the query parameter that carries <see cref="PageSize"/>. Default: <c>per_page</c>.</summary>
    public string PageSizeParameter { get; init; } = "per_page";

    /// <summary>Name of the query parameter that carries the page number (<see cref="PaginationStyle.PageNumber"/> only). Default: <c>page</c>.</summary>
    public string PageParameter { get; init; } = "page";

    /// <summary>Number of the first page (<see cref="PaginationStyle.PageNumber"/> only). Default: 1.</summary>
    public int FirstPage { get; init; } = 1;
}
