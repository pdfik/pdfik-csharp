# Changelog

## 0.2.1 (2026-11-10)

- Fixed: `VerifyWebhookSignature` returns `false` for an empty secret (it accepted a forged
  request: an HMAC with an empty key is public).
- Fixed: `DownloadAsync` / `DownloadPdf` are not resent after a broken connection: each
  started stream is one of the file's three download attempts.
- Fixed: the `429` volume quota held by jobs that are still running is waited out like the
  request-rate throttle; a used-up volume quota is still thrown at once.

## 0.2.0 (2026-11-09)

- Fixed: `PdfikClient.VerifyWebhookSignature` checks every `v1` in
  `X-PDFik-Signature`. While the overlap window of a secret rotation is open,
  PDFik signs each delivery with both secrets and sends both signatures in the
  one header, the current secret's first:
  `t=<unix seconds>,v1=<signature with the new secret>,v1=<signature with the previous secret>`.
  The helper now accepts the delivery if any `v1` matches the secret you pass,
  comparing each one in constant time. 0.1.0 kept only the last `v1` — inside a
  window, the previous secret's — so a receiver already switched to the new
  secret would reject every genuine delivery until the window closed: upgrade
  before you rotate with an overlap window. A header with a single `v1`, the
  ±300 s timestamp rule and the `bool` result are unchanged.
- Fixed: a connection failure — no HTTP answer at all (DNS failure, refused or
  reset connection, TLS error, the per-request timeout) or a connection that
  breaks while the body is being read — now throws `PdfikException` with
  `ErrorCode` `"NETWORK_ERROR"` as well as `StatusCode` `503`, the same code the
  JavaScript, Python and Java clients report. It used to carry no error code, so
  code that branches on the code could not tell it apart from a server's own
  `503`. Retries are unchanged: the ordinary calls resend it like a `5xx` (a
  resent create keeps its `Idempotency-Key`), and the `*File` methods throw it
  after their one attempt. Its `InnerException` is the `HttpRequestException`
  behind it (for a body that broke off, that one carries the `IOException`).
- Fixed: an API key or `Idempotency-Key` with a non-ASCII character (often an
  invisible one pasted along with the value) throws `FormatException` before
  anything is sent, as a CR/LF already did. HttpClient refuses such a header only
  while writing the request, so it surfaced as a `503` connection failure (which
  the ordinary calls resent twice) although no request had been sent.
- README "Handling Webhooks" says deliveries are signed once a signing secret
  has been generated (until then they arrive unsigned), and describes the
  signature header, the ±300 s rule and the two signatures of a rotation window
  (it used to advise checking the new secret and then the old one yourself); the
  XML doc of `VerifyWebhookSignature` describes the window too. The Auto retry
  line names the `NETWORK_ERROR` code.
- Version `0.1.0` → `0.2.0` (`Pdfik.csproj`).

## 0.1.0 (2026-10-30)

First release of the official C# SDK (`Pdfik` on NuGet), targeting
`netstandard2.0` (covers .NET Framework 4.6.2+, .NET Core 2.0+) and `net8.0`.

- All job-creating endpoints: `UrlToPdf`, `HtmlToPdf`, `MarkdownToPdf`,
  `UrlToImage`, `HtmlToImage`, `EinvoiceToPdf` — each as `Task`-based
  (`...Async`) and blocking methods, with simple-argument and request-object
  variants (the latter expose `Delivery` — BYOB presigned PUT — and, for
  e-invoices, `TemplateId`/`Template`).
- Synchronous one-call mode (`wait: true`): `*File`/`*FileAsync` methods return
  the finished file's bytes directly; `Delivery` is rejected before any HTTP;
  503 `SYNC_TIMEOUT` and transport failures are deliberately never resent; a
  pre-sync-mode server's 202 answer raises `SYNC_MODE_UNSUPPORTED` naming the
  queued job. Per-request timeout floor 70 s so the server's verdict always
  arrives first.
- `GetJob`, `WaitForJob` (2 s poll / 120 s timeout by default; a 429 while
  polling is waited out inside the timeout, a failed job throws with the job's
  own error code), `GetFileUrl`, `Download` (also for image jobs; the
  `download-busy` 429 is waited out and costs no attempt).
- Retries: up to 3 attempts on 5xx and transport failures (1 s / 2 s backoff);
  rate-limit and concurrency 429s are waited out up to 5 times guided by
  `retry_after_seconds` / `Retry-After` (10 s fallback, 60 s cap) without
  consuming an attempt. An `Idempotency-Key` (yours or a generated UUID) is
  attached to every POST and reused across resends.
- RFC 7807 errors as `PdfikException` (`StatusCode`, `ErrorCode`,
  `ResponseBody`); 422 validation arrays flattened to `loc: msg; ...`.
- `PdfikClient.VerifyWebhookSignature` — HMAC-SHA256 webhook verification
  (`t=...,v1=...`, ±300 s window, constant-time comparison).
