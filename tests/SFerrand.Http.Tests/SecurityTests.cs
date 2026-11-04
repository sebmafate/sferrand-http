using System.Net;
using System.Text;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>
/// Security regression tests: each one pins a way the client could be made to leak a credential, reach an address it was not configured for,
/// forge a request, write secrets to a log or exhaust memory. They were written before the fixes and fail on the code that did not have them.
/// </summary>
[TestClass]
public class SecurityTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    private static (TestClient Client, StubHandler Handler) Create(Action<HttpClientOptions>? configure = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        return (TestClients.Create(handler, o => { o.TokenFactory = () => "secret-token"; configure?.Invoke(o); }, logger), handler);
    }

    #region Credentials only go to origins the application declared

    /// <summary>
    /// SSRF and credential theft: a resource written by a user (<c>"https://evil.test/x"</c>) replaced the base address, and the token and the
    /// default headers went along with it.
    /// </summary>
    [TestMethod]
    [DataRow("https://evil.test/steal")]
    [DataRow("https://api.test.evil.test/v1/x")]      // a host that merely starts like the trusted one
    [DataRow("https://api.test:8443/v1/x")]           // same host, other port
    [DataRow("http://api.test/v1/x")]                 // same host, other scheme
    public async Task AnAbsoluteResourceOnAnotherOrigin_IsRefused_AndNothingIsSent(string resource)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>(resource));

        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>
    /// Same host, same port, other scheme: the origin differs, so the token must not follow from <c>https</c> to a clear-text <c>http</c> on the same port.
    /// (The other cases above differ by port as well, which would hide a comparison that forgot the scheme.)
    /// </summary>
    [TestMethod]
    public async Task ADowngradeToClearTextOnTheSameHostAndPort_IsRefused()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => { o.BaseUri = "https://api.test:8080/v1"; o.TokenFactory = () => "secret-token"; });

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("http://api.test:8080/v1/x"));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task AnAbsoluteResourceOnTheSameOrigin_IsAllowed()
    {
        var (client, handler) = Create();

        var result = await client.Get<Widget>("https://api.test/v1/other");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Bearer secret-token", handler.Requests.Single().Header("Authorization"));
    }

    [TestMethod]
    public async Task APerCallBaseUri_OnAnUndeclaredOrigin_IsRefused()
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("x", baseUri: "https://evil.test"));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task ADeclaredTrustedOrigin_IsAllowed_AndTheOriginComparisonIgnoresCaseAndDefaultPorts()
    {
        var (client, handler) = Create(o => o.TrustedOrigins.Add("https://Uploads.TEST:443"));

        await client.Get<Widget>("x", baseUri: "https://uploads.test");
        await client.Get<Widget>("https://UPLOADS.test/y");

        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task TheGraphQlEndpoint_IsTrustedWithoutBeingDeclaredTwice()
    {
        var (client, handler) = Create(o => o.GraphQLUri = "https://graph.test/graphql");

        await client.GraphQL<Widget, object>(new GraphQLQuery<object> { Query = "{ a }" });

        Assert.AreEqual("https://graph.test/graphql", handler.Requests.Single().Uri.ToString());
    }

    /// <summary>
    /// A redirect is followed by the HTTP stack, behind the client's back. A response that comes from an origin the application never declared
    /// is not returned as if it were the answer of the API.
    /// </summary>
    [TestMethod]
    public async Task AResponseThatCameFromAnUndeclaredOrigin_IsRefused()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson, r => r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.test/landing"));
        var client = TestClients.Create(handler);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.Get<Widget>("widgets"));
        Assert.HasCount(1, handler.Requests);   // not retried: it is a policy violation, not a transient failure
    }

    [TestMethod]
    public void InvalidTrustedOrigins_AreRejectedWhenTheClientIsBuilt()
    {
        Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.TrustedOrigins.Add(null!)));
        Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.TrustedOrigins.Add("not a url")));
        Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.TrustedOrigins.Add("ftp://files.test")));
    }

    #endregion

    #region No clear text

    [TestMethod]
    public void PlainHttp_ToANonLoopbackHost_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.BaseUri = "http://api.test/v1"));

    [TestMethod]
    [DataRow("http://localhost:5000")]
    [DataRow("http://127.0.0.1:5000/api")]
    [DataRow("http://[::1]:5000")]
    public void PlainHttp_ToTheLoopbackInterface_IsAllowed(string baseUri)
        => TestClients.Create(new StubHandler(), o => o.BaseUri = baseUri);

    [TestMethod]
    public void PlainHttp_CanBeAllowedExplicitly()
        => TestClients.Create(new StubHandler(), o => { o.BaseUri = "http://api.internal/v1"; o.AllowInsecureHttp = true; });

    /// <summary>Declaring a clear-text origin as trusted does not make it acceptable: the configuration is refused when the client is built, not at the first call.</summary>
    [TestMethod]
    public void ATrustedOrigin_InClearText_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => Create(o => o.TrustedOrigins.Add("http://files.test")));

    [TestMethod]
    public async Task APerCallPlainHttpBaseUri_ToAnUndeclaredHost_IsRefused()
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("x", baseUri: "http://files.test"));

        Assert.IsEmpty(handler.Requests);
    }

    #endregion

    #region Secrets in addresses, messages and logs

    [TestMethod]
    public void CredentialsInTheBaseUri_AreRefused_WithoutEchoingThem()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.BaseUri = "https://admin:hunter2@api.test/v1?key=abc123"));

        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("abc123", ex.Message);
    }

    [TestMethod]
    public void AnInvalidBaseUri_NeverEchoesItsSecrets()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.BaseUri = "ftp://admin:hunter2@api.test/v1?token=abc123"));

        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("abc123", ex.Message);
    }

    [TestMethod]
    public async Task CredentialsInAnAbsoluteResource_AreRefused()
    {
        var (client, handler) = Create();

        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("https://admin:hunter2@api.test/v1/x"));

        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task Logs_NeverContainUserInfoOrQuery()
    {
        var logger = new ListLogger();
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, logger: logger);

        await client.Get<Widget>("widgets", parameters: new Dictionary<string, string> { ["access_token"] = "s3cr3t" });

        Assert.IsTrue(logger.Messages.All(m => !m.Contains("s3cr3t") && !m.Contains('@')));
    }

    [TestMethod]
    public async Task AnInvalidAbsoluteResource_NeverEchoesItsSecrets()
    {
        var (client, _) = Create();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.Get<Widget>("https://api.test/v1/x#frag?token=abc123"));

        Assert.DoesNotContain("abc123", ex.Message);
    }

    #endregion

    #region Header and request forging

    /// <summary>
    /// A header value is added without validation (so unusual formats work): a value containing a line break must be refused here,
    /// before it can become a second header, or a second request, on the wire.
    /// </summary>
    [TestMethod]
    [DataRow("ok\r\nX-Injected: 1")]
    [DataRow("ok\nX-Injected: 1")]
    [DataRow("ok\rX-Injected: 1")]
    [DataRow("ok\0")]
    public async Task APerCallHeaderValue_WithControlCharacters_IsRefused(string value)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Trace"] = value }));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task ADefaultHeaderFactory_ReturningControlCharacters_IsRefused()
    {
        var (client, handler) = Create(o => o.Headers["X-Trace"] = () => "a\r\nX-Injected: 1");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("widgets"));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task ATokenWithControlCharacters_IsRefused_WithoutEchoingIt()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.TokenFactory = () => "abc\r\nX-Injected: 1");

        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("widgets"));

        Assert.DoesNotContain("abc", ex.Message);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public void AUserAgentWithControlCharacters_IsRefusedWhenTheClientIsBuilt()
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.UserAgent = "app\r\nX-Injected: 1"));

    /// <summary>A header whose name is not valid used to be dropped silently (the result of the add was ignored): a missing header is a silent failure.</summary>
    [TestMethod]
    [DataRow("X Bad")]
    [DataRow("X-Bad:")]
    [DataRow("X-Bad\r\n")]
    [DataRow("")]
    [DataRow("é-header")]
    public async Task AHeaderWithAnInvalidName_IsRefused_NotSilentlyDropped(string name)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { [name] = "v" }));

        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>Headers that frame the message (length, encoding, connection, host) belong to the HTTP stack: setting them is how request smuggling starts.</summary>
    [TestMethod]
    [DataRow("Host")]
    [DataRow("host")]
    [DataRow("Content-Length")]
    [DataRow("Transfer-Encoding")]
    [DataRow("Connection")]
    [DataRow("Upgrade")]
    [DataRow("TE")]
    [DataRow("Trailer")]
    [DataRow("Keep-Alive")]
    [DataRow("Proxy-Connection")]
    public async Task HeadersThatFrameTheMessage_AreRefused(string name)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Post<Widget>("widgets", new Widget("w", 1), new Dictionary<string, string> { [name] = "x" }));

        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>A horizontal tab is legal inside a header value; every other control character, and DEL, is not.</summary>
    [TestMethod]
    public async Task ATabInAHeaderValue_IsAllowed_ButDelIsNot()
    {
        var (client, handler) = Create();

        await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Trace"] = "a\tb" });
        Assert.AreEqual("a\tb", handler.Requests.Single().Header("X-Trace"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Trace"] = "a\u007fb" }));
        Assert.HasCount(1, handler.Requests);
    }

    /// <summary>The characters RFC 9110 allows in a header name, other than letters and digits, are accepted one by one; the delimiters are not.</summary>
    [TestMethod]
    public async Task HeaderNames_FollowTheHttpTokenGrammar()
    {
        foreach (var special in "!#$%&'*+-.^_`|~")
        {
            var (client, handler) = Create();
            var name = $"X{special}Y";

            await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { [name] = "v" });

            Assert.AreEqual("v", handler.Requests.Single().Header(name), $"'{special}' must be allowed in a header name");
        }

        foreach (var delimiter in "\"(),/:;<=>?@[\\]{} \t")
        {
            var (client, handler) = Create();

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => client.Get<Widget>("widgets", headers: new Dictionary<string, string> { [$"X{delimiter}Y"] = "v" }),
                $"'{delimiter}' must not be allowed in a header name");
            Assert.IsEmpty(handler.Requests);
        }
    }

    [TestMethod]
    public async Task OrdinaryHeaders_StillWork()
    {
        var (client, handler) = Create(o => o.Headers["X-Client"] = () => "me");

        await client.Get<Widget>("widgets", headers: new Dictionary<string, string> { ["X-Trace-Id"] = "abc-123", ["Accept"] = "application/json" });

        var request = handler.Requests.Single();
        Assert.AreEqual("abc-123", request.Header("X-Trace-Id"));
        Assert.AreEqual("me", request.Header("X-Client"));
    }

    #endregion

    #region Path traversal

    /// <summary>
    /// <c>Uri</c> removes dot segments: an identifier typed by a user (<c>"../../admin"</c>) climbed out of the resource and reached another endpoint
    /// with the application's credentials.
    /// </summary>
    [TestMethod]
    [DataRow("..")]
    [DataRow("../admin")]
    [DataRow("../../admin/users")]
    [DataRow("a/../b")]
    [DataRow("a/./b")]
    [DataRow(".")]
    [DataRow("..\\admin")]
    public async Task AnIdentifierWithDotSegments_IsRefused(string id)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("widgets", id));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    [DataRow("../admin")]
    [DataRow("a/../../b")]
    [DataRow("./x")]
    [DataRow("a\\..\\b")]
    [DataRow("%2e%2e/admin")]
    [DataRow("%2E%2E/admin")]
    [DataRow(".%2e/admin")]
    [DataRow("a/%2e/b")]
    public async Task AResourceWithDotSegments_IsRefused(string resource)
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>(resource));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task AFragmentInAResource_IsRefused_BecauseItWouldSwallowTheIdentifier()
    {
        var (client, handler) = Create();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Get<Widget>("widgets#frag", "42"));

        Assert.IsEmpty(handler.Requests);
    }

    /// <summary>Dots that are not a whole segment, and encoded dots, are ordinary data: they stay escaped and cannot traverse.</summary>
    [TestMethod]
    [DataRow("v1.2", "/v1/widgets/v1.2")]
    [DataRow("a..b", "/v1/widgets/a..b")]
    [DataRow("...", "/v1/widgets/...")]
    [DataRow("%2e%2e", "/v1/widgets/%252e%252e")]
    [DataRow("..%2f..", "/v1/widgets/..%252f..")]
    [DataRow(".hidden", "/v1/widgets/.hidden")]
    public async Task DotsThatAreNotWholeSegments_AreOrdinaryData(string id, string expectedPath)
    {
        var (client, handler) = Create();

        await client.Get<Widget>("widgets", id);

        Assert.AreEqual(expectedPath, handler.Requests.Single().Uri.AbsolutePath);
    }

    #endregion

    #region Memory

    private static StubHandler BigResponse(long bytes, bool announceLength, HttpStatusCode status = HttpStatusCode.OK)
        => new StubHandler().Respond(status, null, r =>
        {
            r.Content = announceLength
                ? new ByteArrayContent(new byte[bytes])
                : new StreamContent(new ZeroStream(bytes));
            r.Content.Headers.ContentType = new("application/json");
        });

    /// <summary>A stream of zeros of a given length that does not announce it: the way a hostile server drips an endless body.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _remaining);
            buffer[..n].Clear();
            _remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ABodyLargerThanTheLimit_IsRefused_AndNotRetried(bool announced)
    {
        var handler = BigResponse(2_000_000, announced);
        var client = TestClients.Create(handler, o => o.MaxResponseBytes = 1_000_000);

        var ex = await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(() => client.Get<string>("widgets"));

        Assert.AreEqual(1_000_000, ex.Limit);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task AnErrorBodyLargerThanTheLimit_IsRefusedToo()
    {
        var handler = BigResponse(2_000_000, announceLength: false, HttpStatusCode.BadRequest);
        var client = TestClients.Create(handler, o => o.MaxResponseBytes = 1_000_000);

        await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(() => client.Get<string>("widgets"));
    }

    [TestMethod]
    public async Task ABodyExactlyAtTheLimit_IsAccepted()
    {
        var handler = BigResponse(1_000, announceLength: true);
        var client = TestClients.Create(handler, o => o.MaxResponseBytes = 1_000);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(1_000, result.Content!.Length);
    }

    [TestMethod]
    public async Task WithoutALimit_ALargeBodyIsAccepted()
    {
        var handler = BigResponse(3_000_000, announceLength: false);
        var client = TestClients.Create(handler, o => o.MaxResponseBytes = null);

        var result = await client.Get<string>("widgets");

        Assert.AreEqual(3_000_000, result.Content!.Length);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void ANonPositiveLimit_IsRefusedWhenTheClientIsBuilt(long limit)
        => Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.MaxResponseBytes = limit));

    /// <summary>A body that announces a size over the limit is refused without being read at all.</summary>
    private sealed class AnnouncedButNeverRead(long announced) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = announced;
            return true;
        }
    }

    [TestMethod]
    public async Task AnAnnouncedSizeOverTheLimit_IsRefused_WithoutReadingTheBody()
    {
        var content = new AnnouncedButNeverRead(5_000_000);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(() => HttpResult<string>.FromResponseAsync(response, maxContentBytes: 1_000_000));

        Assert.IsFalse(content.WasRead);
    }

    [TestMethod]
    public async Task TheLimit_AlsoAppliesToHttpResultReadDirectly()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(5_000)) };

        await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(
            () => HttpResult<string>.FromResponseAsync(response, maxContentBytes: 1_000));
    }

    /// <summary>A charset the runtime does not know must not fail the call: JSON APIs speak UTF-8.</summary>
    [TestMethod]
    [DataRow("no-such-charset")]
    [DataRow("\"utf-8\"")]      // quoted, which the header grammar allows
    [DataRow("")]
    public async Task AnUnknownOrOddCharset_FallsBackToUtf8(string charset)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("café")) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", $"text/plain; charset={charset}");

        var result = await HttpResult<string>.FromResponseAsync(response, maxContentBytes: 1_000);

        Assert.AreEqual("café", result.Content);
    }

    [TestMethod]
    public async Task TheBodyIsDecodedWithItsDeclaredCharset_WithinTheLimit()
    {
        var latin1 = Encoding.Latin1.GetBytes("café");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(latin1) };
        response.Content.Headers.ContentType = new("text/plain") { CharSet = "iso-8859-1" };

        var result = await HttpResult<string>.FromResponseAsync(response, maxContentBytes: 1_000);

        Assert.AreEqual("café", result.Content);
    }

    #endregion
}
