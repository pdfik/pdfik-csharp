# Changelog

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
