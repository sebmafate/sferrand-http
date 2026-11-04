using System.Net;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

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

    [TestMethod]
    public async Task ConcurrentCreation_YieldsASingleClientPerName()
    {
        using var factory = new SimpleHttpClientFactory();

        var clients = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => factory.CreateClient("shared"))));

        Assert.HasCount(1, clients.Distinct());
    }
}
