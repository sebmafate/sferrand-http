using System.Net;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// Tests of <see cref="SimpleHttpClientFactory"/>: client caching per name, handler injection, ownership of the handler on dispose
/// and thread safety.
/// </summary>
[TestClass]
public class SimpleHttpClientFactoryTests
{
    [TestMethod]
    public void SameName_ReturnsTheSameClient()
    {
        using var factory = new SimpleHttpClientFactory();

        Assert.AreSame(factory.CreateClient("github"), factory.CreateClient("github"));
    }

    [TestMethod]
    public void DifferentNames_ReturnDifferentClients()
    {
        using var factory = new SimpleHttpClientFactory();

        Assert.AreNotSame(factory.CreateClient("a"), factory.CreateClient("b"));
    }

    [TestMethod]
    public void NamesAreCaseSensitive_LikeTheBuiltInFactory()
    {
        using var factory = new SimpleHttpClientFactory();

        Assert.AreNotSame(factory.CreateClient("GitHub"), factory.CreateClient("github"));
    }

    [TestMethod]
    public void UnnamedClient_IsTheEmptyNameClient()
    {
        using var factory = new SimpleHttpClientFactory();

        Assert.AreSame(factory.CreateClient(), factory.CreateClient(string.Empty));
        Assert.AreSame(factory.CreateClient(), factory.CreateClient(null!));
    }

    [TestMethod]
    public async Task Clients_UseTheGivenHandler()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.Accepted);
        using var factory = new SimpleHttpClientFactory(handler);

        using var response = await factory.CreateClient("x").GetAsync("https://api.test/ping");

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    /// <summary>
    /// Ownership rule: the factory disposes what it created (the clients) but not the handler the caller injected, which may be shared by other factories or inspected after the test.
    /// </summary>
    [TestMethod]
    public async Task Dispose_DisposesTheClients_ButNotTheHandlerItWasGiven()
    {
        var handler = new StubHandler();
        var factory = new SimpleHttpClientFactory(handler);
        var client = factory.CreateClient("x");

        factory.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("https://api.test/ping"));
        Assert.IsFalse(handler.IsDisposed);
    }

    [TestMethod]
    public async Task Dispose_CanBeCalledTwice_AndNewClientsAreRebuilt()
    {
        var factory = new SimpleHttpClientFactory(new StubHandler());
        var first = factory.CreateClient("x");

        factory.Dispose();
        factory.Dispose();
        var second = factory.CreateClient("x");

        Assert.AreNotSame(first, second);
        using var response = await second.GetAsync("https://api.test/ping");
        Assert.IsTrue(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// 64 threads asking for the same name must all get the same instance (no duplicated clients, no race on the dictionary).
    /// </summary>
    [TestMethod]
    public async Task ConcurrentCreation_YieldsASingleClientPerName()
    {
        using var factory = new SimpleHttpClientFactory();

        var clients = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => factory.CreateClient("shared"))));

        Assert.HasCount(1, clients.Distinct());
    }
}
