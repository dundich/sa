using System.Text.Json.Serialization;

namespace Sa.Media.FFmpeg.Services;

[JsonSerializable(typeof(FFProbeMetaDataInfo))]
internal partial class FFmpegJsonSerializerContext : JsonSerializerContext { }

internal sealed record FFProbeMetaDataInfo(
    [property: JsonPropertyName("format")] FFProbeFormat Format
);

internal sealed record FFProbeFormat(
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("format_name")] string? FormatName,
    [property: JsonPropertyName("bit_rate")] string? BitRate,
    [property: JsonPropertyName("size")] string? Size
);
