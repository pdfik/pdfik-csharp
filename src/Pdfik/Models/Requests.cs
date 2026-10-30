using System.Collections.Generic;

namespace Pdfik.Models
{
    // The "wait" flag of the synchronous one-call mode is deliberately NOT a field
    // on these request models: the client injects it at serialization time inside
    // the *File methods only. On the plain job-creating methods a caller-set wait
    // would make the client parse file bytes as JobCreatedResponse and crash
    // confusingly (same reasoning as the Java SDK).

    /// <summary>Request body for POST /url-to-pdf.</summary>
    public class UrlToPdfRequest
    {
        /// <summary>Public URL of the page to convert. Private IPs are blocked.</summary>
        public string Url { get; set; }

        /// <summary>Optional callback URL; PDFik POSTs the signed job outcome there.</summary>
        public string WebhookUrl { get; set; }

        public PdfOptions Options { get; set; }
        public RenderOptions Render { get; set; }
        public JobAuthOptions Auth { get; set; }
        public EInvoiceOptions Einvoice { get; set; }
        public DeliveryOptions Delivery { get; set; }

        /// <summary>Test mode: full pipeline, no rendering, free; the download serves the bundled sample file.</summary>
        public bool? Test { get; set; }

        public UrlToPdfRequest() { }

        public UrlToPdfRequest(string url)
        {
            Url = url;
        }
    }

    /// <summary>Request body for POST /html-to-pdf.</summary>
    public class HtmlToPdfRequest
    {
        /// <summary>Raw HTML markup to convert (sanitized server-side; max 512 KiB).</summary>
        public string Html { get; set; }

        public string WebhookUrl { get; set; }
        public PdfOptions Options { get; set; }
        public RenderOptions Render { get; set; }
        public EInvoiceOptions Einvoice { get; set; }
        public DeliveryOptions Delivery { get; set; }
        public bool? Test { get; set; }

        public HtmlToPdfRequest() { }

        public HtmlToPdfRequest(string html)
        {
            Html = html;
        }
    }

    /// <summary>Request body for POST /markdown-to-pdf (CommonMark + GFM tables and strikethrough; raw HTML inside is escaped, not rendered).</summary>
    public class MarkdownToPdfRequest
    {
        public string Markdown { get; set; }
        public string WebhookUrl { get; set; }
        public PdfOptions Options { get; set; }
        public RenderOptions Render { get; set; }
        public DeliveryOptions Delivery { get; set; }
        public bool? Test { get; set; }

        public MarkdownToPdfRequest() { }

        public MarkdownToPdfRequest(string markdown)
        {
            Markdown = markdown;
        }
    }

    /// <summary>Request body for POST /url-to-image (PNG/JPEG screenshot of a public URL).</summary>
    public class UrlToImageRequest
    {
        public string Url { get; set; }
        public string WebhookUrl { get; set; }
        public ImageOptions Options { get; set; }
        public RenderOptions Render { get; set; }
        public JobAuthOptions Auth { get; set; }
        public DeliveryOptions Delivery { get; set; }
        public bool? Test { get; set; }

        public UrlToImageRequest() { }

        public UrlToImageRequest(string url)
        {
            Url = url;
        }
    }

    /// <summary>Request body for POST /html-to-image (PNG/JPEG screenshot of raw HTML).</summary>
    public class HtmlToImageRequest
    {
        public string Html { get; set; }
        public string WebhookUrl { get; set; }
        public ImageOptions Options { get; set; }
        public RenderOptions Render { get; set; }
        public DeliveryOptions Delivery { get; set; }
        public bool? Test { get; set; }

        public HtmlToImageRequest() { }

        public HtmlToImageRequest(string html)
        {
            Html = html;
        }
    }

    /// <summary>
    /// Request body for POST /einvoice-to-pdf: PDFik builds the human-readable
    /// invoice from your Cross-Industry-Invoice XML using a block template, renders
    /// it to PDF/A-3 and embeds the XML as factur-x.xml (Factur-X / ZUGFeRD hybrid).
    /// <para><see cref="TemplateId"/> (a saved dashboard template) and
    /// <see cref="Template"/> (an inline block-template definition) are mutually
    /// exclusive; omit both to use the account default (or built-in) template.</para>
    /// </summary>
    public class EInvoiceToPdfRequest
    {
        /// <summary>UN/CEFACT Cross-Industry-Invoice XML (UTF-8, up to 1 MB), validated against the official XSD of the declared profile before any quota is spent.</summary>
        public string Xml { get; set; }

        /// <summary>"minimum", "basicwl", "basic", "en16931" (server default) or "extended". "minimum"/"basicwl" are accompanying data only, NOT a legally sufficient e-invoice.</summary>
        public string Profile { get; set; }

        /// <summary>Id of a saved dashboard template.</summary>
        public string TemplateId { get; set; }

        /// <summary>Inline block-template definition.</summary>
        public Dictionary<string, object> Template { get; set; }

        public string WebhookUrl { get; set; }
        public DeliveryOptions Delivery { get; set; }
        public bool? Test { get; set; }

        public EInvoiceToPdfRequest() { }

        public EInvoiceToPdfRequest(string xml)
        {
            Xml = xml;
        }

        public EInvoiceToPdfRequest(string xml, string profile)
        {
            Xml = xml;
            Profile = profile;
        }
    }
}
