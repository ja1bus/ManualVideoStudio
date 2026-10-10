using System.Globalization;
using System.Text.Json;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.FFmpeg;

public interface IMediaMetadataService
{
    Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default);
}

/// <summary>ffprobe で動画情報を取得する（FFprobeService の役割）。</summary>
public sealed class MediaMetadataService(IProcessRunner runner, ILogger<MediaMetadataService>? logger = null) : IMediaMetadataService
{
    private readonly ILogger _logger = logger ?? NullLogger<MediaMetadataService>.Instance;

    public static IReadOnlyList<string> BuildArguments(string videoPath)
        => ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", videoPath];

    public async Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new ProcessRequest(ffprobePath, BuildArguments(videoPath)) { Timeout = TimeSpan.FromSeconds(60) }, ct);
        if (!result.Succeeded)
        {
            _logger.LogError("ffprobe failed for {Path}: {Error}", videoPath, result.ErrorTail(20));
            throw new InvalidOperationException($"動画情報を取得できませんでした。対応していない形式か、ファイルが壊れている可能性があります。{Environment.NewLine}{result.ErrorTail(3)}");
        }
        try
        {
            var meta = FfprobeJsonParser.Parse(result.StandardOutput);
            if (meta.FileSize == 0 && File.Exists(videoPath)) meta = meta with { FileSize = new FileInfo(videoPath).Length };
            return meta;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            _logger.LogError(ex, "ffprobe JSON parse failed for {Path}", videoPath);
            throw new InvalidOperationException("ffprobe の出力を解析できませんでした。", ex);
        }
    }
}

/// <summary>ffprobe -print_format json の出力を VideoMetadata に変換する純粋ロジック。</summary>
public static class FfprobeJsonParser
{
    public static VideoMetadata Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type == "video" && video is null && !IsAttachedPicture(s)) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }
        if (video is null) throw new InvalidOperationException("映像ストリームがありません。");

        double duration = Dbl(format, "duration");
        if (duration <= 0 && video is { } v0) duration = Dbl(v0, "duration");

        var v = video.Value;
        double fps = ParseRate(Str(v, "avg_frame_rate"));
        if (fps <= 0) fps = ParseRate(Str(v, "r_frame_rate"));

        return new VideoMetadata
        {
            Container = Str(format, "format_name"),
            VideoCodec = Str(v, "codec_name"),
            AudioCodec = audio is { } a1 ? Str(a1, "codec_name") : "",
            Width = (int)Lng(v, "width"),
            Height = (int)Lng(v, "height"),
            Fps = Math.Round(fps, 3),
            Duration = Math.Round(duration, 3),
            PixelFormat = Str(v, "pix_fmt"),
            VideoBitrate = Bitrate(v),
            AudioBitrate = audio is { } a2 ? Bitrate(a2) : 0,
            FileSize = Lng(format, "size"),
            HasVideo = true,
            HasAudio = audio is not null,
        };
    }

    private static bool IsAttachedPicture(JsonElement s)
        => s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;

    /// <summary>"30000/1001" → 29.97。"0/0" や不正値は 0。</summary>
    public static double ParseRate(string rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d != 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    /// <summary>stream.bit_rate、無ければ MKV 等の tags.BPS。</summary>
    private static long Bitrate(JsonElement s)
    {
        var b = Lng(s, "bit_rate");
        if (b > 0) return b;
        if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            foreach (var p in tags.EnumerateObject())
                if (p.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                    if (long.TryParse(p.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps)) return bps;
        return 0;
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString()
            : "";

    private static double Dbl(JsonElement e, string name)
        => double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long Lng(JsonElement e, string name)
        => long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
