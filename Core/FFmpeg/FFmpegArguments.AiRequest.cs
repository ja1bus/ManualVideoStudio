using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.FFmpeg;

/// <summary>
/// 不要区間の解析の依頼用動画の大きさ（v1.21。設計 4.2）。
/// VideoWidth / VideoHeight は縮小・偶数化した後の映像、BandHeight は下に足す黒い帯、Height は帯を足した後の高さ。
/// </summary>
public sealed record AiRequestVideoLayout(int VideoWidth, int VideoHeight, int BandHeight, bool Scaled)
{
    public int Width => VideoWidth;
    public int Height => VideoHeight + BandHeight;
}

public static partial class FFmpegArguments
{
    /// <summary>依頼用動画の幅の上限（これを超える動画だけ縮小する）。</summary>
    public const int AiRequestMaxWidth = 1920;
    /// <summary>依頼用動画のフレームレート。</summary>
    public const int AiRequestFps = 10;
    /// <summary>帯の高さの最小（px）。</summary>
    public const int AiRequestMinBand = 40;
    /// <summary>帯の高さ（映像の高さに対する割合）。</summary>
    public const double AiRequestBandRatio = 0.06;

    /// <summary>
    /// 依頼用動画の大きさ。幅が 1920 を超えるときだけ幅 1920 へ縮小（高さは比を保って偶数に丸める）。
    /// それ以外は解像度を変えず、奇数なら右端・下端 1px を切り落とす（<see cref="EvenCrop"/>）。
    /// 帯の高さは映像の高さの 6% を偶数に丸めたもの（最小 40px）。
    /// </summary>
    public static AiRequestVideoLayout AiRequestVideoSize(int width, int height)
    {
        int w, h;
        bool scaled = width > AiRequestMaxWidth;
        if (scaled)
        {
            w = AiRequestMaxWidth;
            h = Math.Max(2, (int)Math.Round(height * (double)AiRequestMaxWidth / width / 2.0, MidpointRounding.AwayFromZero) * 2);
        }
        else
        {
            w = width & ~1;
            h = height & ~1;
        }
        var band = Math.Max(AiRequestMinBand, (int)Math.Round(h * AiRequestBandRatio / 2.0, MidpointRounding.AwayFromZero) * 2);
        return new AiRequestVideoLayout(w, h, band, scaled);
    }

    /// <summary>
    /// 不要区間の解析の依頼用動画（v1.21。設計 4.2）。元動画の時間軸のまま（カットを適用しない）、
    /// 縮小（幅 1920 超だけ）→ 10 fps → 下に黒い帯（pad）→ 帯へ区間番号と時刻の ASS を焼き込む。
    /// libx264 CRF 28 veryfast yuv420p、音声があれば AAC 96k。進捗は -progress pipe:1。
    /// subtitleFile は ffmpeg の作業フォルダーからの相対名（<see cref="SubtitlesFilter"/>）。
    /// </summary>
    public static IReadOnlyList<string> AiRequestVideo(string input, VideoMetadata meta, string subtitleFile, string output)
    {
        var layout = AiRequestVideoSize(meta.Width, meta.Height);
        var filters = new List<string>();
        if (layout.Scaled) filters.Add($"scale={I(layout.VideoWidth)}:{I(layout.VideoHeight)}:flags=lanczos");
        else if (meta.Width % 2 != 0 || meta.Height % 2 != 0) filters.Add(EvenCrop);
        filters.Add($"fps={I(AiRequestFps)}");
        filters.Add($"pad={I(layout.Width)}:{I(layout.Height)}:0:0:black");
        filters.Add(SubtitlesFilter(subtitleFile));

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input, "-map", "0:v:0" };
        if (meta.HasAudio) args.AddRange(["-map", "0:a:0"]);
        args.AddRange(["-vf", string.Join(',', filters)]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p"]);
        if (meta.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "96k"]);
        else args.Add("-an");
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        var ext = Path.GetExtension(output).ToLowerInvariant();
        if (ext is ".mp4" or ".m4v" or ".mov") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", "-stats_period", "0.5"]);
        args.Add(output);
        return args;
    }
}
