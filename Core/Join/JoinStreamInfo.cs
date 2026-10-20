using System.Globalization;
using System.Text.Json;
using ManualVideoStudio.Core.FFmpeg;

namespace ManualVideoStudio.Core.Join;

/// <summary>連結の判定に使う映像ストリームの条件（ffprobe の値そのまま）。</summary>
public sealed record JoinVideoStream(string Codec, string Profile, int Width, int Height, string PixelFormat, string FrameRate, string TimeBase)
{
    /// <summary>公称のフレームレート（r_frame_rate）。不明は 0。</summary>
    public double Fps => FfprobeJsonParser.ParseRate(FrameRate);
}

/// <summary>連結の判定に使う音声ストリームの条件。</summary>
public sealed record JoinAudioStream(string Codec, int SampleRate, int Channels, string ChannelLayout);

/// <summary>1 本の動画の連結用の条件（長さ・サイズ・FPS・コーデックなど）。ビットレートは含めない。</summary>
public sealed record JoinStreamInfo(JoinVideoStream Video, JoinAudioStream? Audio, double Duration, long FileSize);

/// <summary>ffprobe の JSON から <see cref="JoinStreamInfo"/> を作る。既存の VideoMetadata は FPS を丸めているため、連結の判定には使わない。</summary>
public static class JoinStreamParser
{
    public static JoinStreamInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
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
        if (video is not { } v) throw new InvalidOperationException("映像ストリームがありません。");
        var format = root.TryGetProperty("format", out var f) ? f : default;
        double duration = Dbl(format, "duration");
        if (duration <= 0) duration = Dbl(v, "duration");
        return new JoinStreamInfo(
            new JoinVideoStream(Str(v, "codec_name"), Str(v, "profile"), (int)Lng(v, "width"), (int)Lng(v, "height"),
                Str(v, "pix_fmt"), Str(v, "r_frame_rate"), Str(v, "time_base")),
            audio is { } a ? new JoinAudioStream(Str(a, "codec_name"), (int)Lng(a, "sample_rate"), (int)Lng(a, "channels"), Str(a, "channel_layout")) : null,
            duration, Lng(format, "size"));
    }

    private static bool IsAttachedPicture(JsonElement s)
        => s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString()
            : "";

    private static double Dbl(JsonElement e, string name)
        => double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long Lng(JsonElement e, string name)
        => long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>ffprobe で <see cref="JoinStreamInfo"/> を取得する。</summary>
public static class JoinProbe
{
    public static async Task<JoinStreamInfo> ProbeAsync(IProcessRunner runner, string ffprobePath, string videoPath, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new ProcessRequest(ffprobePath, MediaMetadataService.BuildArguments(videoPath)) { Timeout = TimeSpan.FromSeconds(60) }, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new JoinException($"動画の情報を取得できませんでした: {Path.GetFileName(videoPath)}", result.ErrorTail(3));
        try
        {
            return JoinStreamParser.Parse(result.StandardOutput);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new JoinException($"動画の情報を読めませんでした: {Path.GetFileName(videoPath)}", ex.Message);
        }
    }
}

/// <summary>結合・追加録画の条件確認で、利用者へ理由を示すための例外（Problems に不一致の項目を入れる）。</summary>
public sealed class JoinException(string message, string? detail = null, IReadOnlyList<string>? problems = null) : Exception(message)
{
    public string? Detail { get; } = detail;
    public IReadOnlyList<string> Problems { get; } = problems ?? [];
}
