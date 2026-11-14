using System.Globalization;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Analysis;

/// <summary>
/// 静止区間・無音区間からカット候補を作る純粋ロジック。
/// 操作マニュアル動画ではナレーション中の静止画面は必要なことが多いため、
/// 「静止＋無音」を最優先、「静止のみ（音声あり）」は低い優先度、「無音のみ」は設定時のみ候補にする。
/// 録画時の操作記録があれば、「静止＋入力なし」（0.90）と「入力なし（画面は変化あり）」（0.55）も作る（v1.21.0）。
/// </summary>
public static class CandidateBuilder
{
    /// <summary>静止区間のうち無音が占める割合がこれ以上なら「静止＋無音」とみなす。</summary>
    public const double SilentCoverageThreshold = 0.8;
    /// <summary>静止区間のうち入力なしが占める割合がこれ以上なら「静止＋入力なし」とみなす（v1.21.0）。</summary>
    public const double IdleCoverageThreshold = 0.8;
    /// <summary>入力なしの候補の末尾に残す最短の秒数（処理の結果が出た画面を消さない。v1.21.0）。</summary>
    public const double IdleEndMarginSeconds = 1.0;

    /// <summary>
    /// 候補を作る。<paramref name="inputIdle"/> は録画時の操作記録から求めた「入力の無い範囲」（v1.21.0）。
    /// null・空なら従来と同じ結果になる。
    /// </summary>
    public static List<CutCandidate> Build(IReadOnlyList<TimeRange> freezes, IReadOnlyList<TimeRange> silences,
        double duration, bool hasAudio, AnalysisSettings settings, IReadOnlyList<TimeRange>? inputIdle = null)
    {
        var result = new List<CutCandidate>();
        bool hasIdle = inputIdle is { Count: > 0 };
        foreach (var f in freezes)
        {
            double silent = Overlap(f, silences);
            double coverage = f.Duration > 0 ? silent / f.Duration : 0;
            double idleCoverage = hasIdle && f.Duration > 0 ? Overlap(f, inputIdle!) / f.Duration : 0;
            CandidateKind kind;
            double confidence;
            string label;
            if (hasIdle && idleCoverage >= IdleCoverageThreshold)
            {
                // 静止して入力も無い＝待っているだけの区間。無音も重なれば理由にだけ書き、信頼度は同じ 0.90。
                bool alsoSilent = hasAudio && settings.EnableSilence && coverage >= SilentCoverageThreshold;
                kind = CandidateKind.IdleAndFreeze; confidence = 0.9;
                label = alsoSilent ? "静止＋無音＋入力なし" : "静止＋入力なし";
            }
            else if (!hasAudio)
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
        if (hasIdle)
        {
            // 入力なしで画面は動いている区間（処理待ちなど）。静止と半分以上重なるものは上で扱い済み。
            // 末尾は処理の結果が出た画面を消さないよう、余白と 1 秒の大きい方を残す。
            double endMargin = Math.Max(settings.EdgeMarginSeconds, IdleEndMarginSeconds);
            foreach (var idle in inputIdle!)
            {
                if (idle.Duration < settings.FreezeMinSeconds) continue;
                if (Overlap(idle, freezes) >= idle.Duration * 0.5) continue;
                AddCandidate(result, idle, CandidateKind.Idle, 0.55, "入力なし（画面は変化あり）", duration, settings, endMargin);
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static void AddCandidate(List<CutCandidate> list, TimeRange range, CandidateKind kind, double confidence,
        string label, double duration, AnalysisSettings settings, double? endMargin = null)
    {
        // 前後に余白を残す（直前の操作結果・直後の操作開始を消さない）。動画の先頭・末尾側は余白不要。
        double start = range.Start <= 0.001 ? 0 : range.Start + settings.EdgeMarginSeconds;
        double end = duration > 0 && range.End >= duration - 0.001 ? duration : range.End - (endMargin ?? settings.EdgeMarginSeconds);
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
