using System.Net;
using System.Text;
using SFerrand.Http.Tests.Support;

namespace SFerrand.Http.Tests;

/// <summary>Form and multipart bodies, and streamed downloads.</summary>
[TestClass]
public class BodiesTests
{
    private const string WidgetJson = """{"name":"w","size":3}""";

    #region Form

    [TestMethod]
    public async Task AForm_IsSentUrlEncoded_WithItsContentType()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler);

        await client.Form<Widget>("token", new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "read write", ["note"] = "a&b=c" });

        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("application/x-www-form-urlencoded", request.Header("Content-Type"));
        Assert.AreEqual("grant_type=client_credentials&scope=read+write&note=a%26b%3Dc", request.Body);
    }

    /// <summary>A name or a value that contains <c>&amp;</c> or <c>=</c> must not be able to add or change another field.</summary>
    [TestMethod]
    public async Task FormFieldsCannotInjectOtherFields()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);

        await TestClients.Create(handler).Form<Widget>("x", new[] { KeyValuePair.Create("a&admin", "true&role=root"), KeyValuePair.Create("é ü", "日本") });

        Assert.AreEqual("a%26admin=true%26role%3Droot&%C3%A9+%C3%BC=%E6%97%A5%E6%9C%AC", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task FormFields_KeepTheirOrder_Duplicates_AndEmptyValues()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);

        await TestClients.Create(handler).Form<Widget>("x", new[] { KeyValuePair.Create("b", "2"), KeyValuePair.Create("a", ""), KeyValuePair.Create("b", "3") });

        Assert.AreEqual("b=2&a=&b=3", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task AnEmptyForm_IsAnEmptyBody()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);

        await TestClients.Create(handler).Form<Widget>("x", Array.Empty<KeyValuePair<string, string>>());

        Assert.AreEqual(string.Empty, handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task ANullFormIsRefused_AndAFormIsNotRetriedByDefaultButReplayedWithAKey()
    {
        var client = TestClients.Create(new StubHandler());
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => client.Form<Widget>("x", null!));

        var plain = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        await TestClients.Create(plain).Form<Widget>("x", new[] { KeyValuePair.Create("a", "1") });
        Assert.HasCount(1, plain.Requests);

        var keyed = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        await TestClients.Create(keyed, o => o.Retry.IdempotencyKeyHeader = "Idempotency-Key").Form<Widget>("x", new[] { KeyValuePair.Create("a", "1") });
        Assert.HasCount(2, keyed.Requests);
        Assert.AreEqual(keyed.Requests[0].Body, keyed.Requests[1].Body);
    }

    #endregion

    #region Multipart

    [TestMethod]
    public async Task AMultipartForm_IsSentWithItsBoundary_FieldsAndFiles()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, WidgetJson);
        var form = new MultipartForm().AddField("title", "Quarterly report").AddFile("file", "report.txt", Encoding.UTF8.GetBytes("hello"), "text/plain");

        await TestClients.Create(handler).Multipart<Widget>("documents", form);

        var request = handler.Requests.Single();
        var contentType = request.Header("Content-Type")!;
        StringAssert.StartsWith(contentType, "multipart/form-data; boundary=");
        var boundary = contentType["multipart/form-data; boundary=".Length..].Trim('"');
        Assert.IsNotNull(request.Body);
        StringAssert.Contains(request.Body, $"--{boundary}");
        StringAssert.Contains(request.Body, "name=title");
        StringAssert.Contains(request.Body, "Quarterly report");
        StringAssert.Contains(request.Body, "name=file");
        StringAssert.Contains(request.Body, "filename=report.txt");
        StringAssert.Contains(request.Body, "Content-Type: text/plain");
        StringAssert.Contains(request.Body, "hello");
        StringAssert.EndsWith(request.Body.TrimEnd(), $"--{boundary}--");
    }

    [TestMethod]
    public async Task ABinaryFile_IsSentUnchanged()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        // The stub keeps the body as text, which would mangle the bytes: look at the bytes the form builds.
        var built = await new MultipartForm().AddFile("f", "all.bin", all).BuildAsync(CancellationToken.None);
        var received = built.Bytes;
        var index = IndexOf(received, [13, 10, 13, 10]) + 4;   // after the part headers
        CollectionAssert.AreEqual(all, received[index..(index + 256)]);
    }

    private static int IndexOf(byte[] source, byte[] pattern)
    {
        for (var i = 0; i <= source.Length - pattern.Length; i++)
        {
            if (source.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }

        return -1;
    }

    /// <summary>The body is built once: every attempt sends the same bytes, boundary included, otherwise a replayed upload would differ from the first.</summary>
    [TestMethod]
    public async Task ARetriedMultipart_ReplaysTheSameBytes()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, WidgetJson);
        var client = TestClients.Create(handler, o => o.Retry.IdempotencyKeyHeader = "Idempotency-Key");

        await client.Multipart<Widget>("documents", new MultipartForm().AddField("a", "1"));

        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.AreEqual(handler.Requests[0].Header("Content-Type"), handler.Requests[1].Header("Content-Type"));
    }

    /// <summary>The names are written between quotes without escaping: one that contains a quote or a line break would forge headers of the part.</summary>
    [TestMethod]
    [DataRow("a\"b")]
    [DataRow("a\r\nContent-Type: text/html")]
    [DataRow("a\\b")]
    [DataRow("")]
    public void AFieldOrFileNameThatCouldForgePartHeaders_IsRefused(string name)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MultipartForm().AddField(name, "v"));
        Assert.ThrowsExactly<ArgumentException>(() => new MultipartForm().AddFile(name, "f.txt", [1]));
        Assert.ThrowsExactly<ArgumentException>(() => new MultipartForm().AddFile("f", name, [1]));
    }

    [TestMethod]
    public void MultipartArguments_AreValidated()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new MultipartForm().AddField("a", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new MultipartForm().AddFile("a", "f", null!));
        Assert.ThrowsExactly<ArgumentException>(() => new MultipartForm().AddFile("a", "f", [1], "not a media type"));
    }

    [TestMethod]
    public async Task ANullMultipartForm_IsRefused()
        => await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => TestClients.Create(new StubHandler()).Multipart<Widget>("x", null!));

    [TestMethod]
    public async Task AnEmptyMultipartForm_IsJustTheClosingBoundary()
    {
        var built = await new MultipartForm().BuildAsync(CancellationToken.None);

        StringAssert.StartsWith(Encoding.UTF8.GetString(built.Bytes), "--");
    }

    #endregion

    #region Download

    private static StubHandler Serving(byte[] bytes) => new StubHandler().Respond(HttpStatusCode.OK, null, r => r.Content = new ByteArrayContent(bytes));

    /// <summary>A stream that produces some bytes, then fails: the way a connection drops in the middle of a download.</summary>
    private sealed class DroppingStream(int bytesBeforeFailure) : Stream
    {
        private int _remaining = bytesBeforeFailure;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
                throw new IOException("connection reset");
            var n = Math.Min(buffer.Length, _remaining);
            buffer[..n].Fill(7);
            _remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task ADownload_IsWrittenToTheStream_AndTheByteCountIsReturned()
    {
        var payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var destination = new MemoryStream();

        var result = await TestClients.Create(Serving(payload)).Download("files/1", destination);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(300_000L, result.Result);
        Assert.IsNull(result.Content);
        CollectionAssert.AreEqual(payload, destination.ToArray());
    }

    [TestMethod]
    public async Task AnErrorStatus_IsAResult_AndTheStreamIsLeftUntouched()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.NotFound, "no such file");
        using var destination = new MemoryStream();

        var result = await TestClients.Create(handler).Download("files/1", destination);

        Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
        Assert.AreEqual("no such file", result.Content);
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task ADownload_IsRetriedLikeAGet_WhileNothingWasWritten()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.ServiceUnavailable).Respond(HttpStatusCode.OK, null, r => r.Content = new ByteArrayContent(new byte[] { 1, 2, 3 }));
        using var destination = new MemoryStream();

        var result = await TestClients.Create(handler).Download("files/1", destination);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, destination.ToArray());
    }

    /// <summary>Once bytes are in the destination a replay would write them twice: the failure is final, and says how far the download got.</summary>
    [TestMethod]
    public async Task AFailureAfterTheFirstBytes_IsNotRetried_AndSaysHowFarItGot()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new DroppingStream(1000)));
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsExactlyAsync<DownloadInterruptedException>(() => TestClients.Create(handler).Download("files/1", destination));

        Assert.AreEqual(1000, ex.BytesWritten);
        StringAssert.Contains(ex.Message, "1000 bytes");
        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual(1000, destination.Length);
    }

    [TestMethod]
    public async Task AFailureBeforeAnyByte_IsRetried_AndTheDestinationIsWrittenOnce()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new DroppingStream(0)))
            .Respond(HttpStatusCode.OK, null, r => r.Content = new ByteArrayContent(new byte[] { 9 }));
        using var destination = new MemoryStream();

        var result = await TestClients.Create(handler).Download("files/1", destination);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, handler.Requests);
        CollectionAssert.AreEqual(new byte[] { 9 }, destination.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ABodyOverTheLimit_IsRefused_AnnouncedOrNot(bool announced)
    {
        var handler = announced
            ? Serving(new byte[5_000])
            : new StubHandler().Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new DroppingStream(5_000)));
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(() => TestClients.Create(handler).Download("files/1", destination, maxBytes: 1_000));

        Assert.AreEqual(1_000, ex.Limit);
        Assert.HasCount(1, handler.Requests);
        if (announced)
            Assert.AreEqual(0, destination.Length);   // refused before reading
        Assert.IsLessThanOrEqualTo(1_000L, destination.Length);
    }

    [TestMethod]
    public async Task TheOptionsLimit_IsTheDefault_AndNullRemovesIt()
    {
        using var destination = new MemoryStream();

        await Assert.ThrowsExactlyAsync<HttpResponseTooLargeException>(
            () => TestClients.Create(Serving(new byte[5_000]), o => o.MaxDownloadBytes = 1_000).Download("files/1", destination));

        var result = await TestClients.Create(Serving(new byte[5_000]), o => o.MaxDownloadBytes = null).Download("files/1", new MemoryStream());
        Assert.AreEqual(5_000L, result.Result);
    }

    [TestMethod]
    public async Task ABodyExactlyAtTheLimit_IsAccepted()
    {
        var result = await TestClients.Create(Serving(new byte[1_000])).Download("files/1", new MemoryStream(), maxBytes: 1_000);

        Assert.AreEqual(1_000L, result.Result);
    }

    [TestMethod]
    public async Task TheCallersCancellation_DuringADownload_IsACancellation()
    {
        var handler = new StubHandler().Respond(HttpStatusCode.OK, null, r => r.Content = new StreamContent(new DroppingStream(10)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => TestClients.Create(handler).Download("files/1", new MemoryStream(), ct: cts.Token));
    }

    [TestMethod]
    public async Task InvalidDownloadArguments_AreRefused()
    {
        var client = TestClients.Create(new StubHandler());

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => client.Download("x", null!));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Download("x", new MemoryStream(new byte[1], writable: false)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => client.Download("x", new MemoryStream(), maxBytes: 0));
        Assert.ThrowsExactly<ArgumentException>(() => TestClients.Create(new StubHandler(), o => o.MaxDownloadBytes = 0));
    }

    [TestMethod]
    public async Task ADownloadFollowsRedirects_LikeAnyCall()
    {
        var handler = new StubHandler()
            .Respond(HttpStatusCode.Found, null, r => r.Headers.Location = new Uri("/v1/files/real", UriKind.Relative))
            .Respond(HttpStatusCode.OK, null, r => r.Content = new ByteArrayContent(new byte[] { 5, 6 }));
        using var destination = new MemoryStream();

        await TestClients.Create(handler).Download("files/1", destination);

        CollectionAssert.AreEqual(new byte[] { 5, 6 }, destination.ToArray());
        Assert.AreEqual("/v1/files/real", handler.Requests[1].Uri.AbsolutePath);
    }

    #endregion
}
