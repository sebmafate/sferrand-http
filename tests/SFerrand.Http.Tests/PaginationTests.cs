using System.Net;
using SFerrand.Http.Internal;
using SFerrand.Http.Tests.Support;
using SFerrand.Http.Testing;

namespace SFerrand.Http.Tests;

/// <summary>Reading paginated resources: the <c>Link</c> header parser, the page walk (with the safety rules for an address that comes from the server) and <c>ListAllAsync</c>.</summary>
[TestClass]
public class PaginationTests
{
    #region Link header parser

    [TestMethod]
    [DataRow("""<https://api.test/v1/x?page=2>; rel="next"; title="p2"; type="a, b"; x=";", <https://api.test/v1/x?page=9>; rel="last" """, "https://api.test/v1/x?page=2")]
    [DataRow("""<https://api.test/v1/x?page=1>; rel="prev", <https://api.test/v1/x?page=3>; rel="next", <https://api.test/v1/x?page=9>; rel="last" """, "https://api.test/v1/x?page=3")]
    [DataRow("<https://api.test/x?p=2>; rel=next", "https://api.test/x?p=2")]
    [DataRow("<https://api.test/x?p=2>;rel=\"next\"", "https://api.test/x?p=2")]
    [DataRow("<https://api.test/x?p=2>; REL=\"NEXT\"", "https://api.test/x?p=2")]
    [DataRow("<https://api.test/x?p=2>; rel=\"prefetch next\"", "https://api.test/x?p=2")]
    [DataRow("</v1/x?page=2>; rel=\"next\"", "/v1/x?page=2")]
    [DataRow("<https://api.test/x?a=1,2;b>; rel=\"next\"", "https://api.test/x?a=1,2;b")]    // a comma and a semicolon inside the URL
    [DataRow("  <  https://api.test/x  >  ;  rel = \"next\"  ", "https://api.test/x")]
    public void TheNextLink_IsFound(string header, string expected)
        => Assert.AreEqual(expected, LinkHeader.FindNext(new[] { header }));

    [TestMethod]
    public void TheNextLink_IsFoundInAnyOfSeveralHeaderValues()
        => Assert.AreEqual("https://api.test/n", LinkHeader.FindNext(new[] { "<https://api.test/p>; rel=\"prev\"", "<https://api.test/n>; rel=\"next\"" }));

    [TestMethod]
    [DataRow("<https://api.test/x>; rel=\"prev\"")]
    [DataRow("<https://api.test/x>; rel=\"nextpage\"")]       // not the same relation
    [DataRow("<https://api.test/x>; title=\"next\"")]          // "next" is a title, not a relation
    [DataRow("<https://api.test/x>")]
    [DataRow("<>; rel=\"next\"")]
    [DataRow("<https://api.test/x; rel=\"next\"")]             // unterminated
    [DataRow("rel=\"next\"")]
    [DataRow("")]
    [DataRow(",,,")]
    public void NoNextLink_IsReportedAsNone(string header)
        => Assert.IsNull(LinkHeader.FindNext(new[] { header }));

    [TestMethod]
    public void ANullOrAbsentHeader_IsNone()
    {
        Assert.IsNull(LinkHeader.FindNext((System.Net.Http.Headers.HttpResponseHeaders?)null));
        using var response = new HttpResponseMessage();
        Assert.IsNull(LinkHeader.FindNext(response.Headers));
    }

    #endregion

    #region Walking pages

    private sealed record Page(List<Widget> Items);

    private static StubHandler Pages(params (string Body, string? Link)[] pages)
    {
        var handler = new StubHandler();
        foreach (var (body, link) in pages)
            handler.Respond(HttpStatusCode.OK, body, r => { if (link is not null) r.Headers.TryAddWithoutValidation("Link", link); });
        return handler;
    }

    private static string Items(params string[] names) => "[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"size\":1}}")) + "]";

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    [TestMethod]
    public async Task ThePagesAreFollowed_ThroughTheLinkHeader_UntilTheLastOne()
    {
        var handler = Pages(
            (Items("a", "b"), "<https://api.test/v1/widgets?page=2>; rel=\"next\""),
            (Items("c", "d"), "<https://api.test/v1/widgets?page=3>; rel=\"next\""),
            (Items("e"), null));
        var client = TestClients.Create(handler);

        var all = await Collect(client.ListAll<Widget>("widgets"));

        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, all.Select(w => w.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "/v1/widgets", "/v1/widgets?page=2", "/v1/widgets?page=3" }, handler.Requests.Select(r => r.Uri.PathAndQuery).ToArray());
    }

    [TestMethod]
    public async Task ARelativeLink_IsResolvedAgainstThePageJustRequested()
    {
        var handler = Pages((Items("a"), "</v1/other?cursor=abc>; rel=\"next\""), (Items("b"), null));
        var client = TestClients.Create(handler);

        await Collect(client.ListAll<Widget>("widgets"));

        Assert.AreEqual("https://api.test/v1/other?cursor=abc", handler.Requests[1].Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task EveryPage_CarriesTheCredentialsAndTheHeaders()
    {
        var handler = Pages((Items("a"), "<https://api.test/v1/widgets?page=2>; rel=\"next\""), (Items("b"), null));
        var client = TestClients.Create(handler, o => { o.TokenFactory = () => "tok"; o.Headers["X-Client"] = () => "me"; });

        await Collect(client.ListAll<Widget>("widgets"));

        Assert.IsTrue(handler.Requests.All(r => r.Header("Authorization") == "Bearer tok" && r.Header("X-Client") == "me"));
    }

    /// <summary>The next address comes from the server: it is untrusted input and gets the same checks as any call, so a hostile server cannot steer the walk (and the token) elsewhere.</summary>
    [TestMethod]
    [DataRow("<https://evil.test/steal>; rel=\"next\"")]
    [DataRow("<http://api.test/v1/widgets?page=2>; rel=\"next\"")]                   // downgrade to clear text
    [DataRow("<https://admin:pw@api.test/v1/widgets?page=2>; rel=\"next\"")]        // credentials in the address
    [DataRow("<ftp://api.test/v1/widgets>; rel=\"next\"")]
    public async Task AHostileNextLink_IsRefused_AndNothingIsSentThere(string link)
    {
        var handler = Pages((Items("a"), link), (Items("b"), null));
        var client = TestClients.Create(handler, o => o.TokenFactory = () => "tok");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Collect(client.ListAll<Widget>("widgets")));

        Assert.HasCount(1, handler.Requests);   // the first page only
    }

    /// <summary>A link with dot segments is normalized by <c>Uri</c> before anything else: the server picks a path, but on its own origin, like any other link.</summary>
    [TestMethod]
    public async Task ALinkWithDotSegments_IsNormalized_AndStaysOnTheOrigin()
    {
        var handler = Pages((Items("a"), "<https://api.test/v1/../other>; rel=\"next\""), (Items("b"), null));
        var client = TestClients.Create(handler);

        await Collect(client.ListAll<Widget>("widgets"));

        Assert.AreEqual("https://api.test/other", handler.Requests[1].Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task APageThatLoopsBack_IsDetected()
    {
        var handler = Pages((Items("a"), "<https://api.test/v1/widgets?page=2>; rel=\"next\""), (Items("b"), "<https://api.test/v1/widgets>; rel=\"next\""));
        var client = TestClients.Create(handler);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Collect(client.ListAll<Widget>("widgets")));

        StringAssert.Contains(ex.Message, "loops back");
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ASelfReferencingLink_IsDetected()
    {
        var handler = Pages((Items("a"), "<https://api.test/v1/widgets>; rel=\"next\""));
        var client = TestClients.Create(handler);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Collect(client.ListAll<Widget>("widgets")));
    }

    /// <summary>Hitting the guard must never truncate the list silently.</summary>
    [TestMethod]
    public async Task PastTheMaximumNumberOfPages_ItThrows_InsteadOfEndingQuietly()
    {
        var handler = new StubHandler();
        for (var i = 1; i <= 5; i++)
        {
            var next = i + 1;   // a copy: the lambda would otherwise see the final value of the loop variable
            handler.Respond(HttpStatusCode.OK, Items($"p{i}"), r => r.Headers.TryAddWithoutValidation("Link", $"<https://api.test/v1/widgets?page={next}>; rel=\"next\""));
        }
        var client = TestClients.Create(handler);
        var seen = new List<string>();

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in client.ListAll<Widget>("widgets", new PaginationOptions { MaxPages = 3 }))
                seen.Add(item.Name);
        });

        StringAssert.Contains(ex.Message, "more than 3 pages");
        CollectionAssert.AreEqual(new[] { "p1", "p2", "p3" }, seen);   // what was read is delivered before the failure
    }

    [TestMethod]
    public async Task ExactlyTheMaximumNumberOfPages_IsFine()
    {
        var handler = Pages((Items("a"), "<https://api.test/v1/widgets?page=2>; rel=\"next\""), (Items("b"), null));
        var client = TestClients.Create(handler);

        var all = await Collect(client.ListAll<Widget>("widgets", new PaginationOptions { MaxPages = 2 }));

        Assert.HasCount(2, all);
    }

    [TestMethod]
    public async Task AFailingPage_IsGivenAsAResult_ThenTheWalkEnds()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, Items("a"), r => r.Headers.TryAddWithoutValidation("Link", "<https://api.test/v1/widgets?page=2>; rel=\"next\""))
            .Respond(HttpStatusCode.NotFound);
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 0);

        var pages = await Collect(client.Pages<List<Widget>>("widgets"));

        Assert.HasCount(2, pages);
        Assert.IsTrue(pages[0].IsSuccess);
        Assert.AreEqual(HttpStatusCode.NotFound, pages[1].StatusCode);
    }

    [TestMethod]
    public async Task ListAll_ThrowsAnApiException_OnAFailingPage_AfterDeliveringTheEarlierItems()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, Items("a"), r => r.Headers.TryAddWithoutValidation("Link", "<https://api.test/v1/widgets?page=2>; rel=\"next\""))
            .Respond(HttpStatusCode.InternalServerError);
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 0);
        var seen = new List<string>();

        var ex = await Assert.ThrowsExactlyAsync<ApiException>(async () =>
        {
            await foreach (var item in client.ListAll<Widget>("widgets"))
                seen.Add(item.Name);
        });

        Assert.AreEqual(HttpStatusCode.InternalServerError, ex.StatusCode);
        CollectionAssert.AreEqual(new[] { "a" }, seen);
    }

    [TestMethod]
    public async Task StoppingEarly_StopsTheRequests()
    {
        var handler = new StubHandler();
        for (var i = 1; i <= 10; i++)
        {
            var next = i + 1;
            handler.Respond(HttpStatusCode.OK, Items($"p{i}"), r => r.Headers.TryAddWithoutValidation("Link", $"<https://api.test/v1/widgets?page={next}>; rel=\"next\""));
        }
        var client = TestClients.Create(handler);

        await foreach (var item in client.ListAll<Widget>("widgets"))
        {
            if (item.Name == "p2")
                break;
        }

        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task Cancellation_IsHonoured_BetweenPages()
    {
        var handler = new StubHandler();
        for (var i = 1; i <= 10; i++)
        {
            var next = i + 1;
            handler.Respond(HttpStatusCode.OK, Items($"p{i}"), r => r.Headers.TryAddWithoutValidation("Link", $"<https://api.test/v1/widgets?page={next}>; rel=\"next\""));
        }
        var client = TestClients.Create(handler);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in client.ListAll<Widget>("widgets", ct: cts.Token))
                cts.Cancel();
        });
    }

    [TestMethod]
    public async Task AnEmptyBody_IsAnEmptyPage()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "");

        var all = await Collect(TestClients.Create(handler).ListAll<Widget>("widgets"));

        Assert.IsEmpty(all);
    }

    #endregion

    #region Page numbers and cursors

    [TestMethod]
    public async Task PageNumbers_AreCounted_UntilAShortPage()
    {
        var handler = Pages((Items("a", "b"), null), (Items("c", "d"), null), (Items("e"), null), (Items("never"), null));
        var client = TestClients.Create(handler);

        var all = await Collect(client.ListAll<Widget>("widgets", new PaginationOptions { Style = PaginationStyle.PageNumber, PageSize = 2 }));

        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, all.Select(w => w.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, handler.Requests.Select(r => int.Parse(System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["page"]!)).ToArray());
        Assert.IsTrue(handler.Requests.All(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["per_page"] == "2"));
    }

    [TestMethod]
    public async Task PageNumbers_EndOnAnEmptyPage_WhenNoPageSizeIsKnown()
    {
        var handler = Pages((Items("a", "b"), null), (Items("c"), null), ("[]", null), (Items("never"), null));
        var client = TestClients.Create(handler);

        var all = await Collect(client.ListAll<Widget>("widgets", new PaginationOptions { Style = PaginationStyle.PageNumber }));

        Assert.HasCount(3, all);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    public async Task PageNumbers_CanStartAtZero_AndUseOtherParameterNames()
    {
        var handler = Pages((Items("a"), null), ("[]", null));
        var client = TestClients.Create(handler);

        await Collect(client.ListAll<Widget>("widgets", new PaginationOptions { Style = PaginationStyle.PageNumber, FirstPage = 0, PageParameter = "p", PageSize = 1, PageSizeParameter = "limit" }));

        StringAssert.Contains(handler.Requests[0].Uri.Query, "p=0");
        StringAssert.Contains(handler.Requests[0].Uri.Query, "limit=1");
        StringAssert.Contains(handler.Requests[1].Uri.Query, "p=1");
    }

    [TestMethod]
    public async Task APageSize_IsSent_AlsoWithTheLinkHeaderStyle_AndTheCallersParametersAreKept()
    {
        var handler = Pages((Items("a"), null));
        var client = TestClients.Create(handler);

        await Collect(client.ListAll<Widget>("widgets", new PaginationOptions { PageSize = 50 }, new Dictionary<string, string> { ["state"] = "open" }));

        StringAssert.Contains(handler.Requests[0].Uri.Query, "state=open");
        StringAssert.Contains(handler.Requests[0].Uri.Query, "per_page=50");
    }

    [TestMethod]
    public async Task ACursorInTheBody_DrivesTheWalk()
    {
        var handler = Pages(("""{"items":[{"name":"a","size":1}],"next":"c2"}""", null), ("""{"items":[{"name":"b","size":1}],"next":null}""", null));
        var client = TestClients.Create(handler);
        var names = new List<string>();

        await foreach (var page in client.Pages<NamedPage>("widgets", next: r => r.Result?.Next is { } c ? PageCursor.WithParameters(new Dictionary<string, string> { ["cursor"] = c }) : null))
            names.AddRange(page.EnsureSuccess().Result!.Items.Select(i => i.Name));

        CollectionAssert.AreEqual(new[] { "a", "b" }, names);
        Assert.IsFalse(handler.Requests[0].Uri.Query.Contains("cursor", StringComparison.Ordinal));
        StringAssert.Contains(handler.Requests[1].Uri.Query, "cursor=c2");
    }

    private sealed record NamedPage(List<Widget> Items, string? Next);

    [TestMethod]
    public void TheCursorFactories_ValidateTheirArguments()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PageCursor.ToUrl(" "));
        Assert.ThrowsExactly<ArgumentNullException>(() => PageCursor.ToUrl(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PageCursor.WithParameters(null!));
        Assert.AreEqual("/x", PageCursor.ToUrl("/x").Url);
        Assert.IsNull(PageCursor.ToUrl("/x").Parameters);
        Assert.AreEqual("2", PageCursor.WithParameters(new[] { KeyValuePair.Create("page", "2") }).Parameters!["page"]);
        Assert.IsNull(PageCursor.WithParameters(new[] { KeyValuePair.Create("page", "2") }).Url);
    }

    [TestMethod]
    public async Task InvalidPaginationSettings_AreRefused()
    {
        var client = TestClients.Create(new StubHandler());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(client.ListAll<Widget>("widgets", new PaginationOptions { MaxPages = 0 })));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(client.ListAll<Widget>("widgets", new PaginationOptions { PageSize = 0 })));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(client.Pages<Widget>("widgets", maxPages: 0)));
    }

    #endregion
}
