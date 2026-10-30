using System;

namespace Pdfik
{
    /// <summary>
    /// Error raised by the PDFik API or by the client itself. PDFik errors follow
    /// RFC 7807: the body's <c>detail</c> becomes <see cref="Exception.Message"/>
    /// (request-validation 422 arrays are flattened to "loc: msg; ..."), the
    /// <c>error</c> code (or legacy <c>error_code</c>) becomes
    /// <see cref="ErrorCode"/>, and the raw body is kept in
    /// <see cref="ResponseBody"/> so nothing the server said is lost.
    /// </summary>
    public class PdfikException : Exception
    {
        /// <summary>HTTP status code of the failed call (503 for connection failures, 408 for a polling timeout).</summary>
        public int StatusCode { get; }

        /// <summary>Machine-readable error code (e.g. <c>SSRF_BLOCKED</c>, <c>SYNC_TIMEOUT</c>, <c>TIMEOUT</c>), when the API named one.</summary>
        public string ErrorCode { get; }

        /// <summary>The raw response body, when there was one.</summary>
        public string ResponseBody { get; }

        public PdfikException(string message, int statusCode, string errorCode, string responseBody)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
            ResponseBody = responseBody;
        }
    }
}
