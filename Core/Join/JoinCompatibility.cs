using System.Globalization;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Join;

/// <summary>追加録画で録画画面に固定する条件（元動画と同じ幅・高さ・FPS、音声の有無）。</summary>
public sealed record AppendRequirement(int Width, int Height, int Fps, bool HasAudio, string Description);

/// <summary>
/// 元動画と別の動画を再エンコードせずに連結できるかの判定（解像度などの自動変換はしない）。
/// FFmpeg の concat demuxer + -c copy は、映像・音声のコーデックとパラメーターが一致している必要がある。
/// ビットレートと長さの一致は求めない。
/// </summary>
public static class JoinCompatibility
{
    private const double FpsTolerance = 0.001;

    /// <summary>reference と other の違う項目を返す（無ければ空）。</summary>
    public static IReadOnlyList<string> Compare(JoinStreamInfo reference, JoinStreamInfo other)
    {
        var list = new List<string>();
        var a = reference.Video;
        var b = other.Video;
        if (!string.Equals(a.Codec, b.Codec, StringComparison.OrdinalIgnoreCase)) list.Add($"映像のコーデック（{a.Codec} / {b.Codec}）");
        if (!string.Equals(a.Profile, b.Profile, StringComparison.OrdinalIgnoreCase)) list.Add($"映像のプロファイル（{a.Profile} / {b.Profile}）");
        if (a.Width != b.Width || a.Height != b.Height) list.Add($"画面サイズ（{a.Width}x{a.Height} / {b.Width}x{b.Height}）");
        if (!string.Equals(a.PixelFormat, b.PixelFormat, StringComparison.OrdinalIgnoreCase)) list.Add($"画素形式（{a.PixelFormat} / {b.PixelFormat}）");
        if (Math.Abs(a.Fps - b.Fps) > FpsTolerance) list.Add($"FPS（{Fmt(a.Fps)} / {Fmt(b.Fps)}）");
        else if (a.TimeBase != b.TimeBase) list.Add($"映像の時間基準（{a.TimeBase} / {b.TimeBase}）");

        if (reference.Audio is null != other.Audio is null)
        {
            list.Add($"音声の有無（{(reference.Audio is null ? "なし" : "あり")} / {(other.Audio is null ? "なし" : "あり")}）");
        }
        else if (reference.Audio is { } x && other.Audio is { } y)
        {
            if (!string.Equals(x.Codec, y.Codec, StringComparison.OrdinalIgnoreCase)) list.Add($"音声のコーデック（{x.Codec} / {y.Codec}）");
            if (x.SampleRate != y.SampleRate) list.Add($"音声のサンプルレート（{x.SampleRate} / {y.SampleRate}）");
            if (x.Channels != y.Channels || !string.Equals(x.ChannelLayout, y.ChannelLayout, StringComparison.OrdinalIgnoreCase))
                list.Add($"音声のチャンネル構成（{x.ChannelLayout} / {y.ChannelLayout}）");
        }
        return list;
    }

    /// <summary>
    /// 元動画に追加録画をつなげられるか（本アプリの録画の出力に合うか）。つなげられるなら条件、そうでなければ理由を返す。
    /// 本アプリの録画は H.264（yuv420p）、15 / 30 fps、音声は AAC 48 kHz ステレオ。ここに合わない動画には、録画後に連結できない。
    /// </summary>
    public static AppendRequirement? CheckAppendable(JoinStreamInfo source, out IReadOnlyList<string> problems)
    {
        var list = new List<string>();
        var v = source.Video;
        if (!string.Equals(v.Codec, "h264", StringComparison.OrdinalIgnoreCase)) list.Add($"映像のコーデックが H.264 ではありません（{v.Codec}）。");
        if (!string.Equals(v.PixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase)) list.Add($"画素形式が yuv420p ではありません（{v.PixelFormat}）。");
        int fps = RecordingSettings.FpsPresets.FirstOrDefault(p => Math.Abs(p - v.Fps) <= FpsTolerance);
        if (fps == 0) list.Add($"FPS が録画画面で選べる値（{string.Join(" / ", RecordingSettings.FpsPresets)}）ではありません（{Fmt(v.Fps)}）。");
        if (v.Width < 16 || v.Height < 16 || v.Width % 2 != 0 || v.Height % 2 != 0) list.Add($"画面サイズ（{v.Width}x{v.Height}）を範囲指定の録画で再現できません。");
        if (source.Audio is { } a)
        {
            if (!string.Equals(a.Codec, "aac", StringComparison.OrdinalIgnoreCase) || a.SampleRate != 48000 || a.Channels != 2)
                list.Add($"音声が本アプリの録画の形式（AAC・48 kHz・ステレオ）ではありません（{a.Codec}・{a.SampleRate} Hz・{a.Channels} ch）。");
        }
        problems = list;
        if (list.Count > 0) return null;
        var audio = source.Audio is null ? "音声なし" : "音声あり（マイク）";
        return new AppendRequirement(v.Width, v.Height, fps, source.Audio is not null, $"{v.Width}x{v.Height} / {fps} fps / {audio}");
    }

    private static string Fmt(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);
}
