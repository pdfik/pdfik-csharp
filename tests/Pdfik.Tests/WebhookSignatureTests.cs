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
        private const string PreviousSecret = "whsec_previous_secret";
        private const string Body = "{\"event_type\":\"job.finished\",\"job_id\":\"j-1\",\"status\":\"done\"}";

        private static string Sign(string body, string secret, long timestamp)
        {
            return Sign(body, secret, timestamp.ToString());
        }

        private static string Sign(string body, string secret, string timestamp)
        {
            using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = mac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + body));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        // What a delivery carries while the previous secret is inside its overlap
        // window: one v1 per secret, the current secret's first.
        private static Dictionary<string, string> RotationHeaders(long timestamp)
        {
            return new Dictionary<string, string>
            {
                { "X-PDFik-Signature", "t=" + timestamp + ",v1=" + Sign(Body, Secret, timestamp) + ",v1=" + Sign(Body, PreviousSecret, timestamp) },
                { "X-PDFik-Timestamp", timestamp.ToString() },
            };
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
        public void TwoSignatures_ReceiverOnTheCurrentSecret_MatchesTheFirst()
        {
            // The helper used to keep only the LAST v1 (the previous secret's), so a
            // receiver already switched to the new secret rejected every delivery.
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Assert.True(PdfikClient.VerifyWebhookSignature(Body, RotationHeaders(now), Secret));
        }

        [Fact]
        public void TwoSignatures_ReceiverOnThePreviousSecret_MatchesTheSecond()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Assert.True(PdfikClient.VerifyWebhookSignature(Body, RotationHeaders(now), PreviousSecret));
        }

        [Fact]
        public void TwoSignatures_MatchingNeitherSecret_ReturnFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, RotationHeaders(now), "whsec_unrelated"));
        }

        [Fact]
        public void TwoSignatures_OutsideTheWindow_ReturnFalse()
        {
            long stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400;

            Assert.False(PdfikClient.VerifyWebhookSignature(Body, RotationHeaders(stale), Secret));
            Assert.False(PdfikClient.VerifyWebhookSignature(Body, RotationHeaders(stale), PreviousSecret));
        }

        [Fact]
        public void MalformedSignatureHeaders_WithV1Values_ReturnFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var malformed = new[]
            {
                "v1=" + Sign(Body, Secret, now) + ",v1=" + Sign(Body, PreviousSecret, now), // no t
                "t=" + now, // no v1
                "t=" + now + ",v1=,v1=", // empty v1 values
                "garbage",
                "t=soon,v1=" + Sign(Body, Secret, "soon"), // signed, but t is not a number
            };

            foreach (var signature in malformed)
            {
                var headers = new Dictionary<string, string>
                {
                    { "X-PDFik-Signature", signature },
                    { "X-PDFik-Timestamp", now.ToString() },
                };
                Assert.False(PdfikClient.VerifyWebhookSignature(Body, headers, Secret), signature);
            }
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
