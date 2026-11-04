using SFerrand.Http.Internal;

namespace SFerrand.Http.Tests;

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

    [TestMethod]
    [DataRow("/v1")]
    [DataRow("v1")]
    [DataRow("ftp://files.test")]
    [DataRow("file:///tmp")]
    public void NonHttpBase_Throws(string baseUri)
        => Assert.ThrowsExactly<ArgumentException>(() => RequestUri.Build(baseUri, "x"));

    [TestMethod]
    public void NullResource_Throws()
        => Assert.ThrowsExactly<ArgumentNullException>(() => RequestUri.Build("https://api.test", null!));
}
