using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdfik
{
    internal static class PdfikJson
    {
        /// <summary>
        /// The one serializer configuration of the SDK: snake_case property names
        /// (WebhookUrl &lt;-&gt; webhook_url), null fields omitted from request
        /// bodies, unknown response fields ignored, and a lenient timestamp
        /// reader (the API sometimes sends naive ISO timestamps without a zone —
        /// they are UTC).
        /// </summary>
        internal static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new LenientDateTimeOffsetConverter());
            return options;
        }
    }

    /// <summary>
    /// Reads ISO-8601 timestamps with or without a zone designator: "...Z", an
    /// explicit offset, fractional seconds of any length, and the naive form a
    /// Python datetime serializes to — a naive timestamp is UTC.
    /// </summary>
    internal sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        public override bool HandleNull => true;

        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }
            throw new JsonException($"Unparseable timestamp: '{text}'");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}
