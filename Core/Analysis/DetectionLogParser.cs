using System.Globalization;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Editing;

namespace ManualVideoStudio.Core.Analysis;

/// <summary>freezedetect / silencedetect / showinfo の stderr ログを区間・時刻へ変換する純粋ロジック。</summary>
public static partial class DetectionLogParser
{
    /// <summary>静止区間。終了が出力されない（動画末尾まで静止）場合は duration を終了とする。</summary>
    public static IReadOnlyList<TimeRange> ParseFreezes(IEnumerable<string> lines, double duration)
        => ParseIntervals(lines, FreezePattern(), duration);

    /// <summary>無音区間。</summary>
    public static IReadOnlyList<TimeRange> ParseSilences(IEnumerable<string> lines, double duration)
        => ParseIntervals(lines, SilencePattern(), duration);

    /// <summary>select+showinfo が出力した画面変化フレームの時刻。</summary>
    public static IReadOnlyList<double> ParseSceneChanges(IEnumerable<string> lines)
    {
        var list = new List<double>();
        foreach (var line in lines)
        {
            if (!line.Contains("showinfo", StringComparison.Ordinal)) continue;
            var m = ShowInfoPattern().Match(line);
            if (m.Success && TryDouble(m.Groups[1].Value, out var t)) list.Add(Math.Round(t, 3));
        }
        return list.Distinct().Order().ToList();
    }

    private static IReadOnlyList<TimeRange> ParseIntervals(IEnumerable<string> lines, Regex pattern, double duration)
    {
        var result = new List<TimeRange>();
        double? start = null;
        foreach (var line in lines)
        {
            var m = pattern.Match(line);
            if (!m.Success || !TryDouble(m.Groups["v"].Value, out var t)) continue;
            if (m.Groups["k"].Value == "start")
            {
                start = Math.Max(0, t);
            }
            else if (start is { } s)
            {
                if (t > s) result.Add(new TimeRange(Math.Round(s, 3), Math.Round(t, 3)));
                start = null;
            }
        }
        if (start is { } open && duration > open) result.Add(new TimeRange(Math.Round(open, 3), Math.Round(duration, 3)));
        return result;
    }

    private static bool TryDouble(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    [GeneratedRegex(@"lavfi\.freezedetect\.freeze_(?<k>start|end):\s*(?<v>-?[\d.]+(?:e[-+]?\d+)?)")]
    private static partial Regex FreezePattern();

    [GeneratedRegex(@"silence_(?<k>start|end):\s*(?<v>-?[\d.]+(?:e[-+]?\d+)?)")]
    private static partial Regex SilencePattern();

    [GeneratedRegex(@"\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:\s*(-?[\d.]+)")]
    private static partial Regex ShowInfoPattern();
}
