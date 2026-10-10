using System.Globalization;

namespace ManualVideoStudio.Core.FFmpeg;

/// <summary>ffmpeg -progress pipe:1 の key=value 行から処理済み時間（秒）を得る。</summary>
public static class FFmpegProgressParser
{
    /// <summary>進捗行なら処理済み秒を返す。"progress=end" は double.PositiveInfinity。</summary>
    public static double? ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        switch (key)
        {
            // out_time_ms は歴史的経緯でマイクロ秒。
            case "out_time_us":
            case "out_time_ms":
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0 ? us / 1_000_000.0 : null;
            case "progress":
                return value == "end" ? double.PositiveInfinity : null;
            default:
                return null;
        }
    }

    /// <summary>0～1 に丸めた割合。</summary>
    public static double Ratio(double processedSeconds, double totalSeconds)
    {
        if (double.IsPositiveInfinity(processedSeconds)) return 1;
        if (totalSeconds <= 0) return 0;
        return Math.Clamp(processedSeconds / totalSeconds, 0, 1);
    }
}
