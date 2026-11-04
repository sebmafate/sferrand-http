using SFerrand.Http.Internal;

namespace SFerrand.Http.Tests;

/// <summary>
/// Tests of the URI builder (an internal helper reached through <c>InternalsVisibleTo</c>): joining slashes, identifiers, query encoding and
/// rejection of invalid bases.
/// </summary>
[TestClass]
public class RequestUriTests
{
    [TestMethod]
    [DataRow("https://api.test/v1", "users", "https://api.test/v1/users")]
    [DataRow("https://api.test/v1/", "users", "https://api.test/v1/users")]
    [DataRow("https://api.test/v1", "/users", "https://api.test/v1/users")]
    [DataRow("https://api.test/v1/", "/users/", "https://api.test/v1/users/")]
    [DataRow("https://api.test", "users", "https://api.test/users")]
    [DataRow("https://api.test/orgs/acme", "repos", "https://api.test/orgs/acme/repos")]
    public void Resource_IsAlwaysAppendedToTheBase(string baseUri, string resource, string expected)
        => Assert.AreEqual(expected, RequestUri.Build(baseUri, resource).AbsoluteUri);

    [TestMethod]
    public void EmptyResource_TargetsTheBaseItself()
        => Assert.AreEqual("https://api.test/graphql", RequestUri.Build("https://api.test/graphql", "").AbsoluteUri);

    [TestMethod]
    public void AbsoluteResource_ReplacesTheBase()
        => Assert.AreEqual("https://other.test/a/b", RequestUri.Build("https://api.test/v1", "https://other.test/a/b").AbsoluteUri);

    [TestMethod]
    [DataRow("42", "https://api.test/v1/widgets/42")]
    [DataRow("owner/repo", "https://api.test/v1/widgets/owner/repo")]
    [DataRow("my widget", "https://api.test/v1/widgets/my%20widget")]
    [DataRow("a?b#c", "https://api.test/v1/widgets/a%3Fb%23c")]
    [DataRow("/slashes/", "https://api.test/v1/widgets/slashes")]
    [DataRow("", "https://api.test/v1/widgets")]
    [DataRow(null, "https://api.test/v1/widgets")]
    public void Id_BecomesEncodedPathSegments(string? id, string expected)
        => Assert.AreEqual(expected, RequestUri.Build("https://api.test/v1", "widgets", id).AbsoluteUri);

    [TestMethod]
    public void Parameters_AreEncodedAndOrdered()
    {
        var parameters = new Dictionary<string, string> { ["page"] = "2", ["q"] = "a b&c=d", ["param>3"] = "valeur 3", ["empty"] = null! };

        var uri = RequestUri.Build("https://api.test/v1", "search", parameters: parameters);

        Assert.AreEqual("https://api.test/v1/search?page=2&q=a%20b%26c%3Dd&param%3E3=valeur%203&empty=", uri.AbsoluteUri);
    }

    /// <summary>
    /// Security-relevant: a user-controlled value containing <c>&amp;</c>, <c>=</c> or <c>#</c> must not be able to add parameters or start a fragment.
    /// Those characters are chosen on purpose: <see cref="Uri"/> would normalize characters like <c>&gt;</c> by itself and hide a missing encoding.
    /// </summary>
    [TestMethod]
    public void ParameterNamesAndValues_CannotInjectOtherParameters()
    {
        // '&', '=' and '#' survive Uri normalization: only explicit encoding keeps them from changing the query structure.
        var parameters = new Dictionary<string, string> { ["a&b=c#d"] = "x&y=z#w" };

        var uri = RequestUri.Build("https://api.test/v1", "search", parameters: parameters);

        Assert.AreEqual("https://api.test/v1/search?a%26b%3Dc%23d=x%26y%3Dz%23w", uri.AbsoluteUri);
        Assert.AreEqual(string.Empty, uri.Fragment);
    }

    [TestMethod]
    public void Parameters_AreAddedAfterAnExistingQuery()
        => Assert.AreEqual(
            "https://api.test/v1/search?sort=asc&page=2",
            RequestUri.Build("https://api.test/v1", "search?sort=asc", parameters: new Dictionary<string, string> { ["page"] = "2" }).AbsoluteUri);

    [TestMethod]
    public void IdAndParameters_Combine()
        => Assert.AreEqual(
            "https://api.test/v1/widgets/7?expand=owner",
            RequestUri.Build("https://api.test/v1", "widgets", "7", new Dictionary<string, string> { ["expand"] = "owner" }).AbsoluteUri);

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void BlankBase_Throws(string baseUri)
        => Assert.ThrowsExactly<ArgumentException>(() => RequestUri.Build(baseUri, "x"));

    /// <summary>
    /// On Unix <c>Uri.TryCreate("/v1", UriKind.Absolute)</c> succeeds and yields <c>file:///v1</c>: only http(s) bases are accepted.
    /// </summary>
    [TestMethod]
    [DataRow("/v1")]
    [DataRow("v1")]
    [DataRow("ftp://files.test")]
    [DataRow("file:///tmp")]
    public void NonHttpBase_Throws(string baseUri)
        => Assert.ThrowsExactly<ArgumentException>(() => RequestUri.Build(baseUri, "x"));

    [TestMethod]
    [DataRow("ftp://files.test/x")]
    [DataRow("file:///etc/passwd")]
    [DataRow("javascript://x")]
    [DataRow("s3://bucket/key")]
    [DataRow("HTTP+X://host/x")]
    public void AnAddressWithAnotherScheme_IsRefused_NotAppendedToTheBase(string resource)
        => Assert.ThrowsExactly<ArgumentException>(() => RequestUri.Build("https://api.test/v1", resource));

    /// <summary>Only a scheme at the very start makes an address: a path that merely contains <c>://</c> (in a query, say) is still a path.</summary>
    [TestMethod]
    [DataRow("redirect?to=ftp://files.test/x", "https://api.test/v1/redirect?to=ftp://files.test/x")]
    [DataRow("a/b://c", "https://api.test/v1/a/b://c")]
    [DataRow("1x://y", "https://api.test/v1/1x://y")]
    [DataRow("://x", "https://api.test/v1/://x")]
    public void APathContainingASchemeSeparator_IsStillAPath(string resource, string expected)
        => Assert.AreEqual(expected, RequestUri.Build("https://api.test/v1", resource).OriginalString);

    [TestMethod]
    public void NullResource_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(() => RequestUri.Build("https://api.test", null!));
}
