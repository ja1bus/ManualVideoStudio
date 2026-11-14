using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 不要区間の解析の依頼用動画（v1.21。設計 4.2）で、映像の下に足した黒い帯へ「区間 12　00:03:25」を出す ASS を作る。
/// 1 秒に 1 イベント（時刻は元動画の時刻。区間はその秒の始まりの時刻を含む区間）。帯は FFmpeg の pad で足し、
/// PlayResX / PlayResY は帯を足した後の大きさにする。フォントは Meiryo、白、左寄せで帯の上下中央。
/// </summary>
public static class TimecodeAssBuilder
{
    public const string FontName = "Meiryo";
    /// <summary>帯の高さに対する文字の大きさの割合。</summary>
    public const double FontRatio = 0.6;

    /// <param name="duration">元動画の長さ（秒）。</param>
    /// <param name="segments">区間表（空なら時刻だけを出す）。</param>
    /// <param name="width">帯を足した後の幅（＝映像の幅）。</param>
    /// <param name="height">帯を足した後の高さ（映像の高さ＋帯）。</param>
    /// <param name="bandHeight">帯の高さ（下端から）。</param>
    public static string Build(double duration, IReadOnlyList<AiSegment> segments, int width, int height, int bandHeight)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n");
        sb.Append("ScriptType: v4.00+\n");
        sb.Append(Inv($"PlayResX: {width}\n"));
        sb.Append(Inv($"PlayResY: {height}\n"));
        sb.Append("WrapStyle: 2\n");
        sb.Append("ScaledBorderAndShadow: yes\n");
        sb.Append("YCbCr Matrix: None\n\n");
        sb.Append("[V4+ Styles]\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        var fontSize = Math.Max(10, (int)Math.Round(bandHeight * FontRatio));
        // 白文字・縁なし・左寄せ中段（an4）。位置は各イベントの \pos で帯の上下中央に置く。
        sb.Append(Inv($"Style: Band,{FontName},{fontSize},&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,4,0,0,0,1\n\n"));
        sb.Append("[Events]\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        var x = Math.Max(8, bandHeight / 3);
        var y = height - bandHeight / 2.0;
        var pos = Inv($"{{\\an4\\pos({x},{Math.Round(y, 1).ToString("0.#", CultureInfo.InvariantCulture)})}}");
        int count = EventCount(duration);
        var ordered = segments.OrderBy(s => s.Start).ToList();
        for (int i = 0; i < count; i++)
        {
            double start = i;
            double end = Math.Min(i + 1, duration);
            var text = Text(i, ordered);
            sb.Append(Inv($"Dialogue: 0,{AssSubtitleBuilder.Time(start)},{AssSubtitleBuilder.Time(end)},Band,,0,0,0,,{pos}{text}\n"));
        }
        return sb.ToString();
    }

    /// <summary>イベントの数（1 秒に 1 つ。端数の秒も 1 つ）。</summary>
    public static int EventCount(double duration)
        => duration > 0 && !double.IsInfinity(duration) ? (int)Math.Ceiling(duration - 1e-9) : 0;

    /// <summary>その秒に出す文字（「区間 12　00:03:25」。区間が無ければ時刻だけ）。</summary>
    public static string Text(int second, IReadOnlyList<AiSegment> orderedSegments)
    {
        var clock = SegmentTable.Clock(second);
        var seg = orderedSegments.LastOrDefault(s => s.Start <= second + 1e-6 && second < s.End)
            ?? orderedSegments.LastOrDefault(s => s.Start <= second + 1e-6);
        return seg is null ? clock : Inv($"区間 {seg.Id}　{clock}");
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
