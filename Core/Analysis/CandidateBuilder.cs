using System.Globalization;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Analysis;

/// <summary>
/// 静止区間・無音区間からカット候補を作る純粋ロジック。
/// 操作マニュアル動画ではナレーション中の静止画面は必要なことが多いため、
/// 「静止＋無音」を最優先、「静止のみ（音声あり）」は低い優先度、「無音のみ」は設定時のみ候補にする。
/// </summary>
public static class CandidateBuilder
{
    /// <summary>静止区間のうち無音が占める割合がこれ以上なら「静止＋無音」とみなす。</summary>
    public const double SilentCoverageThreshold = 0.8;

    public static List<CutCandidate> Build(IReadOnlyList<TimeRange> freezes, IReadOnlyList<TimeRange> silences,
        double duration, bool hasAudio, AnalysisSettings settings)
    {
        var result = new List<CutCandidate>();
        foreach (var f in freezes)
        {
            double silent = Overlap(f, silences);
            double coverage = f.Duration > 0 ? silent / f.Duration : 0;
            CandidateKind kind;
            double confidence;
            string label;
            if (!hasAudio)
            {
                kind = CandidateKind.Freeze; confidence = 0.75; label = "静止（音声なし動画）";
            }
            else if (!settings.EnableSilence)
            {
                kind = CandidateKind.Freeze; confidence = 0.6; label = "静止（無音判定なし）";
            }
            else if (coverage >= SilentCoverageThreshold)
            {
                kind = CandidateKind.FreezeAndSilence; confidence = 0.9; label = "静止＋無音";
            }
            else
            {
                kind = CandidateKind.Freeze; confidence = 0.4; label = "静止（音声あり・説明中の可能性）";
            }
            AddCandidate(result, f, kind, confidence, label, duration, settings);
        }

        if (settings.IncludeSilenceOnly && hasAudio)
        {
            foreach (var s in silences)
            {
                // 静止区間と大きく重なる無音は上で扱い済み。
                if (Overlap(s, freezes) >= s.Duration * 0.5) continue;
                AddCandidate(result, s, CandidateKind.Silence, 0.3, "無音（画面は変化あり）", duration, settings);
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static void AddCandidate(List<CutCandidate> list, TimeRange range, CandidateKind kind, double confidence,
        string label, double duration, AnalysisSettings settings)
    {
        // 前後に余白を残す（直前の操作結果・直後の操作開始を消さない）。動画の先頭・末尾側は余白不要。
        double start = range.Start <= 0.001 ? 0 : range.Start + settings.EdgeMarginSeconds;
        double end = duration > 0 && range.End >= duration - 0.001 ? duration : range.End - settings.EdgeMarginSeconds;
        // 区間の先頭に、結果を確認できる秒数（設定の「先頭に残す秒数」）を残し、それ以降を削除する候補にする（短縮は廃止。v1.17.0）。
        start = Math.Round(start + settings.ShortenKeepSeconds, 3);
        end = Math.Round(end, 3);
        // 削除しても 0.5 秒未満しか減らない区間は候補にしない。
        if (end - start < 0.5) return;
        if (range.Duration >= 10) confidence = Math.Min(0.95, confidence + 0.05);

        list.Add(new CutCandidate
        {
            Start = start,
            End = end,
            SuggestedAction = SegmentAction.Remove,
            KeepSeconds = 0,
            Confidence = Math.Round(confidence, 2),
            Kind = kind,
            Source = SegmentSource.LocalAnalysis,
            Reason = string.Create(CultureInfo.InvariantCulture, $"{range.Duration:0.0}秒の{label}"),
        });
    }

    /// <summary>range と others の重なりの合計秒。</summary>
    public static double Overlap(TimeRange range, IEnumerable<TimeRange> others)
    {
        double total = 0;
        foreach (var o in others)
        {
            double s = Math.Max(range.Start, o.Start);
            double e = Math.Min(range.End, o.End);
            if (e > s) total += e - s;
        }
        return total;
    }
}
