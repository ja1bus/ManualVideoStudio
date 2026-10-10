using System.Globalization;
using System.Text.RegularExpressions;

namespace ManualVideoStudio.Core.Models;

/// <summary>時刻の文字列表現（HH:MM:SS.mmm）と秒の相互変換。</summary>
public static partial class TimeFormat
{
    /// <summary>秒を "HH:MM:SS.mmm" にする。負値は 0 とみなす。</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        long totalMs = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        long h = totalMs / 3_600_000;
        long m = totalMs / 60_000 % 60;
        long s = totalMs / 1000 % 60;
        long ms = totalMs % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}.{ms:000}");
    }

    /// <summary>表示用の短い形式。1時間未満は "MM:SS.mmm"。</summary>
    public static string FormatShort(double seconds)
    {
        var full = Format(seconds);
        return full.StartsWith("00:", StringComparison.Ordinal) ? full[3..] : full;
    }

    /// <summary>秒を ffmpeg 引数用の小数表記にする（カルチャ非依存、ミリ秒精度）。</summary>
    public static string ToFFmpegSeconds(double seconds)
        => Math.Round(Math.Max(0, seconds), 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// "HH:MM:SS.mmm" / "MM:SS.mmm" / "SS.mmm" / 数値秒 を解析する。
    /// 小数点はピリオドのみ（カンマは SRT 形式として受け付ける）。
    /// </summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.');
        var match = TimePattern().Match(t);
        if (!match.Success) return false;

        double h = 0, m = 0;
        string sText = match.Groups["s"].Value;
        if (match.Groups["h"].Success) h = double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["m"].Success) m = double.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (!double.TryParse(sText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)) return false;

        // 分・秒の桁あふれ（例: 00:75:00）は受け付けない。ただし単独の秒指定（"90.5"）は可。
        if (match.Groups["m"].Success && s >= 60) return false;
        if (match.Groups["h"].Success && m >= 60) return false;

        seconds = Math.Round(h * 3600 + m * 60 + s, 3);
        return true;
    }

    public static double Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"時刻として解析できません: {text}");

    [GeneratedRegex(@"^(?:(?:(?<h>\d+):)?(?<m>\d+):)?(?<s>\d+(?:\.\d+)?)$")]
    private static partial Regex TimePattern();
}
