using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>［字幕に合わせる］で入力欄へ入れる図形の時刻と、表示用の説明。時刻は出力動画の秒。</summary>
public readonly record struct SubtitleAlignResult(double Start, double End, string Description);

/// <summary>
/// 図形の時刻を字幕に合わせる純粋ロジック（v1.21.0。設計 6 章）。
/// 結果は入力欄に入れるだけで、確定は既存の［変更を反映］で行う。
/// </summary>
public static class SubtitleAlign
{
    /// <summary>説明に出す字幕の文字の最大文字数（超えたら … で切る）。</summary>
    public const int DescriptionTextLength = 20;

    /// <summary>
    /// 合わせる字幕を選ぶ。再生位置（出力の秒。削除部分にあるときは null）に出ている字幕を優先し、
    /// 無ければ入力欄の開始（解釈できなければ null → 図形の開始）に開始がいちばん近い字幕。字幕が無ければ null。
    /// 同じ条件の字幕が複数あるときは、開始の早いもの（同じなら一覧の順で先のもの）。
    /// </summary>
    public static SubtitleSegment? Pick(IReadOnlyList<SubtitleSegment> subtitles, double? currentOutputTime, double? draftStart, double shapeStart)
    {
        if (subtitles.Count == 0) return null;
        var ordered = subtitles.OrderBy(s => s.Start).ToList(); // 安定な並べ替え（同じ開始は一覧の順）
        if (currentOutputTime is { } t)
        {
            var shown = ordered.FirstOrDefault(s => s.Start <= t && t < s.End);
            if (shown is not null) return shown;
        }
        double basis = draftStart ?? shapeStart;
        SubtitleSegment? best = null;
        double bestDistance = double.MaxValue;
        foreach (var s in ordered)
        {
            double d = Math.Abs(s.Start - basis);
            if (d < bestDistance - 1e-9)
            {
                best = s;
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>
    /// ［字幕の開始に合わせる］。開始を字幕の開始にし、終了はそのまま。
    /// 開始が終了以後になる場合は長さを保って終了もずらす（編集後の長さ <paramref name="outputDuration"/> を超えない）。
    /// </summary>
    public static SubtitleAlignResult AlignStart(SubtitleSegment subtitle, double start, double end, double outputDuration)
    {
        double newStart = Math.Round(subtitle.Start, 3);
        double newEnd = end;
        if (newStart >= end)
        {
            double length = Math.Max(0, end - start);
            newEnd = newStart + length;
            if (outputDuration > 0) newEnd = Math.Min(newEnd, outputDuration);
        }
        return new SubtitleAlignResult(newStart, Math.Round(newEnd, 3), Describe(subtitle));
    }

    /// <summary>［字幕の時間に合わせる］。開始・終了を字幕の開始・終了にする。</summary>
    public static SubtitleAlignResult AlignRange(SubtitleSegment subtitle)
        => new(Math.Round(subtitle.Start, 3), Math.Round(subtitle.End, 3), Describe(subtitle));

    /// <summary>「字幕「…」に合わせました」。改行は空白にし、長い文字は <see cref="DescriptionTextLength"/> 文字で … を付けて切る。</summary>
    public static string Describe(SubtitleSegment subtitle)
    {
        string text = subtitle.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var info = new System.Globalization.StringInfo(text);
        if (info.LengthInTextElements > DescriptionTextLength)
            text = info.SubstringByTextElements(0, DescriptionTextLength) + "…";
        return $"字幕「{text}」に合わせました";
    }
}
