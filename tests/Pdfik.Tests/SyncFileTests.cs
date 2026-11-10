using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Pdfik.Models;
using Xunit;

namespace Pdfik.Tests
{
    public class SyncFileTests
    {
        private static readonly byte[] Pdf = { 0x25, 0x50, 0x44, 0x46, 0x2d };   // %PDF-
        private static readonly byte[] Png = { 0x89, 0x50, 0x4e, 0x47 };

        [Fact]
        public async Task UrlToPdfFile_ReturnsBytes_AndInjectsWaitTrue()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler);

            var bytes = await client.UrlToPdfFileAsync("https://example.com");

            Assert.Equal(Pdf, bytes);
            Assert.Equal("/url-to-pdf", handler.Requests[0].Uri.AbsolutePath);
            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("https://example.com", (string)body["url"]);
            Assert.True((bool)body["wait"]);
        }

        [Fact]
        public void HtmlToPdfFile_Works()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler);

            var bytes = client.HtmlToPdfFile("<p>x</p>");

            Assert.Equal(Pdf, bytes);
            Assert.True((bool)JsonNode.Parse(handler.Requests[0].Body)["wait"]);
        }

        [Fact]
        public async Task MarkdownToPdfFile_Works()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler);

            await client.MarkdownToPdfFileAsync("# hi");

            Assert.Equal("/markdown-to-pdf", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task UrlToImageFile_Works()
        {
            var handler = new MockHttpHandler().Bytes(200, Png, "image/png");
            using var client = TestKit.Client(handler);

            var bytes = await client.UrlToImageFileAsync("https://example.com");

            Assert.Equal(Png, bytes);
            Assert.Equal("/url-to-image", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task HtmlToImageFile_Works()
        {
            var handler = new MockHttpHandler().Bytes(200, Png, "image/png");
            using var client = TestKit.Client(handler);

            await client.HtmlToImageFileAsync("<p>x</p>");

            Assert.Equal("/html-to-image", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task EinvoiceToPdfFile_Works()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler);

            await client.EinvoiceToPdfFileAsync("<xml/>");

            Assert.Equal("/einvoice-to-pdf", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task Delivery_RejectedBeforeAnyHttp_OnEveryFileMethod()
        {
            var handler = new MockHttpHandler();
            using var client = TestKit.Client(handler);
            var delivery = new DeliveryOptions("https://bucket.example/out?sig=1");

            await Assert.ThrowsAsync<ArgumentException>(
                () => client.UrlToPdfFileAsync(new UrlToPdfRequest("https://example.com") { Delivery = delivery }));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.HtmlToPdfFileAsync(new HtmlToPdfRequest("<p>x</p>") { Delivery = delivery }));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.MarkdownToPdfFileAsync(new MarkdownToPdfRequest("# x") { Delivery = delivery }));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.UrlToImageFileAsync(new UrlToImageRequest("https://example.com") { Delivery = delivery }));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.HtmlToImageFileAsync(new HtmlToImageRequest("<p>x</p>") { Delivery = delivery }));
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.EinvoiceToPdfFileAsync(new EInvoiceToPdfRequest("<xml/>") { Delivery = delivery }));

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task SyncTimeout503_RaisedWithoutResend()
        {
            var handler = new MockHttpHandler().Json(503,
                "{\"type\":\"https://docs.pdfik.net/error-codes#sync-timeout\"," +
                "\"detail\":\"The render did not finish within the synchronous budget.\",\"error\":\"SYNC_TIMEOUT\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfFileAsync("https://example.com"));

            Assert.Equal(503, e.StatusCode);
            Assert.Equal("SYNC_TIMEOUT", e.ErrorCode);
            Assert.Single(handler.Requests); // deliberately never resent
        }

        [Fact]
        public async Task ServerError500_NotResentOnSyncPath()
        {
            var handler = new MockHttpHandler().Json(500, "boom");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.HtmlToPdfFileAsync("<p>x</p>"));

            Assert.Equal(500, e.StatusCode);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task TransportError_NotResentOnSyncPath()
        {
            var handler = new MockHttpHandler().Throw("reset");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.HtmlToPdfFileAsync("<p>x</p>"));

            Assert.Equal(503, e.StatusCode);
            Assert.Equal("NETWORK_ERROR", e.ErrorCode);
            Assert.Contains("Connection failed", e.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task JobFailed422_CarriesTheJobsErrorCode()
        {
            var handler = new MockHttpHandler().Json(422,
                "{\"type\":\"https://docs.pdfik.net/error-codes#job-failed\"," +
                "\"detail\":\"The page did not load in time.\",\"error\":\"PAGE_TIMEOUT\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfFileAsync("https://example.com"));

            Assert.Equal(422, e.StatusCode);
            Assert.Equal("PAGE_TIMEOUT", e.ErrorCode);
        }

        [Fact]
        public async Task OldServer202Json_RaisesSyncModeUnsupported_NamingTheJob()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfFileAsync("https://example.com"));

            Assert.Equal("SYNC_MODE_UNSUPPORTED", e.ErrorCode);
            Assert.Contains("550e8400-e29b-41d4-a716-446655440000", e.Message);
        }

        [Fact]
        public async Task JsonContentTypeOn200_AlsoUnsupported()
        {
            var handler = new MockHttpHandler().Json(200, "{\"job_id\":\"j-9\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfFileAsync("https://example.com"));

            Assert.Equal("SYNC_MODE_UNSUPPORTED", e.ErrorCode);
            Assert.Contains("j-9", e.Message);
        }

        [Fact]
        public async Task Throttle429_WaitedOutUnderTheSameIdempotencyKey()
        {
            var handler = new MockHttpHandler()
                .Json(429, "{\"type\":\"https://docs.pdfik.net/error-codes#rate-limit-exceeded\",\"retry_after_seconds\":1}")
                .Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler, out var delays);

            var bytes = await client.UrlToPdfFileAsync("https://example.com");

            Assert.Equal(Pdf, bytes);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
            Assert.Equal(handler.Requests[0].Header("Idempotency-Key"), handler.Requests[1].Header("Idempotency-Key"));
        }

        [Fact]
        public async Task ClientSideTimeout_SurfacesAsConnectionFailure_WithoutResend()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            handler.ResponseDelay = TimeSpan.FromMilliseconds(300);
            // The effective sync timeout is max(RequestTimeout, SyncRequestTimeout),
            // so BOTH must shrink for the test.
            using var client = new PdfikClient(new PdfikClientOptions
            {
                ApiKey = TestKit.ApiKey,
                BaseUrl = TestKit.BaseUrl,
                HttpMessageHandler = handler,
                RequestTimeout = TimeSpan.FromMilliseconds(50),
            });
            client.DelayFunc = (_, __) => Task.CompletedTask;
            client.SyncRequestTimeout = TimeSpan.FromMilliseconds(50);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfFileAsync("https://example.com"));

            Assert.Equal(503, e.StatusCode);
            Assert.Equal("NETWORK_ERROR", e.ErrorCode);
            Assert.Contains("Connection failed", e.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task CallerRequestObject_IsNotMutatedByWaitInjection()
        {
            var handler = new MockHttpHandler().Bytes(200, Pdf, "application/pdf");
            using var client = TestKit.Client(handler);
            var request = new UrlToPdfRequest("https://example.com") { Test = true };

            await client.UrlToPdfFileAsync(request);

            // "wait" lives only in the serialized body; the model has no such member.
            Assert.True((bool)JsonNode.Parse(handler.Requests[0].Body)["wait"]);
            Assert.True(request.Test);
            Assert.Null(typeof(UrlToPdfRequest).GetProperty("Wait"));
        }
    }
}
