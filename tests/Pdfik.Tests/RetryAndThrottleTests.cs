using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;

namespace Pdfik.Tests
{
    public class RetryTests
    {
        [Fact]
        public async Task ServerError_RetriedTwiceThenSucceeds()
        {
            var handler = new MockHttpHandler()
                .Json(500, "{\"detail\":\"boom\"}")
                .Json(502, "bad gateway")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            var job = await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("queued", job.Status);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task ServerError_ThreeTimes_Throws()
        {
            var handler = new MockHttpHandler().Json(500, "{\"detail\":\"persistent\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(500, e.StatusCode);
            Assert.Equal("persistent", e.Message);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task TransportError_RetriedThenSucceeds()
        {
            var handler = new MockHttpHandler()
                .Throw("connection reset")
                .Throw("connection reset")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var job = await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("queued", job.Status);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task TransportError_ThreeTimes_ThrowsConnectionFailed()
        {
            var handler = new MockHttpHandler().Throw("connection reset");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(503, e.StatusCode);
            Assert.Equal("NETWORK_ERROR", e.ErrorCode);
            Assert.Contains("Connection failed", e.Message);
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public async Task DownloadBodyThatBreaksOff_IsANetworkError_NotResent()
        {
            // The status line arrived, then the connection dropped mid-body: HttpClient
            // buffers the body inside SendAsync, so this is the same transport failure.
            var handler = new MockHttpHandler().BrokenBody(200, "application/pdf");
            using var client = TestKit.Client(handler, out var delays);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.DownloadAsync("550e8400-e29b-41d4-a716-446655440000"));

            Assert.Equal(503, e.StatusCode);
            Assert.Equal("NETWORK_ERROR", e.ErrorCode);
            // Not resent: every started stream is one of the file's three download attempts.
            Assert.Single(handler.Requests);
            Assert.Empty(delays);
            // The transport failure stays reachable: HttpClient wraps the body's IOException.
            var transport = Assert.IsType<HttpRequestException>(e.InnerException);
            Assert.Equal("The response ended prematurely.", Assert.IsType<IOException>(transport.InnerException).Message);
        }

        // HttpClient refuses a non-ASCII header value only while it writes the request, as
        // an HttpRequestException: that read as a NETWORK_ERROR and was resent twice,
        // although no request had been sent. A real socket handler on a loopback listener,
        // so the refusal is HttpClient's own, not the mock's.
        [Theory]
        [InlineData("X-API-Key")]
        [InlineData("Idempotency-Key")]
        public async Task NonAsciiHeaderValue_IsThrownBeforeSending_NotANetworkError(string header)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                // A zero-width space, pasted along with the value.
                using var client = new PdfikClient(new PdfikClientOptions
                {
                    ApiKey = header == "X-API-Key" ? TestKit.ApiKey + "\u200B" : TestKit.ApiKey,
                    BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port,
                });
                var delays = new List<TimeSpan>();
                client.DelayFunc = (span, ct) =>
                {
                    delays.Add(span);
                    return Task.CompletedTask;
                };
                string idempotencyKey = header == "Idempotency-Key" ? "order-42\u200B" : null;

                var e = await Assert.ThrowsAsync<FormatException>(
                    () => client.UrlToPdfAsync("https://example.com", idempotencyKey: idempotencyKey));

                Assert.Contains(header, e.Message);
                Assert.Contains("U+200B", e.Message);
                Assert.Empty(delays);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task IdempotencyKey_ReusedAcrossRetries()
        {
            var handler = new MockHttpHandler()
                .Json(500, "oops")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(handler.Requests[0].Header("Idempotency-Key"), handler.Requests[1].Header("Idempotency-Key"));
        }

        [Fact]
        public async Task ClientError_NotRetried()
        {
            var handler = new MockHttpHandler().Json(400, "{\"detail\":\"bad request\",\"error\":\"VALIDATION\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(400, e.StatusCode);
            Assert.Single(handler.Requests);
        }
    }

    public class ThrottleTests
    {
        private const string RateLimited =
            "{\"type\":\"https://docs.pdfik.net/error-codes#rate-limit-exceeded\",\"detail\":\"Too many requests\",\"retry_after_seconds\":3}";
        private const string Concurrency =
            "{\"type\":\"https://docs.pdfik.net/error-codes#concurrency-limit-exceeded\",\"detail\":\"Too many in flight\"}";
        private const string QuotaExceeded =
            "{\"type\":\"https://docs.pdfik.net/error-codes#quota-exceeded\",\"detail\":\"Monthly quota exhausted\"}";

        [Fact]
        public async Task RateLimit429_WaitedOutThenSucceeds()
        {
            var handler = new MockHttpHandler()
                .Json(429, RateLimited)
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            var job = await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("queued", job.Status);
            Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);
        }

        [Fact]
        public async Task Concurrency429_IsAlsoAThrottle_DefaultDelay10s()
        {
            var handler = new MockHttpHandler()
                .Json(429, Concurrency)
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, delays);
        }

        [Fact]
        public async Task RetryAfterHeader_UsedWhenBodyHasNoSeconds()
        {
            var handler = new MockHttpHandler()
                .JsonWithRetryAfterHeader(429, Concurrency, "7")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, delays);
        }

        [Fact]
        public async Task RetryAfterSeconds_CappedAt60s()
        {
            var rate999 = RateLimited.Replace("\"retry_after_seconds\":3", "\"retry_after_seconds\":999");
            var handler = new MockHttpHandler()
                .Json(429, rate999)
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, delays);
        }

        [Fact]
        public async Task Quota429_RaisedAtOnce()
        {
            var handler = new MockHttpHandler().Json(429, QuotaExceeded);
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(429, e.StatusCode);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task SixthThrottle_RaisesThe429()
        {
            var handler = new MockHttpHandler().Json(429, RateLimited); // repeats forever
            using var client = TestKit.Client(handler, out var delays);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(429, e.StatusCode);
            Assert.Equal(5, delays.Count);            // five waits spent
            Assert.Equal(6, handler.Requests.Count);  // then the sixth answer is raised
        }

        [Fact]
        public async Task Throttle_DoesNotConsumeARetryAttempt()
        {
            // One throttle wait, then still THREE full attempts on 5xx.
            var handler = new MockHttpHandler()
                .Json(429, RateLimited)
                .Json(500, "a")
                .Json(500, "b")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var job = await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("queued", job.Status);
            Assert.Equal(4, handler.Requests.Count);
        }

        [Fact]
        public async Task DownloadBusy_WaitedOutOnDownload()
        {
            var busy = "{\"type\":\"https://docs.pdfik.net/error-codes#download-busy\",\"detail\":\"Another download is streaming\",\"retry_after_seconds\":2}";
            var handler = new MockHttpHandler()
                .Json(429, busy)
                .Bytes(200, new byte[] { 1, 2, 3 }, "application/pdf");
            using var client = TestKit.Client(handler, out var delays);

            var bytes = await client.DownloadAsync("job-1");

            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task DownloadBusy_RaisedAtOnceOnJobCreation()
        {
            var busy = "{\"type\":\"https://docs.pdfik.net/error-codes#download-busy\",\"detail\":\"n/a\"}";
            var handler = new MockHttpHandler().Json(429, busy);
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(429, e.StatusCode);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task ErrorCodeForm_AlsoRecognizedAsThrottle()
        {
            // Some bodies carry the code in "error" instead of the type URL fragment.
            var handler = new MockHttpHandler()
                .Json(429, "{\"error\":\"RATE_LIMIT_EXCEEDED\",\"retry_after_seconds\":1}")
                .Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler, out var delays);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        }

        [Fact]
        public async Task NonStringFieldsInErrorBody_SurfaceAsTheStatus_NotACastError()
        {
            // A proxy or edge can answer with its own JSON shapes; they must surface
            // as the PdfikException for the status, never as a JSON-cast exception.
            var handler = new MockHttpHandler().Json(429, "{\"error\":429,\"type\":17,\"detail\":{\"reason\":\"edge\"}}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(429, e.StatusCode);
            Assert.Null(e.ErrorCode);
            Assert.Equal("{\"reason\":\"edge\"}", e.Message);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task NonObjectJsonBody_IsNotAThrottle_AndKeepsTheRawBody()
        {
            var handler = new MockHttpHandler().Json(429, "\"slow down\"");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(429, e.StatusCode);
            Assert.Equal("\"slow down\"", e.ResponseBody);
            Assert.Single(handler.Requests);
        }
    }
}
