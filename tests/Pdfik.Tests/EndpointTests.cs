using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Pdfik.Models;
using Xunit;

namespace Pdfik.Tests
{
    public class EndpointTests
    {
        [Fact]
        public async Task UrlToPdf_PostsPathAndParsesResponse()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var job = await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("/url-to-pdf", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal("550e8400-e29b-41d4-a716-446655440000", job.JobId);
            Assert.Equal("queued", job.Status);
            Assert.Equal("Job successfully sent to processing queue.", job.Detail);
        }

        [Fact]
        public void HtmlToPdf_PostsPath()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var job = client.HtmlToPdf("<h1>Hi</h1>");

            Assert.Equal("/html-to-pdf", handler.Requests[0].Uri.AbsolutePath);
            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("<h1>Hi</h1>", (string)body["html"]);
            Assert.Equal("queued", job.Status);
        }

        [Fact]
        public async Task MarkdownToPdf_PostsPath()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.MarkdownToPdfAsync("# Title");

            Assert.Equal("/markdown-to-pdf", handler.Requests[0].Uri.AbsolutePath);
            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("# Title", (string)body["markdown"]);
        }

        [Fact]
        public async Task UrlToImage_PostsPath()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToImageAsync("https://example.com");

            Assert.Equal("/url-to-image", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task HtmlToImage_PostsPath()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.HtmlToImageAsync("<p>x</p>");

            Assert.Equal("/html-to-image", handler.Requests[0].Uri.AbsolutePath);
        }

        [Fact]
        public async Task EinvoiceToPdf_PostsPath()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.EinvoiceToPdfAsync("<rsm:CrossIndustryInvoice/>", profile: "en16931");

            Assert.Equal("/einvoice-to-pdf", handler.Requests[0].Uri.AbsolutePath);
            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("<rsm:CrossIndustryInvoice/>", (string)body["xml"]);
            Assert.Equal("en16931", (string)body["profile"]);
        }

        [Fact]
        public async Task Request_CarriesApiKeyContentTypeAndGeneratedIdempotencyKey()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com");

            var request = handler.Requests[0];
            Assert.Equal(TestKit.ApiKey, request.Header("X-API-Key"));
            Assert.StartsWith("application/json", request.Header("Content-Type"));
            Assert.True(Guid.TryParse(request.Header("Idempotency-Key"), out _));
        }

        [Fact]
        public async Task CustomIdempotencyKey_IsSent()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com", idempotencyKey: "my-key-42");

            Assert.Equal("my-key-42", handler.Requests[0].Header("Idempotency-Key"));
        }

        [Fact]
        public async Task MinimalBody_OmitsEveryNullField()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("{\"url\":\"https://example.com\"}", handler.Requests[0].Body);
        }

        [Fact]
        public async Task FullBody_SerializesSnakeCase()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var request = new UrlToPdfRequest("https://example.com")
            {
                WebhookUrl = "https://client.example/hook",
                Options = new PdfOptions
                {
                    Format = PaperFormat.Letter,
                    Landscape = true,
                    Margin = new MarginOptions { Top = "10mm", Left = "1cm" },
                    PrintBackground = false,
                    DisplayHeaderFooter = true,
                    HeaderTemplate = "<span class='title'></span>",
                    FooterTemplate = "<span class='pageNumber'></span>",
                    Watermark = new WatermarkOptions { Text = "DRAFT", Color = "rgba(255,0,0,0.2)", FontSize = "48px", RotationDegrees = -30 },
                    UserPassword = "secret123",
                    Compression = new CompressionOptions { Level = 6, ImageQuality = 85 },
                    Scale = 0.8,
                },
                Render = new RenderOptions
                {
                    PageLoadTimeoutMs = 30000,
                    WaitUntil = WaitUntilEvent.NetworkIdle,
                    WaitForSelector = "#ready",
                    WaitAfterLoadMs = 250,
                },
                Auth = new JobAuthOptions("basic", "user:password"),
                Test = true,
            };
            await client.UrlToPdfAsync(request);

            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("https://client.example/hook", (string)body["webhook_url"]);
            Assert.Equal("Letter", (string)body["options"]["format"]);
            Assert.True((bool)body["options"]["landscape"]);
            Assert.Equal("10mm", (string)body["options"]["margin"]["top"]);
            Assert.False((bool)body["options"]["print_background"]);
            Assert.True((bool)body["options"]["display_header_footer"]);
            Assert.NotNull(body["options"]["header_template"]);
            Assert.NotNull(body["options"]["footer_template"]);
            Assert.Equal("DRAFT", (string)body["options"]["watermark"]["text"]);
            Assert.Equal("48px", (string)body["options"]["watermark"]["font_size"]);
            Assert.Equal(-30, (int)body["options"]["watermark"]["rotation_degrees"]);
            Assert.Equal("secret123", (string)body["options"]["user_password"]);
            Assert.Equal(6, (int)body["options"]["compression"]["level"]);
            Assert.Equal(85, (int)body["options"]["compression"]["image_quality"]);
            Assert.Equal(0.8, (double)body["options"]["scale"], 3);
            Assert.Equal(30000, (int)body["render"]["page_load_timeout_ms"]);
            Assert.Equal("networkidle", (string)body["render"]["wait_until"]);
            Assert.Equal("#ready", (string)body["render"]["wait_for_selector"]);
            Assert.Equal(250, (int)body["render"]["wait_after_load_ms"]);
            Assert.Equal("basic", (string)body["auth"]["type"]);
            Assert.Equal("user:password", (string)body["auth"]["value"]);
            Assert.True((bool)body["test"]);
            // No "wait" on the plain job-creating call.
            Assert.Null(body["wait"]);
        }

        [Fact]
        public async Task EinvoiceRequest_FullFields()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var request = new EInvoiceToPdfRequest("<xml/>", "basic")
            {
                TemplateId = "tpl_123",
                Template = new Dictionary<string, object> { { "accent", "#336699" } },
                WebhookUrl = "https://client.example/hook",
                Test = true,
            };
            await client.EinvoiceToPdfAsync(request);

            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("basic", (string)body["profile"]);
            Assert.Equal("tpl_123", (string)body["template_id"]);
            Assert.Equal("#336699", (string)body["template"]["accent"]);
            Assert.Equal("https://client.example/hook", (string)body["webhook_url"]);
            Assert.True((bool)body["test"]);
        }

        [Fact]
        public async Task ImageOptions_Serialization()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToImageAsync("https://example.com", new ImageOptions
            {
                Format = ImageFormat.Jpeg,
                FullPage = true,
                Quality = 80,
                Viewport = new ImageViewport(1280, 1024),
            });

            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("jpeg", (string)body["options"]["format"]);
            Assert.True((bool)body["options"]["full_page"]);
            Assert.Equal(80, (int)body["options"]["quality"]);
            Assert.Equal(1280, (int)body["options"]["viewport"]["width"]);
            Assert.Equal(1024, (int)body["options"]["viewport"]["height"]);
        }

        [Fact]
        public async Task Delivery_SerializesModeAndUrl()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            var request = new HtmlToPdfRequest("<p>x</p>")
            {
                Delivery = new DeliveryOptions("https://bucket.example/out.pdf?X-Amz-Signature=abc"),
            };
            await client.HtmlToPdfAsync(request);

            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal("presigned_put", (string)body["delivery"]["mode"]);
            Assert.Equal("https://bucket.example/out.pdf?X-Amz-Signature=abc", (string)body["delivery"]["url"]);
        }

        [Theory]
        [InlineData(PaperFormat.A4, "A4")]
        [InlineData(PaperFormat.Letter, "Letter")]
        [InlineData(PaperFormat.Tabloid, "Tabloid")]
        public async Task PaperFormat_SerializesExactly(PaperFormat format, string expected)
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = TestKit.Client(handler);

            await client.UrlToPdfAsync("https://example.com", new PdfOptions { Format = format });

            var body = JsonNode.Parse(handler.Requests[0].Body);
            Assert.Equal(expected, (string)body["options"]["format"]);
        }

        [Fact]
        public void Constructor_RequiresApiKey()
        {
            Assert.Throws<ArgumentException>(() => new PdfikClient(new PdfikClientOptions { ApiKey = " " }));
        }

        [Fact]
        public async Task BaseUrl_TrailingSlashTrimmed()
        {
            var handler = new MockHttpHandler().Json(202, TestKit.CreatedJson);
            using var client = new PdfikClient(new PdfikClientOptions
            {
                ApiKey = TestKit.ApiKey,
                BaseUrl = "https://api.pdfik.test///",
                HttpMessageHandler = handler,
            });

            await client.UrlToPdfAsync("https://example.com");

            Assert.Equal("https://api.pdfik.test/url-to-pdf", handler.Requests[0].Uri.ToString());
        }
    }
}
