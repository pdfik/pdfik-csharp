# Pdfik

> **Where this code lives:** extracted from the PDFik platform monorepo (last sync 2026-11-11).
> Releases to NuGet are cut from the monorepo; issues and PRs are welcome here.

Official C# SDK for [PDFik](https://pdfik.net) — the URL/HTML-to-PDF API.

One call gets you the file: `UrlToPdfFile("https://example.com")` holds the request while PDFik renders (up to 45 s) and returns the PDF bytes. For batches and long renders the asynchronous flow is there too: submit, get a job id back, and receive an HMAC-signed webhook (or poll) when the PDF is ready. Rendering runs on sandboxed headless Chromium, so modern CSS, web fonts and JavaScript-heavy pages come out the way they look in the browser.

## Features

- **One-call rendering**: the `*File` methods (`UrlToPdfFile`, `HtmlToPdfFile`, `MarkdownToPdfFile`, `UrlToImageFile`, `HtmlToImageFile`, `EinvoiceToPdfFile`, each with a `...FileAsync` twin) send `"wait": true` and return the file bytes directly — no polling, no download step.
- **Task-based & blocking**: every method exists as `...Async` returning `Task<...>` and as a blocking twin; one `PdfikClient` instance is thread-safe and meant to be reused.
- **Wide reach**: targets `netstandard2.0` (covers .NET Framework 4.6.2+, .NET Core 2.0+, Mono/Unity) and `net8.0`; the only dependency is `System.Text.Json`.
- **Auto retry**: `5xx` responses and network failures are retried with exponential backoff (3 attempts). A network failure — no HTTP answer at all, or a connection that breaks while the body is being read — is thrown as `PdfikException` with `ErrorCode` `"NETWORK_ERROR"` and `StatusCode` `503`. A rate-limit or concurrency-limit `429` is waited out on every call — create, status and download — for the time the API states (up to 5 waits per call, each capped at 60 s), and so is a busy download (`download-busy`); every other `429` (quota, test-mode caps, an exhausted download counter) is thrown at once. Every create call carries an `Idempotency-Key` — yours if you pass one, otherwise a random one generated once per call and repeated each time that call is sent again — so a retried create cannot produce a second job. **The `*File` methods are the carve-out**: beyond the throttle waits they never resend — a `5xx`, a network failure and in particular the `503` [sync-timeout](https://docs.pdfik.net/error-codes#sync-timeout) are thrown at once, because the one attempt may already have rendered (and charged) the job, and every automatic retry would be a full re-render. A timed-out sync job was cancelled, not charged, and its `Idempotency-Key` released, so whether to retry is your call.
- **RFC 7807 errors**: every API error is a `PdfikException` with `StatusCode`, `ErrorCode` and the raw `ResponseBody`; `422` validation arrays are flattened to readable `field: message` text.

## Installation

```bash
dotnet add package Pdfik
```

## Quick Start

### One call, file back

`UrlToPdfFile` sends `"wait": true` for you: the API holds the request while the page renders (up to 45 s) and answers with the PDF itself — nothing to poll, nothing to download:

```csharp
using Pdfik;

using var client = new PdfikClient("sk_live_..."); // Retrieve from your dashboard

byte[] pdf = client.UrlToPdfFile("https://example.com");
File.WriteAllBytes("output.pdf", pdf);
Console.WriteLine($"PDF saved to output.pdf ({pdf.Length} bytes)");
```

Every creation endpoint has this one-call form — `UrlToPdfFile`, `HtmlToPdfFile`, `MarkdownToPdfFile`, `UrlToImageFile`, `HtmlToImageFile` (image bytes back), `EinvoiceToPdfFile` — each also taking the full request object plus an optional `Idempotency-Key`, and each with a `...FileAsync` twin returning `Task<byte[]>`. What to expect beyond the file, all thrown as `PdfikException`:

- **`503` `SYNC_TIMEOUT`** — the render did not finish within the 45-second budget. The job was cancelled and **not charged**, and its `Idempotency-Key` was released, so retrying is safe — but the SDK deliberately never retries it for you (every retry is a full re-render). For renders this long, use the asynchronous flow below.
- **`422`** carrying the job's own error code (`PAGE_TIMEOUT`, `PAGE_NOT_FOUND`, ...) via `ErrorCode` — the render failed; the response body names the `job_id`.
- A server from before synchronous mode ignores `wait` and answers `202` with the usual JSON; the `*File` methods detect that and throw `SYNC_MODE_UNSUPPORTED` naming the job that was created, instead of handing JSON back as file bytes.

Two deliberate differences from the job-creating methods: `5xx` responses and network failures are **not** resent (only the rate-limit/concurrency `429` waits remain — see Auto retry above), and these requests run with a per-request timeout of at least 70 s whatever `RequestTimeout` you configured, so the server's verdict always arrives before the client hangs up. The `Delivery` (BYOB) option cannot be combined with `wait` and is rejected with `ArgumentException` before any HTTP. The finished file also stays downloadable for the normal 24 h; its job id rides the `X-PDFik-Job-Id` response header.

### Asynchronous flow: submit, poll (or webhook), download

For batches, webhook-driven pipelines and renders longer than 45 s:

```csharp
using Pdfik;
using Pdfik.Models;

using var client = new PdfikClient("sk_live_..."); // Retrieve from your dashboard

// Configure PDF styling and margins
var options = new PdfOptions
{
    Format = PaperFormat.A4,
    Landscape = false,
    PrintBackground = true,
    Margin = new MarginOptions { Top = "10mm", Right = "10mm", Bottom = "10mm", Left = "10mm" },
};

// 1. Submit the URL to be rendered
JobCreatedResponse job = await client.UrlToPdfAsync("https://example.com", options);
Console.WriteLine($"Job created: {job.JobId}. Processing...");

// 2. Poll until the job completes
JobStatusResponse result = await client.WaitForJobAsync(
    job.JobId,
    timeout: TimeSpan.FromSeconds(120),
    pollInterval: TimeSpan.FromSeconds(2));
Console.WriteLine($"Job finished! Pages: {result.PagesCount}");

// 3. Download the PDF bytes
byte[] pdfBytes = await client.DownloadAsync(job.JobId);
File.WriteAllBytes("output.pdf", pdfBytes);
Console.WriteLine("PDF saved to output.pdf");
```

### Convert HTML to PDF

```csharp
using Pdfik;

using var client = new PdfikClient("sk_live_...");

var job = await client.HtmlToPdfAsync("<h1>Hello World</h1><p>Sent from the C# SDK</p>");
Console.WriteLine($"Job created: {job.JobId}");

await client.WaitForJobAsync(job.JobId);
byte[] pdfBytes = await client.DownloadAsync(job.JobId);
Console.WriteLine($"Downloaded {pdfBytes.Length} bytes.");
```

### Get the direct download URL

Get the direct API download URL for the generated PDF (requires the `X-API-Key` header to download):

```csharp
JobFileResponse fileInfo = client.GetFileUrl(job.JobId);
Console.WriteLine($"Direct Download URL: {fileInfo.DownloadUrl}");
```

### Test mode

Pass `test: true` to run a job through the full pipeline (statuses, webhook, download) without real rendering — the download returns a small sample PDF. Test jobs are free (no PDF or byte quota is debited) and rate-limited instead: 60 test calls per minute and 2,000 per day per account. Job status and webhook payloads always carry `test: true|false`, so you can safely exercise your integration end to end:

```csharp
var job = await client.UrlToPdfAsync("https://example.com", test: true);

var result = await client.WaitForJobAsync(job.JobId);
Console.WriteLine(result.Test);      // True
Console.WriteLine(result.ExpiresAt); // e.g. 2026-08-05T12:00:00Z

byte[] pdfBytes = await client.DownloadAsync(job.JobId); // sample PDF
```

### Markdown to PDF

`MarkdownToPdf` converts Markdown (CommonMark + GFM tables and strikethrough) with a built-in print stylesheet. Raw HTML inside the Markdown is escaped, not rendered — use `HtmlToPdf` for full HTML control. The same `PdfOptions` (paper format, margins, header/footer, watermark, ...) and job flow apply. The Markdown may be up to 100,000 characters (longer input is rejected with `422`); Markdown whose converted document is too large for the processing queue is rejected with `413` ([payload-too-large-for-queue](https://docs.pdfik.net/error-codes#payload-too-large-for-queue)) and is not charged. Example:

```csharp
// Simplest form
var job = await client.MarkdownToPdfAsync("# Report\n\n| Item | Price |\n| --- | --- |\n| Render | $0.01 |");

// Full control via the request object (+ optional Idempotency-Key)
var request = new MarkdownToPdfRequest("# Report\n\nHello **world**")
{
    Options = new PdfOptions { Format = PaperFormat.A4 },
};
job = await client.MarkdownToPdfAsync(request, "report-2026-001");

await client.WaitForJobAsync(job.JobId);
byte[] pdfBytes = await client.DownloadAsync(job.JobId);
```

### Screenshots

`UrlToImage` / `HtmlToImage` capture a page as a PNG (default) or JPEG instead of a PDF. `ImageOptions`: `Format` (`ImageFormat.Png` | `ImageFormat.Jpeg`), `FullPage` (default `false` — the visible area only; with `true` the height follows the real page and is clipped at 8,192 px, which is a ceiling and not a target, so a 2,000 px page still gives a 2,000 px image, and horizontal overflow beyond the viewport width is never captured), `Quality` (1-100, JPEG only) and `Viewport` (`new ImageViewport(width, height)`; you pick the window size and we capture exactly that — nothing is scaled or fitted — `width` 320-1920, `height` 320-8192, defaults to 1024x768). Polling is identical to the PDF endpoints, and `Download` returns the raw image bytes (`image/png` or `image/jpeg`, filename `{jobId}.png`/`.jpg`):

```csharp
var request = new UrlToImageRequest("https://example.com")
{
    Options = new ImageOptions
    {
        Format = ImageFormat.Jpeg,
        Quality = 80,
        FullPage = true,
        Viewport = new ImageViewport(1280, 720),
    },
};
var job = await client.UrlToImageAsync(request);

await client.WaitForJobAsync(job.JobId);
byte[] imageBytes = await client.DownloadAsync(job.JobId); // JPEG bytes

var shot = await client.HtmlToImageAsync("<h1>Hello</h1>"); // 1024x768 PNG
```

`UrlToImageRequest` also accepts the `Auth` option (basic/bearer), exactly as `UrlToPdf`. Passing `Quality` together with PNG is rejected with `422`.

### Deliver to your own bucket (BYOB)

Set `Delivery` (every plan, on every request object — `UrlToPdfRequest`, `HtmlToPdfRequest`, `MarkdownToPdfRequest`, `UrlToImageRequest`, `HtmlToImageRequest`, `EInvoiceToPdfRequest`) and the output is uploaded straight to your own bucket via a presigned PUT URL; nothing is stored on PDFik's side. The URL must be `https` on the standard port 443 (any other port is rejected with `422`); presign it for at least 15 minutes and without a Content-Type condition:

```csharp
var request = new UrlToPdfRequest("https://example.com")
{
    Delivery = new DeliveryOptions(presignedPutUrl), // mode "presigned_put" is the only mode
};
var job = await client.UrlToPdfAsync(request);

await client.WaitForJobAsync(job.JobId); // "done" means the PUT to your bucket succeeded
```

Once a delivered job is done, the `job.finished` webhook reports the outcome only: it carries neither `file_url` nor `expires_at`, because PDFik keeps no copy and never records where the file went (the presigned URL is a credential, so it is used once and forgotten). You already know the destination — you signed it. `Download` answers `404` ([output-delivered-externally](https://docs.pdfik.net/error-codes#output-delivered-externally)) — the file only exists in your bucket. `Delivery` cannot be combined with test mode (`400`).

### Factur-X e-invoices

PDFik can produce hybrid e-invoices (Factur-X / ZUGFeRD): a PDF/A-3 file with your UN/CEFACT Cross-Industry-Invoice XML embedded as `factur-x.xml`. Two ways to get one:

**1. From the XML alone (`EinvoiceToPdf`)** — PDFik builds the human-readable invoice from the XML with a block template (the account default, a saved dashboard template via `TemplateId`, or an inline `Template`), renders it to PDF/A-3 and embeds the XML. The normal job flow applies afterwards (`WaitForJob` + `Download`):

```csharp
// Simplest form: default template, profile defaults to en16931
var job = await client.EinvoiceToPdfAsync(ciiXml);

// Full control via the request class (+ optional Idempotency-Key)
var request = new EInvoiceToPdfRequest(ciiXml)
{
    Profile = "en16931",     // must match the XML's guideline ID: minimum | basicwl | basic | en16931 | extended
    TemplateId = "tpl_...",  // saved dashboard template; mutually exclusive with Template
};
job = await client.EinvoiceToPdfAsync(request, "invoice-2026-001");

var result = await client.WaitForJobAsync(job.JobId);
byte[] pdfA3 = await client.DownloadAsync(job.JobId);
```

**2. Attached to your own rendering (the `Einvoice` option on `UrlToPdf` / `HtmlToPdf`)** — the page you render becomes the visual half of the hybrid; the output is normalized to PDF/A-3 with the XML embedded:

```csharp
var einvoice = new EInvoiceOptions(ciiXml, "en16931");
var job = await client.HtmlToPdfAsync(invoiceHtml, einvoice: einvoice);
```

Notes:

- The XML (UTF-8, up to 1 MB) is validated against the official XSD of the declared profile before any quota is spent; its `GuidelineSpecifiedDocumentContextParameter` must match the profile. TypeCode 380 (invoice) and 381 (credit note) are supported. Invalid XML returns `422` ([einvoice-xml-invalid](https://docs.pdfik.net/error-codes#einvoice-xml-invalid)).
- The output is validated with veraPDF and Mustangproject. Schema-valid does not mean tax-compliant — the invoice content remains your responsibility.
- Profiles `minimum` and `basicwl` carry accompanying data only and are NOT a legally sufficient e-invoice; use `basic`, `en16931` or `extended` for a full invoice.
- The `Einvoice` option is mutually exclusive with `UserPassword` (PDF/A forbids encryption) and `Compression` (re-saving breaks the PDF/A attributes).
- Available on every plan, Free included — the output is the same clean PDF/A-3.

## Limits, retention and error codes

- **Generated volume per month** (byte quota, counts rendered output only — downloads are free): Free 0.5 GB, Starter 10 GB, Pro 50 GB, Business 300 GB. Business plans can purchase additional +1 GB blocks from the dashboard. Exceeding the quota returns `429` ([quota-bytes-exceeded](https://docs.pdfik.net/error-codes#quota-bytes-exceeded)).
- **File size**: up to 150 MB per PDF on every plan — contact support if you need more. A larger render fails the job with `FILE_TOO_LARGE` ([file-too-large](https://docs.pdfik.net/error-codes#file-too-large)).
- **Retention**: each file is available for download for 24 hours after the job finishes; the exact deadline is `JobStatusResponse.ExpiresAt` (`expires_at` in the webhook payload). After that the download returns `410` ([file-expired](https://docs.pdfik.net/error-codes#file-expired)).
- **Downloads**: at most 3 download attempts per file (counted when the stream starts); after that the API returns `429` ([download-attempts-exhausted](https://docs.pdfik.net/error-codes#download-attempts-exhausted)) — re-render the file or contact support. Only 1 download stream may be active at a time; a concurrent request returns `429` ([download-busy](https://docs.pdfik.net/error-codes#download-busy)), which does not burn an attempt — `Download` waits the ~10 seconds the API states and asks again by itself.

### Handling Webhooks

If you specify `WebhookUrl` on any create call, PDFik sends an HTTP `POST` request to your server when the rendering job is finished.

You should verify the authenticity of the webhook by validating the signature. The static helper `PdfikClient.VerifyWebhookSignature` does that.

Signing is on once you generate a signing secret on the Webhooks page of the dashboard (every plan, Free included); until then — or after you delete the secret — deliveries arrive without the `X-PDFik-Signature` and `X-PDFik-Timestamp` headers. While a secret is set, every delivery carries `X-PDFik-Signature: t=<unix seconds>,v1=<signature>` — the hex HMAC-SHA256 of `<t>.<raw body>` under your webhook secret — and `X-PDFik-Timestamp`; a `t` more than 300 seconds away from now is rejected. When you rotate the secret with an overlap window (picked in the dashboard: 1 hour to 5 days, or none at all), every delivery inside the window is signed with both secrets, current first: `t=<unix seconds>,v1=<signature with the new secret>,v1=<signature with the previous secret>`. `VerifyWebhookSignature` accepts a delivery if any `v1` matches the secret you pass, so a receiver still holding the previous secret keeps working — switch each receiver to the new secret before the window closes.

Custom delivery headers (e.g. a Cloudflare Access service token) are configured once on the dashboard Webhooks page and attached to every delivery — nothing to pass per request.

#### Example (ASP.NET Core minimal API):

```csharp
using Pdfik;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

const string WebhookSecret = "whsec_..."; // From PDFik dashboard

app.MapPost("/webhook", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string rawBody = await reader.ReadToEndAsync();

    var headers = request.Headers.ToDictionary(
        h => h.Key, h => h.Value.ToString());

    bool valid = PdfikClient.VerifyWebhookSignature(rawBody, headers, WebhookSecret);
    if (!valid)
    {
        return Results.Unauthorized();
    }

    // Signature is valid. Process the event payload...
    return Results.Ok(new { received = true });
});

app.Run();
```

## License

MIT License.
