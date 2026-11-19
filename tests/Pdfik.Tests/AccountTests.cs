using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Pdfik.Models;
using Xunit;

namespace Pdfik.Tests
{
    /// <summary>Integrations release (0.3.0): GET /account and X-PDFik-Client.</summary>
    public class AccountTests
    {
        private static string ExpectedHeader()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Pdfik", "Pdfik.csproj")))
            {
                dir = dir.Parent;
            }
            var csproj = File.ReadAllText(Path.Combine(dir.FullName, "src", "Pdfik", "Pdfik.csproj"));
            return "sdk-csharp/" + Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        }

        private const string AccountJson = "{\"plan\":\"pro\","
            + "\"window\":{\"start\":\"2026-11-05T00:00:00Z\",\"end\":\"2026-12-05T00:00:00Z\"},"
            + "\"usage\":{\"documents\":1234,\"bytes\":52428800},"
            + "\"limits\":{\"documents\":50000,\"bytes\":53687091200,\"requests_per_minute\":500,"
            + "\"concurrent_jobs\":50,\"max_file_size_bytes\":157286400},"
            + "\"test_mode\":{\"per_minute\":60,\"per_day\":2000},"
            + "\"key\":{\"name\":null}}";

        private static void AssertAccount(AccountResponse account)
        {
            Assert.Equal("pro", account.Plan);
            Assert.Equal(new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), account.Window.End);
            Assert.Equal(1234L, account.Usage.Documents);
            Assert.Equal(52428800L, account.Usage.Bytes);
            Assert.Equal(53687091200L, account.Limits.Bytes);
            Assert.Equal(500, account.Limits.RequestsPerMinute);
            Assert.Equal(157286400L, account.Limits.MaxFileSizeBytes);
            Assert.Equal(2000, account.TestMode.PerDay);
            Assert.Null(account.Key.Name);
        }

        [Fact]
        public void ClientHeader_NamesTheSdkAndTheCsprojVersion()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Pdfik", "Pdfik.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            var csproj = File.ReadAllText(Path.Combine(dir.FullName, "src", "Pdfik", "Pdfik.csproj"));
            var declared = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(declared));

            var handler = new MockHttpHandler().Json(200, AccountJson);
            using var client = TestKit.Client(handler);
            client.GetAccount();
            Assert.Equal("sdk-csharp/" + declared, handler.Requests[0].Header("X-PDFik-Client"));
        }

        [Fact]
        public async Task GetAccountAsync_ReadsAccountAndSendsTheClientHeader()
        {
            var handler = new MockHttpHandler().Json(200, AccountJson);
            using var client = TestKit.Client(handler);

            AssertAccount(await client.GetAccountAsync());

            Assert.Single(handler.Requests);
            var request = handler.Requests[0];
            Assert.Equal("GET", request.Method.Method);
            Assert.Equal("/account", request.Uri.AbsolutePath);
            Assert.Equal(ExpectedHeader(), request.Header("X-PDFik-Client"));
            Assert.Null(request.Header("Idempotency-Key"));
        }

        [Fact]
        public void GetAccount_Blocking_ReadsTheSameAccount()
        {
            var handler = new MockHttpHandler().Json(200, AccountJson);
            using var client = TestKit.Client(handler);

            AssertAccount(client.GetAccount());
        }

        [Fact]
        public async Task GetAccountAsync_RejectedKey_ThrowsPdfikException()
        {
            var handler = new MockHttpHandler().Json(401,
                "{\"type\":\"https://docs.pdfik.net/error-codes#401\",\"title\":\"Invalid API key\",\"status\":401,\"detail\":\"Invalid API key\"}");
            using var client = TestKit.Client(handler);

            var e = await Assert.ThrowsAsync<PdfikException>(() => client.GetAccountAsync());
            Assert.Equal(401, e.StatusCode);
        }

        [Fact]
        public async Task JobCreation_NamesTheSdkToo()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal(ExpectedHeader(), handler.Requests[0].Header("X-PDFik-Client"));
        }
    }
}
