using System.Net;
using System.Text;
using System.Text.Json;

namespace SFerrand.Http.Tests;

[TestClass]
public class HttpResultTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null)
        => new(status) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task Success_WithObjectBody_IsDeserialized()
    {
        using var response = Response(HttpStatusCode.OK, """{"name":"w","size":3}""");

        var result = await HttpResult<Support.Widget>.FromResponseAsync(response);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual("OK", result.ReasonPhrase);
        Assert.AreEqual(new Support.Widget("w", 3), result.Result);
        Assert.AreEqual("""{"name":"w","size":3}""", result.Content);
        Assert.IsNotNull(result.Headers);
    }

    [TestMethod]
    public async Task Success_WithJsonNullBody_HasNullResult()
    {
        using var response = Response(HttpStatusCode.OK, "null");

        var result = await HttpResult<object>.FromResponseAsync(response);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Result);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task Success_WithEmptyBody_HasNullResult(string body)
    {
        using var response = Response(HttpStatusCode.NoContent, body);

        var result = await HttpResult<Support.Widget>.FromResponseAsync(response);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Result);
    }

    [TestMethod]
    public async Task Error_KeepsStatusAndRawBody_WithoutDeserializing()
    {
        using var response = Response(HttpStatusCode.BadRequest, "definitely not json");

        var result = await HttpResult<Support.Widget>.FromResponseAsync(response);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.AreEqual("Bad Request", result.ReasonPhrase);
        Assert.AreEqual("definitely not json", result.Content);
        Assert.IsNull(result.Result);
    }

    [TestMethod]
    public async Task StringResult_KeepsTheRawBody_EvenWhenItIsNotJson()
    {
        using var response = Response(HttpStatusCode.OK, "plain text");

        var result = await HttpResult<string>.FromResponseAsync(response);

        Assert.AreEqual("plain text", result.Result);
    }

    [TestMethod]
    public async Task Success_WithInvalidJson_ThrowsJsonException()
    {
        using var response = Response(HttpStatusCode.OK, "<html>oops</html>");

        await Assert.ThrowsAsync<JsonException>(() => HttpResult<Support.Widget>.FromResponseAsync(response));
    }

    [TestMethod]
    public async Task Deserialization_UsesTheGivenOptions()
    {
        using var response = Response(HttpStatusCode.OK, """{"Name":"w","Size":3}""");
        var strict = new JsonSerializerOptions { PropertyNameCaseInsensitive = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var result = await HttpResult<Support.Widget>.FromResponseAsync(response, strict);

        Assert.IsNull(result.Result?.Name);
    }

    [TestMethod]
    public void Fail_And_Empty_AreSyntheticResults()
    {
        Assert.IsFalse(HttpResult<string>.Fail.IsSuccess);
        Assert.AreEqual(default, HttpResult<string>.Fail.StatusCode);
        Assert.IsNull(HttpResult<string>.Fail.Headers);

        Assert.IsTrue(HttpResult<string>.Empty.IsSuccess);
        Assert.AreEqual(HttpStatusCode.NoContent, HttpResult<string>.Empty.StatusCode);
        Assert.IsNull(HttpResult<string>.Empty.Result);
    }

    [TestMethod]
    public void CreateFromObject_WrapsTheValueInA200()
    {
        var result = HttpResult<Support.Widget>.CreateFromObject(new Support.Widget("w", 3));

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual("""{"name":"w","size":3}""", result.Content);
    }

    [TestMethod]
    public void ImplicitConversion_UnwrapsTheResult()
    {
        Support.Widget? widget = HttpResult<Support.Widget>.CreateFromObject(new Support.Widget("w", 3));

        Assert.AreEqual("w", widget?.Name);
    }
}
