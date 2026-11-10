using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pdfik.Models;

namespace Pdfik
{
    /// <summary>
    /// Client for the PDFik API (https://docs.pdfik.net). Task-based methods are
    /// the primary API; every one has a blocking twin. One instance is thread-safe
    /// and meant to be reused.
    /// </summary>
    public class PdfikClient : IDisposable
    {
        // A throttle (rate or in-flight limit) is waited out, not counted as a failed
        // attempt — but not forever: after this many waits the 429 is raised.
        internal const int ThrottleWaits = 5;
        internal const long RetryAfterFallbackMs = 10_000;
        // The server states the wait; the cap keeps it from parking the caller.
        internal const long RetryAfterCapMs = 60_000;

        // The 429s that clear by themselves within a minute: the per-account request
        // rate and the in-flight job cap. A quota 429, the test-mode caps and an
        // exhausted download counter do not, so they are raised at once.
        private static readonly string[] Throttles = { "rate-limit-exceeded", "concurrency-limit-exceeded" };
        // The download has one more: another stream of the same file is still open.
        // It clears within seconds and costs no download attempt.
        private static readonly string[] DownloadThrottles = { "rate-limit-exceeded", "concurrency-limit-exceeded", "download-busy" };

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly TimeSpan _requestTimeout;
        private readonly HttpClient _http;

        // Injectable time, so the retry and polling schedules are testable without sleeping.
        internal Func<TimeSpan, CancellationToken, Task> DelayFunc = Task.Delay;

        // Floor for the per-request timeout of the *File ("wait": true) methods: the
        // server answers by 45 s and the edge cuts at 60 s, so 70 s guarantees the
        // server's verdict always beats the client's own timeout — the configured
        // RequestTimeout (default 30 s) would cut a healthy wait. Internal so tests
        // can shrink it.
        internal TimeSpan SyncRequestTimeout = TimeSpan.FromSeconds(70);

        public PdfikClient(string apiKey) : this(new PdfikClientOptions { ApiKey = apiKey }) { }

        public PdfikClient(PdfikClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.ApiKey))
                throw new ArgumentException("API key is required", nameof(options));
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
                throw new ArgumentException("Base URL is required (PdfikClientOptions carries the production default)", nameof(options));

            _apiKey = options.ApiKey;
            _baseUrl = options.BaseUrl.TrimEnd('/');
            _requestTimeout = options.RequestTimeout;

            _http = options.HttpMessageHandler == null
                ? new HttpClient()
                : new HttpClient(options.HttpMessageHandler, disposeHandler: false);
            // Timeouts are per request (the sync *File calls need a longer one), so
            // the client-wide timer is off and a linked CancellationToken does the job.
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }

        // ------------------------------------------------------------------
        // HTTP core
        // ------------------------------------------------------------------

        private sealed class ApiResponse
        {
            public int Status;
            public byte[] Body;
            public string ContentType;
            public string RetryAfterHeader;
            private string _text;

            public string Text
            {
                get
                {
                    if (_text == null)
                        _text = Body == null || Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body);
                    return _text;
                }
            }
        }

        private async Task<ApiResponse> SendOnceAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try
                {
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        var body = response.Content == null
                            ? new byte[0]
                            : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        string contentType = response.Content != null && response.Content.Headers.ContentType != null
                            ? response.Content.Headers.ContentType.ToString()
                            : "";
                        string retryAfter = null;
                        IEnumerable<string> retryValues;
                        if (response.Headers.TryGetValues("Retry-After", out retryValues))
                        {
                            foreach (var v in retryValues) { retryAfter = v; break; }
                        }
                        return new ApiResponse
                        {
                            Status = (int)response.StatusCode,
                            Body = body,
                            ContentType = contentType,
                            RetryAfterHeader = retryAfter,
                        };
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Our own per-request timeout, surfaced like any other transport failure.
                    throw new HttpRequestException("The request timed out after " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                }
            }
        }

        /// <summary>
        /// The one retry engine. <paramref name="resend"/> true: up to 3 attempts on
        /// 5xx and transport failures (backoff 1 s, 2 s). false — the synchronous
        /// ("wait": true) path: the one attempt may already have rendered and charged
        /// the job, so 5xx is returned to the caller as is and a transport failure is
        /// raised at once; in particular the 503 #sync-timeout must never be
        /// auto-resent. In both modes a throttled 429 is waited out (up to
        /// <see cref="ThrottleWaits"/> times) without consuming an attempt — a
        /// throttle fires before the job is admitted, so a resend under the call's
        /// one Idempotency-Key is free.
        /// </summary>
        private async Task<ApiResponse> ExecuteAsync(
            Func<HttpRequestMessage> requestFactory,
            TimeSpan timeout,
            bool resend,
            int throttleWaits,
            string[] throttles,
            CancellationToken ct)
        {
            int attempt = 0;
            int throttled = 0;
            while (true)
            {
                attempt++;
                ApiResponse response;
                using (var request = requestFactory())
                {
                    try
                    {
                        response = await SendOnceAsync(request, timeout, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException e)
                    {
                        // No HTTP answer, a body that broke off while SendAsync buffered
                        // it, or our own per-request timeout: 503 NETWORK_ERROR, as in
                        // the other PDFik SDKs, with the transport failure as InnerException.
                        if (resend && attempt < 3)
                        {
                            await DelayFunc(BackoffDelay(attempt), ct).ConfigureAwait(false);
                            continue;
                        }
                        throw new PdfikException("Connection failed: " + e.Message, 503, "NETWORK_ERROR", null, e);
                    }
                }
                if (throttled < throttleWaits && IsThrottled(response, throttles))
                {
                    throttled++;
                    attempt--; // not a failed attempt
                    await DelayFunc(ThrottleDelay(response), ct).ConfigureAwait(false);
                    continue;
                }
                if (resend && response.Status >= 500 && attempt < 3)
                {
                    await DelayFunc(BackoffDelay(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
        }

        private static TimeSpan BackoffDelay(int attempt)
        {
            return TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 1000);
        }

        /// <summary>A 429 whose documented case is one of <paramref name="throttles"/>; every other 429 is raised at once.</summary>
        private static bool IsThrottled(ApiResponse response, string[] throttles)
        {
            if (response.Status != 429) return false;
            JsonObject node = TryParseObject(response.Text);
            if (node == null) return false;
            foreach (var slug in throttles)
            {
                if (IsCase(node, slug)) return true;
            }
            return false;
        }

        /// <summary>The documented case <paramref name="slug"/>: the fragment of the body's <c>type</c> URL, or its <c>error</c> code.</summary>
        private static bool IsCase(JsonObject node, string slug)
        {
            var type = AsString(node["type"]);
            if (type != null && type.EndsWith("#" + slug, StringComparison.Ordinal)) return true;
            var error = AsString(node["error"]);
            return error != null && string.Equals(error, slug.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);
        }

        // Error bodies can come from proxies and edges too, so nothing here may assume
        // a shape: a non-object body reads as "no fields", a non-string field as null.
        private static JsonObject TryParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonNode.Parse(text) as JsonObject; }
            catch { return null; }
        }

        private static string AsString(JsonNode node)
        {
            var value = node as JsonValue;
            string text;
            return value != null && value.TryGetValue(out text) ? text : null;
        }

        /// <summary>
        /// Wait before resending a throttled 429: the body's <c>retry_after_seconds</c>,
        /// else the <c>Retry-After</c> header in seconds, else 10 s; capped at 60 s.
        /// </summary>
        private static TimeSpan ThrottleDelay(ApiResponse response)
        {
            long seconds = RetryAfterSeconds(response.Text);
            if (seconds <= 0)
            {
                long parsed;
                if (long.TryParse((response.RetryAfterHeader ?? "").Trim(), out parsed)) seconds = parsed;
            }
            if (seconds <= 0) return TimeSpan.FromMilliseconds(RetryAfterFallbackMs);
            return TimeSpan.FromSeconds(Math.Min(seconds, RetryAfterCapMs / 1000));
        }

        /// <summary>
        /// Wait after a 429 while polling: the API's <c>retry_after_seconds</c> (capped
        /// at 60 s so the server cannot park the caller), else <paramref name="fallback"/>.
        /// </summary>
        internal static TimeSpan ThrottleDelay(PdfikException e, TimeSpan fallback)
        {
            long seconds = RetryAfterSeconds(e.ResponseBody);
            if (seconds <= 0) return fallback;
            return TimeSpan.FromSeconds(Math.Min(seconds, RetryAfterCapMs / 1000));
        }

        private static long RetryAfterSeconds(string body)
        {
            JsonObject node = TryParseObject(body);
            if (node == null) return 0;
            var value = node["retry_after_seconds"];
            if (value == null) return 0;
            try { return (long)value.AsValue().GetValue<double>(); }
            catch { return 0; }
        }

        /// <summary>
        /// Readable text for an error body's <c>detail</c>. Request-validation errors
        /// (422) carry an ARRAY of <c>{loc, msg}</c> items; they read as
        /// "options.viewport.width: Input should be less than or equal to 1920".
        /// </summary>
        internal static string ErrorDetail(JsonNode detail)
        {
            if (detail == null) return null;
            if (detail is JsonArray array)
            {
                var sb = new StringBuilder();
                foreach (var item in array)
                {
                    if (item == null) continue;
                    var itemObject = item as JsonObject;
                    var loc = new StringBuilder();
                    if (itemObject != null && itemObject["loc"] is JsonArray locArray)
                    {
                        foreach (var part in locArray)
                        {
                            var partText = part == null ? "" : part.ToString().Trim('"');
                            if (partText == "body") continue;
                            if (loc.Length > 0) loc.Append('.');
                            loc.Append(partText);
                        }
                    }
                    string msg = itemObject != null
                        ? AsString(itemObject["msg"]) ?? itemObject.ToJsonString()
                        : AsString(item) ?? item.ToJsonString();
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(loc.Length > 0 ? loc + ": " + msg : msg);
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            string text = AsString(detail) ?? detail.ToJsonString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static void CheckResponse(ApiResponse response)
        {
            if (response.Status >= 200 && response.Status < 300) return;
            string body = response.Text;
            string message = "API request failed with status " + response.Status;
            string errorCode = null;
            JsonObject node = TryParseObject(body);
            if (node != null)
            {
                var detail = ErrorDetail(node["detail"]);
                if (detail != null) message = detail;
                // pdf-api's RFC 7807 bodies carry the code in "error";
                // "error_code" is a fallback for any body that still uses it.
                errorCode = AsString(node["error"]) ?? AsString(node["error_code"]);
            }
            throw new PdfikException(message, response.Status, errorCode, body);
        }

        // ------------------------------------------------------------------
        // Request factories
        // ------------------------------------------------------------------

        private Func<HttpRequestMessage> PostFactory(string path, object body, string idempotencyKey, bool injectWait)
        {
            // The key is chosen here, once per call, and not inside the factory: the
            // factory runs again for every resend, and a resend under a new key would
            // create a second job.
            string key = string.IsNullOrEmpty(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey;
            string json;
            if (injectWait)
            {
                // "wait" is injected at serialization time and is deliberately NOT a
                // field on the request models — see the note in Models/Requests.cs.
                var node = JsonSerializer.SerializeToNode(body, body.GetType(), PdfikJson.Options).AsObject();
                node["wait"] = true;
                json = node.ToJsonString(PdfikJson.Options);
            }
            else
            {
                json = JsonSerializer.Serialize(body, body.GetType(), PdfikJson.Options);
            }
            return () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                AddHeader(request, "X-API-Key", _apiKey);
                AddHeader(request, "Idempotency-Key", key);
                return request;
            };
        }

        private Func<HttpRequestMessage> GetFactory(string path)
        {
            return () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
                AddHeader(request, "X-API-Key", _apiKey);
                return request;
            };
        }

        // Headers.Add refuses a CR/LF itself, but HttpClient refuses a non-ASCII value only
        // while it writes the request, as an HttpRequestException that would read as a
        // NETWORK_ERROR and be resent. No API key or Idempotency-Key the API accepts has
        // one, so it is refused here, before anything is sent, as the CR/LF is.
        private static void AddHeader(HttpRequestMessage request, string name, string value)
        {
            foreach (char c in value)
            {
                if (c > '\u007f')
                {
                    throw new FormatException(name + " contains the non-ASCII character U+"
                        + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                        + ", which an HTTP header cannot carry (often an invisible character pasted along with the value).");
                }
            }
            request.Headers.Add(name, value);
        }

        private async Task<JobCreatedResponse> PostJobAsync(string path, object body, string idempotencyKey, CancellationToken ct)
        {
            var response = await ExecuteAsync(PostFactory(path, body, idempotencyKey, injectWait: false),
                _requestTimeout, resend: true, ThrottleWaits, Throttles, ct).ConfigureAwait(false);
            CheckResponse(response);
            return Deserialize<JobCreatedResponse>(response);
        }

        private static T Deserialize<T>(ApiResponse response)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(response.Text, PdfikJson.Options);
            }
            catch (Exception e)
            {
                throw new PdfikException("Failed to parse response body: " + e.Message, response.Status, null, response.Text);
            }
        }

        // ------------------------------------------------------------------
        // Synchronous mode ("wait": true) plumbing. The API holds the creation
        // request up to 45 s and answers with the FILE itself — or 503 #sync-timeout
        // (job cancelled, NOT charged, Idempotency-Key released) or 422 #job-failed
        // (the render failed, carrying the job's own error code). Unlike every other
        // call, 5xx responses and transport failures are NOT resent here: the one
        // attempt may already have rendered — and charged — the job, and every
        // resend holds the caller for another full render.
        // ------------------------------------------------------------------

        /// <summary>The *File methods stream the file back on the response; delivery would send it to a bucket instead.</summary>
        private static void RequireNoDelivery(DeliveryOptions delivery)
        {
            if (delivery != null)
            {
                throw new ArgumentException(
                    "The *File methods ask the API to answer with the file (\"wait\": true); delivery (BYOB) uploads " +
                    "it to your own bucket instead — the two cannot be combined. Submit without delivery, or " +
                    "use the job-creating method and let the webhook confirm the upload.");
            }
        }

        private async Task<byte[]> RequestFileAsync(string path, object body, string idempotencyKey, CancellationToken ct)
        {
            // The 70 s floor (see SyncRequestTimeout); a larger configured timeout still wins.
            var timeout = _requestTimeout > SyncRequestTimeout ? _requestTimeout : SyncRequestTimeout;
            var response = await ExecuteAsync(PostFactory(path, body, idempotencyKey, injectWait: true),
                timeout, resend: false, ThrottleWaits, Throttles, ct).ConfigureAwait(false);
            return SyncFileBody(response);
        }

        /// <summary>The file bytes of a sync ("wait": true) answer — or the exception its status and shape dictate.</summary>
        private static byte[] SyncFileBody(ApiResponse response)
        {
            CheckResponse(response); // 503 #sync-timeout, 422 #job-failed, auth/quota errors
            // An OLD server (from before synchronous mode) ignores "wait" silently and
            // answers 202 with the usual JSON. That JSON must never be returned as file
            // bytes — the job it names is real, so the message hands the caller its id
            // and the way to fetch it.
            bool json = (response.ContentType ?? "").IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
            if (response.Status == 202 || json)
            {
                string body = response.Text;
                JsonObject node = TryParseObject(body);
                string jobId = (node == null ? null : AsString(node["job_id"])) ?? "";
                throw new PdfikException(
                    "The API queued job " + jobId + " instead of answering with the file: it ignored \"wait\": true " +
                    "(a server from before synchronous mode). Fetch the job with WaitForJob(\"" + jobId +
                    "\") + Download, or use the job-creating methods.",
                    response.Status, "SYNC_MODE_UNSUPPORTED", body);
            }
            return response.Body;
        }

        // ------------------------------------------------------------------
        // Job-creating endpoints
        // ------------------------------------------------------------------

        /// <summary>Queues a URL-to-PDF render. Private IPs are blocked (SSRF protection).</summary>
        /// <param name="einvoice">Factur-X e-invoicing: the rendered page becomes the human-readable half of a hybrid PDF/A-3 with the XML embedded as factur-x.xml. Mutually exclusive with <c>options.UserPassword</c>/<c>options.Compression</c>.</param>
        /// <param name="test">Test mode: full pipeline, no rendering, free; quotas untouched (separate rate caps apply).</param>
        /// <param name="idempotencyKey">Retrying with the same key returns the original job instead of creating a duplicate.</param>
        public Task<JobCreatedResponse> UrlToPdfAsync(
            string url,
            PdfOptions options = null,
            RenderOptions render = null,
            string webhookUrl = null,
            JobAuthOptions auth = null,
            EInvoiceOptions einvoice = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new UrlToPdfRequest(url)
            {
                Options = options,
                Render = render,
                WebhookUrl = webhookUrl,
                Auth = auth,
                Einvoice = einvoice,
                Test = test,
            };
            return UrlToPdfAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>Delivery</c> (BYOB; not combinable with test mode).</summary>
        public Task<JobCreatedResponse> UrlToPdfAsync(UrlToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/url-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse UrlToPdf(string url, PdfOptions options = null, RenderOptions render = null, string webhookUrl = null, JobAuthOptions auth = null, EInvoiceOptions einvoice = null, bool? test = null, string idempotencyKey = null)
            => Sync(UrlToPdfAsync(url, options, render, webhookUrl, auth, einvoice, test, idempotencyKey));

        public JobCreatedResponse UrlToPdf(UrlToPdfRequest request, string idempotencyKey = null)
            => Sync(UrlToPdfAsync(request, idempotencyKey));

        /// <summary>Queues an HTML-to-PDF render (the markup is sanitized server-side).</summary>
        public Task<JobCreatedResponse> HtmlToPdfAsync(
            string html,
            PdfOptions options = null,
            RenderOptions render = null,
            string webhookUrl = null,
            EInvoiceOptions einvoice = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HtmlToPdfRequest(html)
            {
                Options = options,
                Render = render,
                WebhookUrl = webhookUrl,
                Einvoice = einvoice,
                Test = test,
            };
            return HtmlToPdfAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>Delivery</c> (BYOB; not combinable with test mode).</summary>
        public Task<JobCreatedResponse> HtmlToPdfAsync(HtmlToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/html-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse HtmlToPdf(string html, PdfOptions options = null, RenderOptions render = null, string webhookUrl = null, EInvoiceOptions einvoice = null, bool? test = null, string idempotencyKey = null)
            => Sync(HtmlToPdfAsync(html, options, render, webhookUrl, einvoice, test, idempotencyKey));

        public JobCreatedResponse HtmlToPdf(HtmlToPdfRequest request, string idempotencyKey = null)
            => Sync(HtmlToPdfAsync(request, idempotencyKey));

        /// <summary>
        /// Queues a Markdown-to-PDF render (CommonMark + GFM tables and strikethrough,
        /// built-in print stylesheet). Raw HTML inside the Markdown is escaped, not
        /// rendered — use <see cref="HtmlToPdfAsync(HtmlToPdfRequest, string, CancellationToken)"/> for full HTML control.
        /// </summary>
        public Task<JobCreatedResponse> MarkdownToPdfAsync(
            string markdown,
            PdfOptions options = null,
            RenderOptions render = null,
            string webhookUrl = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new MarkdownToPdfRequest(markdown)
            {
                Options = options,
                Render = render,
                WebhookUrl = webhookUrl,
                Test = test,
            };
            return MarkdownToPdfAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>Delivery</c> (BYOB; not combinable with test mode).</summary>
        public Task<JobCreatedResponse> MarkdownToPdfAsync(MarkdownToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/markdown-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse MarkdownToPdf(string markdown, PdfOptions options = null, RenderOptions render = null, string webhookUrl = null, bool? test = null, string idempotencyKey = null)
            => Sync(MarkdownToPdfAsync(markdown, options, render, webhookUrl, test, idempotencyKey));

        public JobCreatedResponse MarkdownToPdf(MarkdownToPdfRequest request, string idempotencyKey = null)
            => Sync(MarkdownToPdfAsync(request, idempotencyKey));

        /// <summary>
        /// Queues a screenshot of a public URL (PNG by default; the server default
        /// viewport is 1024x768, visible area only). Fetch the bytes with
        /// <see cref="DownloadAsync"/> — for image jobs it returns raw PNG/JPEG bytes.
        /// </summary>
        public Task<JobCreatedResponse> UrlToImageAsync(
            string url,
            ImageOptions options = null,
            RenderOptions render = null,
            string webhookUrl = null,
            JobAuthOptions auth = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new UrlToImageRequest(url)
            {
                Options = options,
                Render = render,
                WebhookUrl = webhookUrl,
                Auth = auth,
                Test = test,
            };
            return UrlToImageAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>Delivery</c> (BYOB; not combinable with test mode).</summary>
        public Task<JobCreatedResponse> UrlToImageAsync(UrlToImageRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/url-to-image", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse UrlToImage(string url, ImageOptions options = null, RenderOptions render = null, string webhookUrl = null, JobAuthOptions auth = null, bool? test = null, string idempotencyKey = null)
            => Sync(UrlToImageAsync(url, options, render, webhookUrl, auth, test, idempotencyKey));

        public JobCreatedResponse UrlToImage(UrlToImageRequest request, string idempotencyKey = null)
            => Sync(UrlToImageAsync(request, idempotencyKey));

        /// <summary>Queues a screenshot of raw HTML markup (sanitized server-side).</summary>
        public Task<JobCreatedResponse> HtmlToImageAsync(
            string html,
            ImageOptions options = null,
            RenderOptions render = null,
            string webhookUrl = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HtmlToImageRequest(html)
            {
                Options = options,
                Render = render,
                WebhookUrl = webhookUrl,
                Test = test,
            };
            return HtmlToImageAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>Delivery</c> (BYOB; not combinable with test mode).</summary>
        public Task<JobCreatedResponse> HtmlToImageAsync(HtmlToImageRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/html-to-image", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse HtmlToImage(string html, ImageOptions options = null, RenderOptions render = null, string webhookUrl = null, bool? test = null, string idempotencyKey = null)
            => Sync(HtmlToImageAsync(html, options, render, webhookUrl, test, idempotencyKey));

        public JobCreatedResponse HtmlToImage(HtmlToImageRequest request, string idempotencyKey = null)
            => Sync(HtmlToImageAsync(request, idempotencyKey));

        /// <summary>
        /// Queues a Factur-X e-invoice: PDFik builds the human-readable invoice from
        /// your Cross-Industry-Invoice XML (default profile "en16931", account default
        /// template), renders it to PDF/A-3 and embeds the XML as factur-x.xml. The
        /// output is the same clean PDF/A-3 on every plan.
        /// </summary>
        /// <param name="profile">"minimum", "basicwl", "basic", "en16931" or "extended". "minimum"/"basicwl" are accompanying data only — NOT a legally sufficient e-invoice.</param>
        public Task<JobCreatedResponse> EinvoiceToPdfAsync(
            string xml,
            string profile = null,
            string webhookUrl = null,
            bool? test = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            var request = new EInvoiceToPdfRequest(xml, profile)
            {
                WebhookUrl = webhookUrl,
                Test = test,
            };
            return EinvoiceToPdfAsync(request, idempotencyKey, cancellationToken);
        }

        /// <summary>Request-object variant — every field, including <c>TemplateId</c>/<c>Template</c> and <c>Delivery</c>.</summary>
        public Task<JobCreatedResponse> EinvoiceToPdfAsync(EInvoiceToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            return PostJobAsync("/einvoice-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public JobCreatedResponse EinvoiceToPdf(string xml, string profile = null, string webhookUrl = null, bool? test = null, string idempotencyKey = null)
            => Sync(EinvoiceToPdfAsync(xml, profile, webhookUrl, test, idempotencyKey));

        public JobCreatedResponse EinvoiceToPdf(EInvoiceToPdfRequest request, string idempotencyKey = null)
            => Sync(EinvoiceToPdfAsync(request, idempotencyKey));

        // ------------------------------------------------------------------
        // Job polling and download
        // ------------------------------------------------------------------

        public Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return GetJobAsync(jobId, ThrottleWaits, cancellationToken);
        }

        private async Task<JobStatusResponse> GetJobAsync(string jobId, int throttleWaits, CancellationToken ct)
        {
            var response = await ExecuteAsync(GetFactory("/jobs/" + jobId),
                _requestTimeout, resend: true, throttleWaits, Throttles, ct).ConfigureAwait(false);
            CheckResponse(response);
            return Deserialize<JobStatusResponse>(response);
        }

        public JobStatusResponse GetJob(string jobId) => Sync(GetJobAsync(jobId));

        /// <summary>
        /// Polls the job until it is done (default: every 2 s, up to 120 s). A failed
        /// job throws a <see cref="PdfikException"/> carrying the job's own error
        /// code; running out of time throws 408 <c>TIMEOUT</c>. A 429 while polling
        /// is a throttle, not a failure — it is waited out within the same timeout.
        /// </summary>
        public async Task<JobStatusResponse> WaitForJobAsync(string jobId, TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(120);
            var interval = pollInterval ?? TimeSpan.FromSeconds(2);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                JobStatusResponse job;
                try
                {
                    // No waits in the request path: this loop waits out a 429 itself,
                    // so one 429 costs one wait and the timeout stays in charge.
                    job = await GetJobAsync(jobId, 0, cancellationToken).ConfigureAwait(false);
                }
                catch (PdfikException e)
                {
                    if (e.StatusCode != 429) throw;
                    if (stopwatch.Elapsed >= limit)
                        throw new PdfikException("Job " + jobId + " timed out", 408, "TIMEOUT", null);
                    await DelayFunc(ThrottleDelay(e, interval), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (job.Status == JobStatus.Done) return job;
                if (job.Status == JobStatus.Failed)
                {
                    throw new PdfikException(
                        "Job " + jobId + " failed",
                        400,
                        job.ErrorCode ?? "JOB_FAILED",
                        null);
                }
                if (stopwatch.Elapsed >= limit)
                    throw new PdfikException("Job " + jobId + " timed out", 408, "TIMEOUT", null);
                await DelayFunc(interval, cancellationToken).ConfigureAwait(false);
            }
        }

        public JobStatusResponse WaitForJob(string jobId, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
            => Sync(WaitForJobAsync(jobId, timeout, pollInterval));

        /// <summary>Download location of the job's file (constructed locally; GET it with the X-API-Key header).</summary>
        public JobFileResponse GetFileUrl(string jobId)
        {
            return new JobFileResponse
            {
                JobId = jobId,
                DownloadUrl = _baseUrl + "/jobs/" + jobId + "/download",
                ExpiresInSeconds = -1,
            };
        }

        /// <summary>
        /// Downloads the finished job's output bytes — a PDF, or raw PNG/JPEG bytes
        /// for image jobs. Jobs submitted with the delivery option have no download
        /// here (404): the file lives in your own bucket. While another download of
        /// the same file is still streaming, the API answers 429 <c>download-busy</c>;
        /// that is waited out here and costs no download attempt.
        /// </summary>
        public async Task<byte[]> DownloadAsync(string jobId, CancellationToken cancellationToken = default)
        {
            var response = await ExecuteAsync(GetFactory("/jobs/" + jobId + "/download"),
                _requestTimeout, resend: true, ThrottleWaits, DownloadThrottles, cancellationToken).ConfigureAwait(false);
            CheckResponse(response);
            return response.Body;
        }

        public byte[] Download(string jobId) => Sync(DownloadAsync(jobId));

        // ------------------------------------------------------------------
        // Synchronous one-call API ("wait": true)
        // ------------------------------------------------------------------

        /// <summary>
        /// Renders a public URL and returns the PDF bytes in one call: the API holds
        /// the request until the render finishes (up to 45 s) and streams the file
        /// back — no job to poll, nothing to download. The job id rides the
        /// <c>X-PDFik-Job-Id</c> response header and the file also stays downloadable
        /// for the normal 24 h.
        /// <para>Outcomes beyond the file, all thrown as <see cref="PdfikException"/>:
        /// 503 <c>SYNC_TIMEOUT</c> — render did not finish; the job was cancelled, NOT
        /// charged, its Idempotency-Key released, and the client deliberately does not
        /// retry (every retry is a full re-render — submit with
        /// <see cref="UrlToPdfAsync(string, PdfOptions, RenderOptions, string, JobAuthOptions, EInvoiceOptions, bool?, string, CancellationToken)"/>
        /// and a webhook instead). 422 carries the job's own error code
        /// (<c>PAGE_TIMEOUT</c>, <c>SSRF_BLOCKED</c>, ...). <c>SYNC_MODE_UNSUPPORTED</c> —
        /// the server ignored <c>wait</c> and queued the job (a pre-sync-mode server);
        /// the message names the job it created.</para>
        /// <para>Unlike the job-creating methods, 5xx responses and transport failures
        /// are NOT resent — the one attempt may already have rendered (and charged)
        /// the job. Only the rate-limit and concurrency 429s are waited out, under the
        /// call's one Idempotency-Key. A request with <c>Delivery</c> set is rejected
        /// with <see cref="ArgumentException"/> before any HTTP.</para>
        /// </summary>
        public Task<byte[]> UrlToPdfFileAsync(string url, CancellationToken cancellationToken = default)
            => UrlToPdfFileAsync(new UrlToPdfRequest(url), null, cancellationToken);

        public Task<byte[]> UrlToPdfFileAsync(UrlToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/url-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public byte[] UrlToPdfFile(string url) => Sync(UrlToPdfFileAsync(url));

        public byte[] UrlToPdfFile(UrlToPdfRequest request, string idempotencyKey = null)
            => Sync(UrlToPdfFileAsync(request, idempotencyKey));

        /// <summary>Renders raw HTML and returns the PDF bytes in one call. Outcomes and the no-resend rule: see <see cref="UrlToPdfFileAsync(string, CancellationToken)"/>.</summary>
        public Task<byte[]> HtmlToPdfFileAsync(string html, CancellationToken cancellationToken = default)
            => HtmlToPdfFileAsync(new HtmlToPdfRequest(html), null, cancellationToken);

        public Task<byte[]> HtmlToPdfFileAsync(HtmlToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/html-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public byte[] HtmlToPdfFile(string html) => Sync(HtmlToPdfFileAsync(html));

        public byte[] HtmlToPdfFile(HtmlToPdfRequest request, string idempotencyKey = null)
            => Sync(HtmlToPdfFileAsync(request, idempotencyKey));

        /// <summary>Converts Markdown and returns the PDF bytes in one call. Outcomes and the no-resend rule: see <see cref="UrlToPdfFileAsync(string, CancellationToken)"/>.</summary>
        public Task<byte[]> MarkdownToPdfFileAsync(string markdown, CancellationToken cancellationToken = default)
            => MarkdownToPdfFileAsync(new MarkdownToPdfRequest(markdown), null, cancellationToken);

        public Task<byte[]> MarkdownToPdfFileAsync(MarkdownToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/markdown-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public byte[] MarkdownToPdfFile(string markdown) => Sync(MarkdownToPdfFileAsync(markdown));

        public byte[] MarkdownToPdfFile(MarkdownToPdfRequest request, string idempotencyKey = null)
            => Sync(MarkdownToPdfFileAsync(request, idempotencyKey));

        /// <summary>Captures a public URL as a screenshot and returns the image bytes in one call. Outcomes and the no-resend rule: see <see cref="UrlToPdfFileAsync(string, CancellationToken)"/>.</summary>
        public Task<byte[]> UrlToImageFileAsync(string url, CancellationToken cancellationToken = default)
            => UrlToImageFileAsync(new UrlToImageRequest(url), null, cancellationToken);

        public Task<byte[]> UrlToImageFileAsync(UrlToImageRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/url-to-image", request, idempotencyKey, cancellationToken);
        }

        public byte[] UrlToImageFile(string url) => Sync(UrlToImageFileAsync(url));

        public byte[] UrlToImageFile(UrlToImageRequest request, string idempotencyKey = null)
            => Sync(UrlToImageFileAsync(request, idempotencyKey));

        /// <summary>Captures raw HTML as a screenshot and returns the image bytes in one call. Outcomes and the no-resend rule: see <see cref="UrlToPdfFileAsync(string, CancellationToken)"/>.</summary>
        public Task<byte[]> HtmlToImageFileAsync(string html, CancellationToken cancellationToken = default)
            => HtmlToImageFileAsync(new HtmlToImageRequest(html), null, cancellationToken);

        public Task<byte[]> HtmlToImageFileAsync(HtmlToImageRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/html-to-image", request, idempotencyKey, cancellationToken);
        }

        public byte[] HtmlToImageFile(string html) => Sync(HtmlToImageFileAsync(html));

        public byte[] HtmlToImageFile(HtmlToImageRequest request, string idempotencyKey = null)
            => Sync(HtmlToImageFileAsync(request, idempotencyKey));

        /// <summary>
        /// Generates a Factur-X e-invoice PDF/A-3 from CII XML and returns its bytes
        /// in one call. The minimum/basicwl profile note that rides <c>detail</c> on
        /// the asynchronous 202 travels in the <c>X-PDFik-Warning</c> response header
        /// here. Outcomes and the no-resend rule: see <see cref="UrlToPdfFileAsync(string, CancellationToken)"/>.
        /// </summary>
        public Task<byte[]> EinvoiceToPdfFileAsync(string xml, CancellationToken cancellationToken = default)
            => EinvoiceToPdfFileAsync(new EInvoiceToPdfRequest(xml), null, cancellationToken);

        public Task<byte[]> EinvoiceToPdfFileAsync(EInvoiceToPdfRequest request, string idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            RequireNoDelivery(request.Delivery);
            return RequestFileAsync("/einvoice-to-pdf", request, idempotencyKey, cancellationToken);
        }

        public byte[] EinvoiceToPdfFile(string xml) => Sync(EinvoiceToPdfFileAsync(xml));

        public byte[] EinvoiceToPdfFile(EInvoiceToPdfRequest request, string idempotencyKey = null)
            => Sync(EinvoiceToPdfFileAsync(request, idempotencyKey));

        // ------------------------------------------------------------------
        // Webhook signature verification
        // ------------------------------------------------------------------

        /// <summary>
        /// Verifies the authenticity of an incoming PDFik webhook by validating the
        /// HMAC-SHA256 signature in the <c>X-PDFik-Signature</c> header (format
        /// <c>t=...,v1=...</c>, HMAC over "timestamp.rawBody", ±300 s window).
        /// While a rotated-out secret is still inside its overlap window the header
        /// carries one <c>v1</c> per secret, the current secret's first
        /// (<c>t=...,v1=...,v1=...</c>): every <c>v1</c> is checked, and the delivery
        /// is genuine if ANY of them matches <paramref name="webhookSecret"/>.
        /// Always verify BEFORE processing the event payload, and pass the RAW body
        /// string, never re-serialized JSON.
        /// </summary>
        /// <param name="rawBody">The raw UTF-8 request body.</param>
        /// <param name="headers">All request headers (looked up case-insensitively).</param>
        /// <param name="webhookSecret">The secret from your PDFik dashboard (<c>whsec_...</c>).</param>
        public static bool VerifyWebhookSignature(string rawBody, IDictionary<string, string> headers, string webhookSecret)
        {
            if (rawBody == null || headers == null || webhookSecret == null) return false;

            string signatureHeader = null;
            string timestampHeader = null;
            foreach (var entry in headers)
            {
                if (string.Equals(entry.Key, "x-pdfik-signature", StringComparison.OrdinalIgnoreCase))
                    signatureHeader = entry.Value;
                else if (string.Equals(entry.Key, "x-pdfik-timestamp", StringComparison.OrdinalIgnoreCase))
                    timestampHeader = entry.Value;
            }
            // X-PDFik-Timestamp must be PRESENT (the server always sends the pair),
            // but the HMAC and the ±300 s window deliberately use the t= from the
            // signature header — the same choice as the other SDKs; keep them aligned.
            if (signatureHeader == null || timestampHeader == null) return false;

            try
            {
                string timestamp = null;
                var receivedSigs = new List<string>();
                foreach (var part in signatureHeader.Split(','))
                {
                    var idx = part.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = part.Substring(0, idx).Trim();
                    var val = part.Substring(idx + 1).Trim();
                    if (key == "t") timestamp = val;
                    else if (key == "v1") receivedSigs.Add(val);
                }
                if (timestamp == null || receivedSigs.Count == 0) return false;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long ts = long.Parse(timestamp);
                if (Math.Abs(now - ts) > 300) return false;

                var signedPayload = timestamp + "." + rawBody;
                byte[] rawHmac;
                using (var mac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret)))
                {
                    rawHmac = mac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
                }
                var hex = new StringBuilder(rawHmac.Length * 2);
                foreach (var b in rawHmac) hex.Append(b.ToString("x2"));
                var expected = Encoding.UTF8.GetBytes(hex.ToString());

                // Every candidate is compared, each in constant time.
                var matched = false;
                foreach (var receivedSig in receivedSigs)
                {
                    if (FixedTimeEquals(expected, Encoding.UTF8.GetBytes(receivedSig))) matched = true;
                }
                return matched;
            }
            catch
            {
                return false;
            }
        }

        // CryptographicOperations.FixedTimeEquals is not in netstandard2.0.
        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int acc = 0;
            for (int i = 0; i < left.Length; i++) acc |= left[i] ^ right[i];
            return acc == 0;
        }

        // ------------------------------------------------------------------

        private static T Sync<T>(Task<T> task)
        {
            return task.ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
