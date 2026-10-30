using System.Collections.Generic;

namespace Pdfik.Models
{
    /// <summary>
    /// One CSS length per side: a number followed by mm/cm/in/px ("10mm", "0.5in",
    /// "20px"); a bare number is pixels. Shorthands like "10mm 5mm" are rejected
    /// by the API with 422.
    /// </summary>
    public class MarginOptions
    {
        public string Top { get; set; }
        public string Right { get; set; }
        public string Bottom { get; set; }
        public string Left { get; set; }
    }

    /// <summary>Watermark rendered on every page.</summary>
    public class WatermarkOptions
    {
        /// <summary>Watermark text. Required by the API.</summary>
        public string Text { get; set; }

        /// <summary>CSS color (default "rgba(200, 200, 200, 0.3)").</summary>
        public string Color { get; set; }

        /// <summary>CSS font size (default "60px").</summary>
        public string FontSize { get; set; }

        /// <summary>Rotation in degrees (default -45).</summary>
        public int? RotationDegrees { get; set; }
    }

    /// <summary>PDF compression: structural level and/or lossy JPEG recompression.</summary>
    public class CompressionOptions
    {
        /// <summary>Structural compression level 1-9: 1-3 basic, 4-6 object streams, 7-9 linearize.</summary>
        public int? Level { get; set; }

        /// <summary>Lossy JPEG image recompression quality (1-100).</summary>
        public int? ImageQuality { get; set; }
    }

    /// <summary>PDF styling and document options.</summary>
    public class PdfOptions
    {
        /// <summary>Paper format (server default A4).</summary>
        public PaperFormat? Format { get; set; }

        /// <summary>Landscape orientation instead of portrait.</summary>
        public bool? Landscape { get; set; }

        public MarginOptions Margin { get; set; }

        /// <summary>Render CSS background colors and images (server default true).</summary>
        public bool? PrintBackground { get; set; }

        public bool? DisplayHeaderFooter { get; set; }

        /// <summary>HTML template for the header (classes pageNumber, totalPages, date, title, url). Sanitized before rendering.</summary>
        public string HeaderTemplate { get; set; }

        /// <summary>HTML template for the footer, same classes as the header.</summary>
        public string FooterTemplate { get; set; }

        public WatermarkOptions Watermark { get; set; }

        /// <summary>Encrypt the PDF with a password. Mutually exclusive with e-invoicing (PDF/A forbids encryption).</summary>
        public string UserPassword { get; set; }

        /// <summary>Compression settings. Mutually exclusive with e-invoicing (re-saving breaks PDF/A attributes).</summary>
        public CompressionOptions Compression { get; set; }

        /// <summary>Rendering scale, 0.5-2.0. Omitted: calculated automatically.</summary>
        public double? Scale { get; set; }
    }

    /// <summary>Browser navigation/rendering options.</summary>
    public class RenderOptions
    {
        /// <summary>Browser page-load timeout in ms (0-60000).</summary>
        public int? PageLoadTimeoutMs { get; set; }

        /// <summary>Event to wait for before rendering.</summary>
        public WaitUntilEvent? WaitUntil { get; set; }

        /// <summary>CSS selector to wait for before rendering.</summary>
        public string WaitForSelector { get; set; }

        /// <summary>Additional delay after the load event, in ms (0-10000).</summary>
        public int? WaitAfterLoadMs { get; set; }
    }

    /// <summary>
    /// Credentials for rendering protected pages. The credential is attached only
    /// to same-origin requests of the render — it never leaks to third-party
    /// subresources.
    /// </summary>
    public class JobAuthOptions
    {
        /// <summary>"basic" (value is "username:password") or "bearer" (value is the raw token).</summary>
        public string Type { get; set; }

        /// <summary>"username:password" for basic, or the raw token for bearer.</summary>
        public string Value { get; set; }

        public JobAuthOptions() { }

        public JobAuthOptions(string type, string value)
        {
            Type = type;
            Value = value;
        }
    }

    /// <summary>
    /// BYOB delivery (every plan): the rendered output is uploaded straight to your
    /// own bucket via a presigned PUT URL and nothing is stored on PDFik's side.
    /// Not combinable with test mode, nor with the one-call *File methods.
    /// </summary>
    public class DeliveryOptions
    {
        /// <summary>Only "presigned_put" in v1.</summary>
        public string Mode { get; set; } = "presigned_put";

        /// <summary>
        /// Presigned PUT URL (https on the default port 443 only) for your own
        /// bucket. Generate it without a Content-Type condition and with an expiry
        /// that covers rendering plus retries — at least 15 minutes recommended.
        /// </summary>
        public string Url { get; set; }

        public DeliveryOptions() { }

        public DeliveryOptions(string url)
        {
            Url = url;
        }
    }

    /// <summary>
    /// Mode A e-invoicing on a url/html render: the rendered page becomes the
    /// human-readable half of a Factur-X hybrid — the output is normalized to
    /// PDF/A-3 with the XML embedded as factur-x.xml. Mutually exclusive with
    /// <see cref="PdfOptions.UserPassword"/> and <see cref="PdfOptions.Compression"/>.
    /// </summary>
    public class EInvoiceOptions
    {
        /// <summary>E-invoice container format. Only "factur-x" in v1.</summary>
        public string Format { get; set; } = "factur-x";

        /// <summary>
        /// Factur-X conformance profile the XML declares: "minimum", "basicwl",
        /// "basic", "en16931" (server default) or "extended". "minimum" and
        /// "basicwl" are accompanying data only and are NOT a legally sufficient
        /// e-invoice in DE/FR.
        /// </summary>
        public string Profile { get; set; }

        /// <summary>UN/CEFACT Cross-Industry-Invoice XML (UTF-8, up to 1 MB), validated against the official XSD of the declared profile.</summary>
        public string Xml { get; set; }

        public EInvoiceOptions() { }

        public EInvoiceOptions(string xml)
        {
            Xml = xml;
        }

        public EInvoiceOptions(string xml, string profile)
        {
            Xml = xml;
            Profile = profile;
        }
    }

    /// <summary>Browser window the page is opened in — you set the size, the API captures it as is (no scaling).</summary>
    public class ImageViewport
    {
        /// <summary>Width in CSS pixels (320-1920).</summary>
        public int Width { get; set; }

        /// <summary>Height in CSS pixels (320-8192).</summary>
        public int Height { get; set; }

        public ImageViewport() { }

        public ImageViewport(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    /// <summary>Screenshot output options (url-to-image / html-to-image).</summary>
    public class ImageOptions
    {
        /// <summary>"png" (server default) or "jpeg".</summary>
        public ImageFormat? Format { get; set; }

        /// <summary>
        /// Capture the whole scrollable page instead of just the visible area
        /// (server default false). With true the height follows the real page,
        /// clipped at 8192 px — a ceiling, not a target.
        /// </summary>
        public bool? FullPage { get; set; }

        /// <summary>JPEG quality 1-100 (jpeg only; the API rejects it for png).</summary>
        public int? Quality { get; set; }

        /// <summary>Viewport; server default 1024x768.</summary>
        public ImageViewport Viewport { get; set; }
    }
}
