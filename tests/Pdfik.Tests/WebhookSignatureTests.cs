using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Pdfik.Tests
{
    public class WebhookSignatureTests
    {
        private const string Secret = "whsec_test_secret";
        private const string Body = "{\"event_type\":\"job.finished\",\"job_id\":\"j-1\",\"status\":\"done\"}";

        private static string Sign(string body, string secret, long timestamp)
        {
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = mac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + body));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        private static Dictionary<string, string> Headers(long timestamp, string signature)
        {
            return new Dictionary<string, string>
            {
                { "X-PDFik-Signature", "t=" + timestamp + ",v1=" + signature },
                { "X-PDFik-Timestamp", timestamp.ToString() },
            };
        }

        [Fact]
        public void ValidSignature_ReturnsTrue()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = Headers(now, Sign(Body, Secret, now));

            Assert.True(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void HeaderNames_AreCaseInsensitive()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                { "x-pdfik-signature", "t=" + now + ",v1=" + Sign(Body, Secret, now) },
                { "X-PDFIK-TIMESTAMP", now.ToString() },
            };

            Assert.True(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void WrongSecret_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = Headers(now, Sign(Body, "whsec_other", now));

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void TamperedBody_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = Headers(now, Sign(Body, Secret, now));

            Assert.False(PdfikClient.VerifyWebhookSignature(Body.Replace("done", "failed"), headers, Secret));
        }

        [Theory]
        [InlineData(-400)]
        [InlineData(400)]
        public void TimestampOutsideWindow_ReturnsFalse(int offsetSeconds)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds;
            var headers = Headers(ts, Sign(Body, Secret, ts));

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void MissingSignatureHeader_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string> { { "X-PDFik-Timestamp", now.ToString() } };

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void MissingTimestampHeader_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                { "X-PDFik-Signature", "t=" + now + ",v1=" + Sign(Body, Secret, now) },
            };

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void TimestampHeaderValue_Ignored_TheSignatureTimestampWins()
        {
            // The header must be present, but the HMAC and the ±300 s window use the
            // t= from the signature header — pinned so the SDKs never diverge on this.
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                { "X-PDFik-Signature", "t=" + now + ",v1=" + Sign(Body, Secret, now) },
                { "X-PDFik-Timestamp", "999" },
            };

            Assert.True(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void MalformedSignatureHeader_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                { "X-PDFik-Signature", "v2=deadbeef" },
                { "X-PDFik-Timestamp", now.ToString() },
            };

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret));
        }

        [Fact]
        public void NullArguments_ReturnFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var headers = Headers(now, Sign(Body, Secret, now));

            Assert.False(PdfikClient.VerifyWebhookSignature(null, headers, Secret));
            Assert.False(PdfikClient.VerifyWebhookSignature(Body, null, Secret));
            Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, null));
        }
    }
}
