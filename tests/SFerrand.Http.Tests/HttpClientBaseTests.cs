using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

[TestClass]
public class HttpClientBaseTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    #region Construction

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("/relative")]
    [DataRow("ftp://files.test")]
    public void Constructor_RejectsInvalidBaseUri(string baseUri)
    {
        var options = new HttpClientOptions { BaseUri = baseUri };

        Assert.ThrowsExactly<ArgumentException>(() => new TestClient(options, new SimpleHttpClientFactory()));
    }

    [TestMethod]
    public void Constructor_RejectsNullArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TestClient((HttpClientBase)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TestClient(TestClients.Options(), null!));
    }

    [TestMethod]
    public async Task CopyConstructor_SharesConfigurationAndAuthentication()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var parent = TestClients.Create(handler, o => o.TokenFactory = () => "shared-token");

        var child = new TestClient(parent);
        await child.Get<Widget>("widgets");

        Assert.AreEqual("Bearer shared-token", handler.Requests.Single().Header("Authorization"));
        Assert.AreEqual("https://api.test/v1/widgets", handler.Requests.Single().Uri.AbsoluteUri);
    }

    #endregion

    #region GET / LIST

    [TestMethod]
    public async Task Get_BuildsTheUri_AndDeserializesTheBody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets", "42", new Dictionary<string, string> { ["q"] = "a b", ["page"] = "2" });

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(new Widget("w", 3), result.Result);
        Assert.AreEqual(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.AreEqual("https://api.test/v1/widgets/42?q=a%20b&page=2", handler.Requests.Single().Uri.AbsoluteUri);
        Assert.IsNull(handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task Get_WithBaseUriOverride_TargetsAnotherHost()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        await client.Get<Widget>("uploads", baseUri: "https://uploads.test");

        Assert.AreEqual("https://uploads.test/uploads", handler.Requests.Single().Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task List_DeserializesAJsonArray()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """[{"name":"a","size":1},{"name":"b","size":2}]""");
        var client = TestClients.Create(handler);

        var result = await client.List<Widget>("widgets", new Dictionary<string, string> { ["per_page"] = "2" });

        CollectionAssert.AreEqual(new[] { new Widget("a", 1), new Widget("b", 2) }, result.Result!.ToArray());
        Assert.AreEqual("https://api.test/v1/widgets?per_page=2", handler.Requests.Single().Uri.AbsoluteUri);
    }

    [TestMethod]
    public async Task Get_OnErrorStatus_ReturnsAFailedResult_InsteadOfThrowing()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.NotFound, """{"message":"nope"}""");
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets", "404");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
        Assert.AreEqual("""{"message":"nope"}""", result.Content);
        Assert.IsNull(result.Result);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task Get_OnMalformedSuccessBody_ThrowsWithoutRetrying()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "<html/>");
        var client = TestClients.Create(handler);

        await Assert.ThrowsAsync<JsonException>(() => client.Get<Widget>("widgets"));
        Assert.HasCount(1, handler.Requests);
    }

    #endregion

    #region Bodies and verbs

    [TestMethod]
    public async Task Post_SendsAJsonBody_WithWebDefaults()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.Created, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Post<Widget>("widgets", new Widget("w", 3));

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(WidgetJson, request.Body);
        Assert.AreEqual("application/json; charset=utf-8", request.Header("Content-Type"));
        Assert.AreEqual(HttpStatusCode.Created, result.StatusCode);
    }

    [TestMethod]
    public async Task Post_UsesTheRuntimeTypeOfTheBody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "{}");
        var client = TestClients.Create(handler);
        object body = new { id = 7, tags = new[] { "a", "b" } };

        await client.Post<object>("things", body);

        Assert.AreEqual("""{"id":7,"tags":["a","b"]}""", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task Post_WithoutBody_SendsNoContent()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "{}");
        var client = TestClients.Create(handler);

        await client.Post<object>("widgets/42/archive");

        Assert.IsNull(handler.Requests.Single().Body);
        Assert.IsNull(handler.Requests.Single().Header("Content-Type"));
    }

    [TestMethod]
    public async Task Put_AndPatch_SendTheBodyToTheIdentifiedResource()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        await client.Put<Widget>("widgets", new Widget("w", 3), "42");
        await client.Patch<Widget>("widgets", new { size = 4 }, "42");

        Assert.AreEqual(HttpMethod.Put, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.IsTrue(handler.Requests.All(r => r.Uri.AbsoluteUri == "https://api.test/v1/widgets/42"));
        Assert.AreEqual("""{"size":4}""", handler.Requests[1].Body);
    }

    [TestMethod]
    public void Put_AndPatch_RequireABody()
    {
        var client = TestClients.Create(new StubHandler());

        Assert.ThrowsExactly<ArgumentNullException>(() => client.Put<Widget>("widgets", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => client.Patch<Widget>("widgets", null!));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NoContent, true)]
    [DataRow(HttpStatusCode.OK, true)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    public async Task Delete_ReturnsWhetherTheCallSucceeded(HttpStatusCode status, bool expected)
    {
        var handler = new StubHandler().Respond(status);
        var client = TestClients.Create(handler);

        Assert.AreEqual(expected, await client.Delete("widgets", "42"));

        Assert.AreEqual(HttpMethod.Delete, handler.Requests.Single().Method);
        Assert.IsNull(handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task Delete_ToleratesANonJsonSuccessBody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "deleted");
        var client = TestClients.Create(handler);

        Assert.IsTrue(await client.Delete("widgets", "42"));
    }

    [TestMethod]
    public async Task Delete_CanCarryABody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.NoContent);
        var client = TestClients.Create(handler);

        await client.Delete("widgets", body: new { ids = new[] { 1, 2 } });

        Assert.AreEqual("""{"ids":[1,2]}""", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task OtherVerbs_AreAvailableThroughSendAsync()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK);
        var client = TestClients.Create(handler);

        await client.Head("widgets");

        Assert.AreEqual(HttpMethod.Head, handler.Requests.Single().Method);
    }

    #endregion

    #region GraphQL

    [TestMethod]
    public async Task GraphQL_PostsTheQueryToTheGraphQLEndpoint()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"data":{"viewer":"me"}}""");
        var client = TestClients.Create(handler, o => o.GraphQLUri = "https://api.test/graphql");

        var result = await client.GraphQL<JsonElement, object>(new GraphQLQuery<object> { Query = "query($id:ID!){node(id:$id){id}}", Variables = new { id = "1" } });

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://api.test/graphql", request.Uri.AbsoluteUri);
        Assert.AreEqual("""{"query":"query($id:ID!){node(id:$id){id}}","variables":{"id":"1"}}""", request.Body);
        Assert.AreEqual("me", result.Result.GetProperty("data").GetProperty("viewer").GetString());
    }

    [TestMethod]
    public async Task GraphQL_EndpointCanBeGivenPerCall()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "{}");
        var client = TestClients.Create(handler);

        await client.GraphQL<JsonElement, object>(new GraphQLQuery<object> { Query = "{a}" }, "https://other.test/gql");

        Assert.AreEqual("https://other.test/gql", handler.Requests.Single().Uri.AbsoluteUri);
    }

    [TestMethod]
    public void GraphQL_WithoutAnyEndpoint_Throws()
    {
        var client = TestClients.Create(new StubHandler());

        Assert.ThrowsExactly<InvalidOperationException>(() => client.GraphQL<JsonElement, object>(new GraphQLQuery<object> { Query = "{a}" }));
    }

    #endregion

    #region Authentication and headers

    [TestMethod]
    public async Task TokenFactory_IsEvaluatedForEveryRequest()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var calls = 0;
        var client = TestClients.Create(handler, o => o.TokenFactory = () => $"t{++calls}");

        await client.Get<Widget>("widgets");
        await client.Get<Widget>("widgets");

        Assert.AreEqual("Bearer t1", handler.Requests[0].Header("Authorization"));
        Assert.AreEqual("Bearer t2", handler.Requests[1].Header("Authorization"));
    }

    [TestMethod]
    public async Task AsyncTokenFactory_TakesPrecedence_AndSchemeIsConfigurable()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o =>
        {
            o.AuthenticationScheme = "token";
            o.TokenFactory = () => "sync";
            o.AsyncTokenFactory = async ct => { await Task.Yield(); return "async"; };
        });

        await client.Get<Widget>("widgets");

        Assert.AreEqual("token async", handler.Requests.Single().Header("Authorization"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task EmptyToken_SendsNoAuthorizationHeader(string? token)
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TokenFactory = () => token);

        await client.Get<Widget>("widgets");

        Assert.IsNull(handler.Requests.Single().Header("Authorization"));
    }

    [TestMethod]
    public async Task Headers_AreSetPerRequest_SoASharedHttpClientNeverAccumulatesThem()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var counter = 0;
        var client = TestClients.Create(handler, o =>
        {
            o.TokenFactory = () => "tok";
            o.UserAgent = "my-agent/1.0";
            o.Headers["X-Request-Number"] = () => (++counter).ToString();
        });

        for (var i = 0; i < 3; i++)
            await client.Get<Widget>("widgets");

        foreach (var request in handler.Requests)
        {
            Assert.AreEqual("Bearer tok", request.Header("Authorization"));
            Assert.AreEqual("my-agent/1.0", request.Header("User-Agent"));
        }
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, handler.Requests.Select(r => r.Header("X-Request-Number")).ToArray());
    }

    [TestMethod]
    public async Task PerCallHeaders_ReplaceTheDefaultOnes()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.Headers["X-Mode"] = () => "default");

        await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["x-mode"] = "override", ["Accept"] = "application/vnd.test+json" });

        Assert.AreEqual("override", handler.Requests.Single().Header("X-Mode"));
        Assert.AreEqual("application/vnd.test+json", handler.Requests.Single().Header("Accept"));
    }

    [TestMethod]
    public async Task PerCallContentHeaders_GoOnTheContent()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, "{}");
        var client = TestClients.Create(handler);

        await client.Post<object>("widgets", new Widget("w", 1), new Dictionary<string, string> { ["Content-Type"] = "application/vnd.api+json" });

        Assert.AreEqual("application/vnd.api+json", handler.Requests.Single().Header("Content-Type"));
    }

    [TestMethod]
    public async Task ContentHeaders_AreIgnoredForRequestsWithoutABody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" });

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(handler.Requests.Single().Header("Content-Type"));
    }

    [TestMethod]
    public async Task UserAgent_DefaultsToAnAssemblyName()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        await client.Get<Widget>("widgets");

        Assert.IsFalse(string.IsNullOrWhiteSpace(handler.Requests.Single().Header("User-Agent")));
    }

    [TestMethod]
    public async Task HttpVersion_IsAPreference_NotARequirement()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.HttpVersion = HttpVersion.Version20);

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task NamedClient_IsRequestedFromTheFactory()
    {
        var factory = new RecordingFactory(new StubHandler().Respond(HttpStatusCode.OK, WidgetJson));
        var client = new TestClient(TestClients.Options(o => o.HttpClientName = "github"), factory);

        await client.Get<Widget>("widgets");

        Assert.AreEqual("github", factory.LastName);
    }

    private sealed class RecordingFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    #endregion

    #region Retries

    [TestMethod]
    public async Task Get_RetriesTransientStatuses_UntilSuccess()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.BadGateway)
            .Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    public async Task Get_WhenRetriesAreExhausted_ReturnsTheLastFailure()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable, "down");
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 2);

        var result = await client.Get<Widget>("widgets");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.AreEqual("down", result.Content);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.NotFound)]
    public async Task Get_DoesNotRetryClientErrors(HttpStatusCode status)
    {
        var handler = new StubHandler().Respond(status);
        var client = TestClients.Create(handler);

        await client.Get<Widget>("widgets");

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task MaxRetriesZero_DisablesRetries()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable);
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 0);

        await client.Get<Widget>("widgets");

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task Post_IsNotRetriedByDefault()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.Created, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Post<Widget>("widgets", new Widget("w", 3));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task Post_CanBeRetried_WhenOptedIn()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.Created, WidgetJson);
        var client = TestClients.Create(handler, o => o.Retry.RetryNonIdempotentMethods = true);

        var result = await client.Post<Widget>("widgets", new Widget("w", 3));

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task Retried_PutRequests_ResendTheSameBody_AndFreshHeaders()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var calls = 0;
        var client = TestClients.Create(handler, o => o.TokenFactory = () => $"t{++calls}");

        await client.Put<Widget>("widgets", new Widget("w", 3), "42");

        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(WidgetJson, handler.Requests[0].Body);
        Assert.AreEqual(WidgetJson, handler.Requests[1].Body);
        Assert.AreEqual("Bearer t2", handler.Requests[1].Header("Authorization"));
    }

    [TestMethod]
    public async Task RetryAfterBeyondTheMaxDelay_ReturnsTheResponseImmediately()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.TooManyRequests, null, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)));
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task TransportErrors_AreRetried()
    {
        var handler = new StubHandler()
            .Throw(new HttpRequestException("connection reset"))
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    public async Task TransportErrors_AreRethrown_WhenRetriesAreExhausted()
    {
        var handler = new StubHandler().Throw(new HttpRequestException("connection reset"));
        var client = TestClients.Create(handler, o => o.Retry.MaxRetries = 1);

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.Get<Widget>("widgets"));

        Assert.AreEqual("connection reset", ex.Message);
        Assert.HasCount(2, handler.Requests);
    }

    #endregion

    #region Timeouts and cancellation

    [TestMethod]
    public async Task AttemptsThatTimeOut_AreRetried_ThenReportedAsTimeoutException()
    {
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler, o =>
        {
            o.RequestTimeout = TimeSpan.FromMilliseconds(40);
            o.Retry.MaxRetries = 1;
        });

        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.Get<Widget>("widgets"));

        StringAssert.Contains(ex.Message, "https://api.test/v1/widgets");
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task ATimeoutOnFirstAttempt_CanBeFollowedByASuccess()
    {
        var handler = new StubHandler().Hang().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.RequestTimeout = TimeSpan.FromMilliseconds(40));

        var result = await client.Get<Widget>("widgets");

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task CallerCancellation_StopsImmediately_WithoutRetrying()
    {
        var handler = new StubHandler().Hang();
        var client = TestClients.Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Get<Widget>("widgets", ct: cts.Token));

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task CallerCancellation_InterruptsTheRetryWait()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable);
        var client = TestClients.Create(handler, o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromMinutes(1);
            o.Retry.MaxDelay = TimeSpan.FromMinutes(1);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Get<Widget>("widgets", ct: cts.Token));

        Assert.HasCount(1, handler.Requests);
    }

    #endregion

    #region Logging

    [TestMethod]
    public async Task Logs_NeverContainTheQueryString()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var logger = new ListLogger();
        var client = TestClients.Create(handler, logger: logger);

        await client.Get<Widget>("widgets", parameters: new Dictionary<string, string> { ["access_token"] = "s3cr3t" });

        Assert.IsNotEmpty(logger.Messages);
        Assert.IsTrue(logger.Messages.All(m => !m.Contains("s3cr3t") && !m.Contains("access_token")));
        Assert.IsTrue(logger.Messages.Any(m => m.Contains("https://api.test/v1/widgets")));
    }

    #endregion
}
