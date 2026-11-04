using System.Net;
using System.Text.Json;
using SFerrand.Http.Tests.Support;
using SFerrand.Http.Testing;

namespace SFerrand.Http.Tests;

/// <summary>Typed errors: <see cref="ProblemDetails"/> (RFC 9457), <see cref="ApiException"/> and <c>EnsureSuccess</c>, and the GraphQL envelope.</summary>
[TestClass]
public class ErrorsTests
{
    private const string Problem = """{"type":"https://api.test/errors/validation","title":"Validation failed","status":422,"detail":"name is required","instance":"/widgets/42","errors":{"name":["required"]},"traceId":"abc"}""";

    private static async Task<HttpResult<Widget>> Fail(HttpStatusCode status, string? body, string contentType = "application/problem+json")
    {
        var handler = new StubHandler().Respond(status, body, r =>
        {
            if (body is not null)
                r.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
        });
        return await TestClients.Create(handler, o => o.Retry.MaxRetries = 0).Get<Widget>("widgets");
    }

    #region ProblemDetails

    [TestMethod]
    public void AProblemDocument_IsRead_WithItsStandardMembersAndExtensions()
    {
        Assert.IsTrue(ProblemDetails.TryParse(Problem, out var problem));

        Assert.AreEqual("https://api.test/errors/validation", problem!.Type);
        Assert.AreEqual("Validation failed", problem.Title);
        Assert.AreEqual(422, problem.Status);
        Assert.AreEqual("name is required", problem.Detail);
        Assert.AreEqual("/widgets/42", problem.Instance);
        Assert.AreEqual("abc", problem.Extensions["traceId"].GetString());
        Assert.AreEqual("required", problem.Extensions["errors"].GetProperty("name")[0].GetString());
    }

    /// <summary>Any one of the five standard members is enough to recognize a problem document.</summary>
    [TestMethod]
    [DataRow("""{"type":"about:blank"}""")]
    [DataRow("""{"title":"t"}""")]
    [DataRow("""{"status":404}""")]
    [DataRow("""{"detail":"d"}""")]
    [DataRow("""{"instance":"/x"}""")]
    public void OneStandardMember_IsEnough(string body)
        => Assert.IsTrue(ProblemDetails.TryParse(body, out _));

    /// <summary>A number that does not fit an int must not make the parser throw: it never throws.</summary>
    [TestMethod]
    [DataRow("""{"title":"t","status":12345678901}""")]
    [DataRow("""{"title":"t","status":400.5}""")]
    [DataRow("""{"title":"t","status":1e400}""")]
    public void AnOutOfRangeOrFractionalStatus_IsIgnoredWithoutThrowing(string body)
    {
        Assert.IsTrue(ProblemDetails.TryParse(body, out var problem));

        Assert.IsNull(problem!.Status);
        Assert.AreEqual("t", problem.Title);
    }

    [TestMethod]
    public void TheMessage_FallsBackToTheDetail_WhenThereIsNoTitle()
    {
        var ex = new ApiException(HttpStatusCode.Conflict, "Conflict", null, new ProblemDetails { Detail = "the name is taken" });

        StringAssert.Contains(ex.Message, "the name is taken");
        StringAssert.Contains(ex.Message, "409");
    }

    [TestMethod]
    public void TheMemberNames_AreMatchedWithoutRegardToCase()
    {
        Assert.IsTrue(ProblemDetails.TryParse("""{"Title":"T","STATUS":400}""", out var problem));

        Assert.AreEqual("T", problem!.Title);
        Assert.AreEqual(400, problem.Status);
    }

    /// <summary>A JSON error object of some other shape is not a problem document: pretending otherwise would hide its content behind empty fields.</summary>
    [TestMethod]
    [DataRow("""{"error":"nope","code":12}""")]
    [DataRow("{}")]
    [DataRow("[1,2]")]
    [DataRow("\"text\"")]
    [DataRow("not json")]
    [DataRow("{\"title\":")]
    [DataRow("")]
    [DataRow("   ")]
    public void ABodyThatIsNotAProblemDocument_IsNotOne(string body)
        => Assert.IsFalse(ProblemDetails.TryParse(body, out _));

    [TestMethod]
    public void NullContent_IsNotAProblem()
        => Assert.IsFalse(ProblemDetails.TryParse(null, out _));

    /// <summary>Members of the wrong type are ignored rather than failing: the document is hostile or sloppy, and still better read than lost.</summary>
    [TestMethod]
    public void MembersOfTheWrongType_AreIgnored()
    {
        Assert.IsTrue(ProblemDetails.TryParse("""{"title":42,"status":"400","detail":null,"type":["x"]}""", out var problem));

        Assert.IsNull(problem!.Title);
        Assert.IsNull(problem.Status);
        Assert.IsNull(problem.Detail);
        Assert.IsNull(problem.Type);
    }

    [TestMethod]
    public void ADocumentNestedTooDeep_IsRefusedInsteadOfExhaustingTheStack()
    {
        var deep = "{\"title\":\"t\",\"x\":" + string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000)) + "}";

        Assert.IsFalse(ProblemDetails.TryParse(deep, out _));
    }

    #endregion

    #region HttpResult.Problem and EnsureSuccess

    [TestMethod]
    public async Task AnErrorResult_ExposesItsProblem_AndItsContentType()
    {
        var result = await Fail(HttpStatusCode.UnprocessableEntity, Problem);

        Assert.AreEqual("application/problem+json", result.ContentType);
        Assert.AreEqual("Validation failed", result.Problem!.Title);
        Assert.AreSame(result.Problem, result.Problem);   // parsed once
    }

    [TestMethod]
    public async Task AProblemSentAsPlainJson_IsRecognizedToo()
    {
        var result = await Fail(HttpStatusCode.BadRequest, """{"title":"Bad","status":400}""", "application/json");

        Assert.AreEqual("Bad", result.Problem!.Title);
    }

    [TestMethod]
    public async Task ASuccess_HasNoProblem_EvenIfItsBodyLooksLikeOne()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"title":"a widget titled like a problem","name":"w","size":1}""");

        var result = await TestClients.Create(handler).Get<Widget>("widgets");

        Assert.IsNull(result.Problem);
    }

    [TestMethod]
    public async Task AnErrorWithoutABody_HasNoProblem()
    {
        var result = await Fail(HttpStatusCode.NotFound, null);

        Assert.IsNull(result.Problem);
    }

    [TestMethod]
    public async Task EnsureSuccess_ReturnsTheResult_OnASuccess()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"name":"w","size":3}""");

        var result = await TestClients.Create(handler).Get<Widget>("widgets");

        Assert.AreSame(result, result.EnsureSuccess());
        Assert.AreEqual(new Widget("w", 3), result.EnsureSuccess().Result);
    }

    [TestMethod]
    public async Task EnsureSuccess_Throws_WithTheStatusTheBodyAndTheProblem()
    {
        var result = await Fail(HttpStatusCode.UnprocessableEntity, Problem);

        var ex = Assert.ThrowsExactly<ApiException>(() => result.EnsureSuccess());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
        Assert.AreEqual(Problem, ex.Content);
        Assert.AreEqual("Validation failed", ex.Problem!.Title);
        StringAssert.Contains(ex.Message, "422");
        StringAssert.Contains(ex.Message, "Validation failed");
    }

    /// <summary>The raw body can hold personal data or secrets, and exception messages end up in logs: it stays on the exception, out of the message.</summary>
    [TestMethod]
    public async Task TheMessage_NeverContainsTheRawBody()
    {
        var result = await Fail(HttpStatusCode.InternalServerError, "stack trace with password=hunter2", "text/plain");

        var ex = Assert.ThrowsExactly<ApiException>(() => result.EnsureSuccess());

        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.AreEqual("stack trace with password=hunter2", ex.Content);
    }

    [TestMethod]
    public void TheMessage_IsOneShortLine_WhateverTheServerWrote()
    {
        var problem = new ProblemDetails { Title = "line one\r\nline two " + new string('x', 500) };

        var ex = new ApiException(HttpStatusCode.BadRequest, "Bad\r\nRequest", null, problem);

        Assert.DoesNotContain("\r", ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
        Assert.IsLessThan(300, ex.Message.Length);
        StringAssert.EndsWith(ex.Message, "...");
    }

    [TestMethod]
    public void WithoutAnyResponse_TheMessageSaysSo()
    {
        var ex = Assert.ThrowsExactly<ApiException>(() => HttpResult<Widget>.Fail.EnsureSuccess());

        StringAssert.Contains(ex.Message, "did not answer");
        Assert.AreEqual(0, (int)ex.StatusCode);
    }

    #endregion

    #region GraphQL

    private static TestClient GraphQlClient(StubHandler handler)
        => TestClients.Create(handler, o => o.GraphQLUri = "https://api.test/graphql");

    private static readonly GraphQLQuery<object> Query = new() { Query = "{ widget { name } }" };

    [TestMethod]
    public async Task AGraphQlAnswer_IsReadAsAnEnvelope()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"data":{"name":"w","size":3}}""");

        var result = await GraphQlClient(handler).Query<Widget, object>(Query);

        Assert.IsFalse(result.Result!.HasErrors);
        Assert.AreEqual(new Widget("w", 3), result.Result.EnsureNoErrors());
    }

    /// <summary>The trap: the status is 200 and <c>IsSuccess</c> is true, yet a field failed.</summary>
    [TestMethod]
    public async Task ErrorsInA200_AreSurfaced()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK,
            """{"data":null,"errors":[{"message":"Cannot find widget","locations":[{"line":1,"column":3}],"path":["widget",0],"extensions":{"code":"NOT_FOUND"}}]}""");

        var result = await GraphQlClient(handler).Query<Widget, object>(Query);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Result!.HasErrors);
        var error = result.Result.Errors!.Single();
        Assert.AreEqual("Cannot find widget", error.Message);
        Assert.AreEqual(1, error.Locations!.Single().Line);
        Assert.AreEqual(3, error.Locations!.Single().Column);
        Assert.AreEqual("widget", error.Path![0].GetString());
        Assert.AreEqual(0, error.Path[1].GetInt32());
        Assert.AreEqual("NOT_FOUND", error.Extensions!["code"].GetString());

        var ex = Assert.ThrowsExactly<GraphQLException>(() => result.Result.EnsureNoErrors());
        StringAssert.Contains(ex.Message, "Cannot find widget");
        Assert.HasCount(1, ex.Errors);
    }

    [TestMethod]
    public async Task PartialData_IsKept_NextToTheErrors()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"data":{"name":"w","size":3},"errors":[{"message":"a"},{"message":"b"}]}""");

        var result = await GraphQlClient(handler).Query<Widget, object>(Query);

        Assert.AreEqual(new Widget("w", 3), result.Result!.Data);
        Assert.HasCount(2, result.Result.Errors!);
        StringAssert.Contains(Assert.ThrowsExactly<GraphQLException>(() => result.Result.EnsureNoErrors()).Message, "2 errors");
    }

    [TestMethod]
    public async Task AnEmptyErrorsList_IsNotAnError()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"data":{"name":"w","size":3},"errors":[]}""");

        var result = await GraphQlClient(handler).Query<Widget, object>(Query);

        Assert.IsFalse(result.Result!.HasErrors);
        Assert.AreEqual(new Widget("w", 3), result.Result.EnsureNoErrors());
    }

    [TestMethod]
    public async Task AGraphQlAnswerWithErrors_IsNotRetried()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, """{"errors":[{"message":"a"}]}""");

        await GraphQlClient(handler).Query<Widget, object>(Query);

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public void AGraphQlException_ShortensAndFlattensTheMessage()
    {
        var ex = new GraphQLException(new[] { new GraphQLError { Message = "bad\r\nthing " + new string('y', 400) } });

        Assert.DoesNotContain("\n", ex.Message);
        Assert.IsLessThan(300, ex.Message.Length);
        Assert.AreEqual("The GraphQL server reported an error: ", ex.Message[..38]);
    }

    [TestMethod]
    public void AGraphQlErrorWithoutAMessage_IsStillReported()
        => StringAssert.Contains(new GraphQLException(new[] { new GraphQLError() }).Message, "no message");

    #endregion
}
