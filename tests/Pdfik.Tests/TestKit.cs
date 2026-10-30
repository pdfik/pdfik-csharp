using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pdfik.Tests
{
    internal sealed class RecordedRequest
    {
        public HttpMethod Method;
        public Uri Uri;
        public string Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            return Headers.TryGetValue(name, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Scripted HttpMessageHandler: each entry answers one request in order; the
    /// last entry repeats (polling loops). Entries are factories because the client
    /// disposes every HttpResponseMessage it receives.
    /// </summary>
    internal sealed class MockHttpHandler : HttpMessageHandler
    {
        public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();
        private readonly List<Func<HttpResponseMessage>> _script = new List<Func<HttpResponseMessage>>();
        private readonly List<string> _throwMessages = new List<string>(); // parallel: non-null = throw instead

        /// <summary>Simulated server-side latency, honoring the client's per-request cancellation.</summary>
        public TimeSpan ResponseDelay = TimeSpan.Zero;

        public MockHttpHandler Json(int status, string json)
        {
            _script.Add(() => Build(status, Encoding.UTF8.GetBytes(json ?? ""), "application/json"));
            _throwMessages.Add(null);
            return this;
        }

        public MockHttpHandler JsonWithRetryAfterHeader(int status, string json, string retryAfter)
        {
            _script.Add(() =>
            {
                var response = Build(status, Encoding.UTF8.GetBytes(json ?? ""), "application/json");
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
                return response;
            });
            _throwMessages.Add(null);
            return this;
        }

        public MockHttpHandler Bytes(int status, byte[] body, string contentType)
        {
            _script.Add(() => Build(status, body, contentType));
            _throwMessages.Add(null);
            return this;
        }

        public MockHttpHandler Throw(string message)
        {
            _script.Add(null);
            _throwMessages.Add(message ?? "boom");
            return this;
        }

        private static HttpResponseMessage Build(int status, byte[] body, string contentType)
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new ByteArrayContent(body ?? new byte[0]),
            };
            if (!string.IsNullOrEmpty(contentType))
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri,
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(),
            };
            foreach (var header in request.Headers)
                recorded.Headers[header.Key] = string.Join(",", header.Value);
            if (request.Content != null)
                foreach (var header in request.Content.Headers)
                    recorded.Headers[header.Key] = string.Join(",", header.Value);
            Requests.Add(recorded);

            if (ResponseDelay > TimeSpan.Zero)
                await Task.Delay(ResponseDelay, cancellationToken);

            if (_script.Count == 0)
                throw new InvalidOperationException("MockHttpHandler has no scripted responses");
            int index = Math.Min(Requests.Count - 1, _script.Count - 1);
            if (_throwMessages[index] != null)
                throw new HttpRequestException(_throwMessages[index]);
            return _script[index]();
        }
    }

    internal static class TestKit
    {
        public const string ApiKey = "sk_test_unit";
        public const string BaseUrl = "https://api.pdfik.test";

        /// <summary>Client over the mock handler with injected, non-sleeping delays recorded into <paramref name="delays"/>.</summary>
        public static PdfikClient Client(MockHttpHandler handler, out List<TimeSpan> delays)
        {
            var client = new PdfikClient(new PdfikClientOptions
            {
                ApiKey = ApiKey,
                BaseUrl = BaseUrl,
                HttpMessageHandler = handler,
            });
            var recorded = new List<TimeSpan>();
            client.DelayFunc = (span, ct) =>
            {
                recorded.Add(span);
                return Task.CompletedTask;
            };
            delays = recorded;
            return client;
        }

        public static PdfikClient Client(MockHttpHandler handler)
        {
            List<TimeSpan> _;
            return Client(handler, out _);
        }

        public const string CreatedJson =
            "{\"job_id\":\"550e8400-e29b-41d4-a716-446655440000\",\"status\":\"queued\",\"detail\":\"Job successfully sent to processing queue.\"}";

        public static string Status(string status, string extra = "")
        {
            return "{\"status\":\"" + status + "\"" + (extra.Length > 0 ? "," + extra : "") + "}";
        }
    }
}
