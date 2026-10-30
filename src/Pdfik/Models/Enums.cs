using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pdfik.Models
{
    /// <summary>Paper formats the renderer is calibrated for (serialized exactly as the API spells them, e.g. "A4", "Letter").</summary>
    [JsonConverter(typeof(PaperFormatConverter))]
    public enum PaperFormat
    {
        A0, A1, A2, A3, A4, A5, A6, Letter, Legal, Tabloid, Ledger
    }

    /// <summary>Browser event <c>render.wait_until</c> waits for.</summary>
    [JsonConverter(typeof(WaitUntilConverter))]
    public enum WaitUntilEvent
    {
        Load,
        DomContentLoaded,
        NetworkIdle,
        Commit
    }

    /// <summary>Screenshot output format.</summary>
    [JsonConverter(typeof(ImageFormatConverter))]
    public enum ImageFormat
    {
        Png,
        Jpeg
    }

    /// <summary>Lifecycle status of a job.</summary>
    [JsonConverter(typeof(JobStatusConverter))]
    public enum JobStatus
    {
        Queued,
        Rendering,
        Uploading,
        Done,
        Failed
    }

    internal abstract class MappedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly Dictionary<T, string> _toJson;
        private readonly Dictionary<string, T> _fromJson;

        protected MappedEnumConverter(Dictionary<T, string> map, Dictionary<string, T> aliases = null)
        {
            _toJson = map;
            _fromJson = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map) _fromJson[pair.Value] = pair.Key;
            if (aliases != null)
                foreach (var pair in aliases) _fromJson[pair.Key] = pair.Value;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (text != null && _fromJson.TryGetValue(text.Trim(), out var value)) return value;
            throw new JsonException($"Unknown {typeof(T).Name} value: '{text}'");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(_toJson[value]);
        }
    }

    internal sealed class PaperFormatConverter : MappedEnumConverter<PaperFormat>
    {
        public PaperFormatConverter() : base(new Dictionary<PaperFormat, string>
        {
            { PaperFormat.A0, "A0" }, { PaperFormat.A1, "A1" }, { PaperFormat.A2, "A2" },
            { PaperFormat.A3, "A3" }, { PaperFormat.A4, "A4" }, { PaperFormat.A5, "A5" },
            { PaperFormat.A6, "A6" }, { PaperFormat.Letter, "Letter" }, { PaperFormat.Legal, "Legal" },
            { PaperFormat.Tabloid, "Tabloid" }, { PaperFormat.Ledger, "Ledger" },
        })
        { }
    }

    internal sealed class WaitUntilConverter : MappedEnumConverter<WaitUntilEvent>
    {
        public WaitUntilConverter() : base(new Dictionary<WaitUntilEvent, string>
        {
            { WaitUntilEvent.Load, "load" },
            { WaitUntilEvent.DomContentLoaded, "domcontentloaded" },
            { WaitUntilEvent.NetworkIdle, "networkidle" },
            { WaitUntilEvent.Commit, "commit" },
        })
        { }
    }

    internal sealed class ImageFormatConverter : MappedEnumConverter<ImageFormat>
    {
        public ImageFormatConverter() : base(
            new Dictionary<ImageFormat, string>
            {
                { ImageFormat.Png, "png" },
                { ImageFormat.Jpeg, "jpeg" },
            },
            // The API accepts "jpg" as an alias; tolerate it on the way in too.
            new Dictionary<string, ImageFormat> { { "jpg", ImageFormat.Jpeg } })
        { }
    }

    internal sealed class JobStatusConverter : MappedEnumConverter<JobStatus>
    {
        public JobStatusConverter() : base(new Dictionary<JobStatus, string>
        {
            { JobStatus.Queued, "queued" },
            { JobStatus.Rendering, "rendering" },
            { JobStatus.Uploading, "uploading" },
            { JobStatus.Done, "done" },
            { JobStatus.Failed, "failed" },
        })
        { }
    }
}
