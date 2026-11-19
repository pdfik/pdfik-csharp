using System;

namespace Pdfik.Models
{
    /// <summary>202 answer of the job-creating endpoints.</summary>
    public class JobCreatedResponse
    {
        public string JobId { get; set; }

        /// <summary>"queued" on creation.</summary>
        public string Status { get; set; }

        public string Detail { get; set; }
    }

    /// <summary>Performance and size metrics of a finished job.</summary>
    public class JobMetrics
    {
        public long? FileSizeBytes { get; set; }
        public string FileSizeHuman { get; set; }
        public int? PageLoadMs { get; set; }
        public int? ProcessingMs { get; set; }
        public int? TotalDurationMs { get; set; }
        public int? PageCount { get; set; }
    }

    /// <summary>GET /jobs/{id} answer.</summary>
    public class JobStatusResponse
    {
        public JobStatus Status { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        /// <summary>
        /// When the generated file stops being downloadable (410 after). Null until
        /// the job finishes — and stays null for a done job submitted with the
        /// delivery option, because nothing is stored on PDFik's side.
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>True when the job was submitted in test mode.</summary>
        public bool? Test { get; set; }

        public int? PagesCount { get; set; }

        /// <summary>Error code when the job failed (e.g. SSRF_BLOCKED, FILE_TOO_LARGE).</summary>
        public string ErrorCode { get; set; }

        public JobMetrics Metrics { get; set; }
    }

    /// <summary>Download location of a finished job's file (constructed locally; the URL needs the X-API-Key header).</summary>
    public class JobFileResponse
    {
        public string JobId { get; set; }

        public string DownloadUrl { get; set; }

        /// <summary>-1: the link does not expire by itself — the file's own 24 h retention applies.</summary>
        public int ExpiresInSeconds { get; set; }
    }

    /// <summary>
    /// GET /account answer: the key's account — plan, current quota window, usage in it
    /// and limits.
    /// </summary>
    public class AccountResponse
    {
        /// <summary>free, starter, pro or business.</summary>
        public string Plan { get; set; }

        /// <summary>
        /// The quota window limits are counted in: the UTC calendar month on the Free plan;
        /// on paid plans monthly from the subscription's billing day.
        /// </summary>
        public AccountWindow Window { get; set; }

        public AccountUsage Usage { get; set; }

        public AccountLimits Limits { get; set; }

        public AccountTestMode TestMode { get; set; }

        public AccountKey Key { get; set; }
    }

    public class AccountWindow
    {
        /// <summary>Start of the window (inclusive, UTC).</summary>
        public DateTimeOffset Start { get; set; }

        /// <summary>End of the window (exclusive, UTC): when it resets.</summary>
        public DateTimeOffset End { get; set; }
    }

    public class AccountUsage
    {
        /// <summary>Documents charged in this window (new jobs are refused at the limit); null if unknown right now.</summary>
        public long? Documents { get; set; }

        /// <summary>Bytes of generated output finished in this window.</summary>
        public long Bytes { get; set; }
    }

    public class AccountLimits
    {
        /// <summary>Documents per window; null means unlimited.</summary>
        public long? Documents { get; set; }

        /// <summary>Bytes of generated output per window; null means unlimited.</summary>
        public long? Bytes { get; set; }

        public int RequestsPerMinute { get; set; }

        public int ConcurrentJobs { get; set; }

        public long MaxFileSizeBytes { get; set; }
    }

    public class AccountTestMode
    {
        public int PerMinute { get; set; }

        public int PerDay { get; set; }
    }

    public class AccountKey
    {
        /// <summary>The key's name in the dashboard; null if it has none.</summary>
        public string Name { get; set; }
    }
}
