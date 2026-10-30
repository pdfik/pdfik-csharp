using System;
using System.Threading.Tasks;
using Pdfik.Models;
using Xunit;

namespace Pdfik.Tests
{
    public class ErrorTests
    {
        [Fact]
        public async Task Rfc7807_DetailAndErrorCode()
        {
            var handler = new MockHttpHandler().Json(402,
                "{\"type\":\"https://docs.pdfik.net/error-codes#quota-exceeded\",\"title\":\"Payment Required\"," +
                "\"detail\":\"Monthly PDF quota exhausted.\",\"error\":\"QUOTA_EXCEEDED\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal(402, e.StatusCode);
            Assert.Equal("Monthly PDF quota exhausted.", e.Message);
            Assert.Equal("QUOTA_EXCEEDED", e.ErrorCode);
            Assert.Contains("quota-exceeded", e.ResponseBody);
        }

        [Fact]
        public async Task LegacyErrorCodeField_IsFallback()
        {
            var handler = new MockHttpHandler().Json(400, "{\"detail\":\"nope\",\"error_code\":\"OLD_STYLE\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal("OLD_STYLE", e.ErrorCode);
        }

        [Fact]
        public async Task ValidationArray_FlattenedToReadableMessage()
        {
            var handler = new MockHttpHandler().Json(422,
                "{\"detail\":[" +
                "{\"loc\":[\"body\",\"options\",\"viewport\",\"width\"],\"msg\":\"Input should be less than or equal to 1920\"}," +
                "{\"loc\":[\"body\",\"options\",\"quality\"],\"msg\":\"Input should be greater than 0\"}]}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToImageAsync("https://example.com"));

            Assert.Equal(
                "options.viewport.width: Input should be less than or equal to 1920; options.quality: Input should be greater than 0",
                e.Message);
        }

        [Fact]
        public async Task NonJsonBody_GenericMessageKeepsBody()
        {
            var handler = new MockHttpHandler().Json(403, "<html>edge block</html>");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.UrlToPdfAsync("https://example.com"));

            Assert.Equal("API request failed with status 403", e.Message);
            Assert.Equal("<html>edge block</html>", e.ResponseBody);
        }
    }

    public class JobPollingTests
    {
        [Fact]
        public async Task GetJob_ParsesAllFields()
        {
            var handler = new MockHttpHandler().Json(200,
                "{\"status\":\"done\",\"created_at\":\"2026-06-14T19:11:03Z\"," +
                "\"finished_at\":\"2026-06-14T19:11:06.123456\"," +           // naive => UTC
                "\"expires_at\":\"2026-06-15T19:11:06+00:00\",\"test\":false,\"pages_count\":3," +
                "\"metrics\":{\"file_size_bytes\":1048576,\"file_size_human\":\"1.00 MB\"," +
                "\"page_load_ms\":1200,\"processing_ms\":650,\"total_duration_ms\":3200,\"page_count\":3}}");
            using var client = TestKit.Client(handler);

            var job = await client.GetJobAsync("job-1");

            Assert.Equal("/jobs/job-1", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal(JobStatus.Done, job.Status);
            Assert.Equal(new DateTimeOffset(2026, 6, 14, 19, 11, 3, TimeSpan.Zero), job.CreatedAt);
            Assert.Equal(new DateTimeOffset(2026, 6, 14, 19, 11, 6, TimeSpan.Zero).AddTicks(1234560), job.FinishedAt);
            Assert.Equal(new DateTimeOffset(2026, 6, 15, 19, 11, 6, TimeSpan.Zero), job.ExpiresAt);
            Assert.False(job.Test);
            Assert.Equal(3, job.PagesCount);
            Assert.Null(job.ErrorCode);
            Assert.Equal(1048576, job.Metrics.FileSizeBytes);
            Assert.Equal("1.00 MB", job.Metrics.FileSizeHuman);
            Assert.Equal(3200, job.Metrics.TotalDurationMs);
        }

        [Fact]
        public async Task WaitForJob_PollsUntilDone()
        {
            var handler = new MockHttpHandler()
                .Json(200, TestKit.Status("queued"))
                .Json(200, TestKit.Status("rendering"))
                .Json(200, TestKit.Status("done"));
            using var client = TestKit.Client(handler, out var delays);

            var job = await client.WaitForJobAsync("job-1");

            Assert.Equal(JobStatus.Done, job.Status);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }, delays);
        }

        [Fact]
        public async Task WaitForJob_CustomPollInterval()
        {
            var handler = new MockHttpHandler()
                .Json(200, TestKit.Status("queued"))
                .Json(200, TestKit.Status("done"));
            using var client = TestKit.Client(handler, out var delays);

            await client.WaitForJobAsync("job-1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500));

            Assert.Equal(new[] { TimeSpan.FromMilliseconds(500) }, delays);
        }

        [Fact]
        public async Task WaitForJob_Failed_ThrowsWithJobErrorCode()
        {
            var handler = new MockHttpHandler().Json(200, TestKit.Status("failed", "\"error_code\":\"SSRF_BLOCKED\""));
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.WaitForJobAsync("job-1"));

            Assert.Equal(400, e.StatusCode);
            Assert.Equal("SSRF_BLOCKED", e.ErrorCode);
        }

        [Fact]
        public async Task WaitForJob_Failed_DefaultsToJobFailedCode()
        {
            var handler = new MockHttpHandler().Json(200, TestKit.Status("failed"));
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.WaitForJobAsync("job-1"));

            Assert.Equal("JOB_FAILED", e.ErrorCode);
        }

        [Fact]
        public async Task WaitForJob_Timeout_Throws408()
        {
            var handler = new MockHttpHandler().Json(200, TestKit.Status("queued"));
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(
                () => client.WaitForJobAsync("job-1", TimeSpan.Zero));

            Assert.Equal(408, e.StatusCode);
            Assert.Equal("TIMEOUT", e.ErrorCode);
        }

        [Fact]
        public async Task WaitForJob_429WhilePolling_IsWaitedOut()
        {
            var handler = new MockHttpHandler()
                .Json(429, "{\"type\":\"https://docs.pdfik.net/error-codes#rate-limit-exceeded\",\"retry_after_seconds\":1}")
                .Json(200, TestKit.Status("done"));
            using var client = TestKit.Client(handler, out var delays);

            var job = await client.WaitForJobAsync("job-1");

            Assert.Equal(JobStatus.Done, job.Status);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, delays);
        }

        [Fact]
        public async Task WaitForJob_429_StillBoundByTimeout()
        {
            var handler = new MockHttpHandler()
                .Json(429, "{\"type\":\"https://docs.pdfik.net/error-codes#rate-limit-exceeded\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(
                () => client.WaitForJobAsync("job-1", TimeSpan.Zero));

            Assert.Equal(408, e.StatusCode);
        }

        [Fact]
        public async Task Download_ReturnsBytes()
        {
            var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
            var handler = new MockHttpHandler().Bytes(200, pdf, "application/pdf");
            using var client = TestKit.Client(handler);

            var bytes = await client.DownloadAsync("job-1");

            Assert.Equal("/jobs/job-1/download", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal(pdf, bytes);
        }

        [Fact]
        public async Task Download_404_ThrowsWithDetail()
        {
            var handler = new MockHttpHandler().Json(404,
                "{\"type\":\"https://docs.pdfik.net/error-codes#output-delivered-externally\"," +
                "\"detail\":\"The output was delivered to external storage.\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.DownloadAsync("job-1"));

            Assert.Equal(404, e.StatusCode);
            Assert.Contains("external storage", e.Message);
        }

        [Fact]
        public void GetFileUrl_ConstructsDownloadUrl()
        {
            using var client = TestKit.Client(new MockHttpHandler());

            var file = client.GetFileUrl("job-1");

            Assert.Equal("job-1", file.JobId);
            Assert.Equal(TestKit.BaseUrl + "/jobs/job-1/download", file.DownloadUrl);
            Assert.Equal(-1, file.ExpiresInSeconds);
        }
    }
}
