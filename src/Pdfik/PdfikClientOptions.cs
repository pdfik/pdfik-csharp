using System;
using System.Net.Http;

namespace Pdfik
{
    /// <summary>Configuration for <see cref="PdfikClient"/>.</summary>
    public class PdfikClientOptions
    {
        /// <summary>Your PDFik API key (<c>sk_live_...</c>). Required.</summary>
        public string ApiKey { get; set; }

        /// <summary>API base URL. Trailing slashes are trimmed.</summary>
        public string BaseUrl { get; set; } = "https://api.pdfik.net";

        /// <summary>
        /// Per-request timeout for the ordinary calls. The one-call *File methods
        /// ("wait": true) use at least 70 s regardless (the server answers by 45 s
        /// and the edge cuts at 60 s, so the server's verdict always beats the
        /// client's own timeout); a LARGER value configured here still wins there.
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Optional custom HTTP handler (proxies, DI, tests). The client does not
        /// dispose a handler you supply.
        /// </summary>
        public HttpMessageHandler HttpMessageHandler { get; set; }
    }
}
