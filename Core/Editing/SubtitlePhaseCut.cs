using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>字幕編集中の追加カットで、字幕・図形のまとまり 1 件がどうなるか（v1.21）。</summary>
public enum PhaseCutOutcome
{
    /// <summary>変わらない（カットより前）。</summary>
    Unchanged,
    /// <summary>長さはそのままで、前へずれる（カットより後）。</summary>
    Shifted,
    /// <summary>一部がカットに重なり、短くなる。</summary>
    Shortened,
    /// <summary>ほぼ全体がカットに入り、最短の長さに満たなくなるため削除する。</summary>
    Removed,
}

/// <summary>字幕・図形のまとまり 1 件の変化（時刻は出力動画基準）。</summary>
public sealed record PhaseCutItem(string Id, PhaseCutOutcome Outcome, double OldStart, double OldEnd, double NewStart, double NewEnd);

/// <summary>
/// 字幕編集中の追加カットの内訳（確認画面と適用で同じものを使う。v1.21）。
/// 時刻: <see cref="CutStart"/>・<see cref="CutEnd"/> は元動画基準（既存のカットとまとめた後の範囲）、
/// <see cref="OutputStart"/>・<see cref="OutputEnd"/> はいまの出力動画基準で、その間の <see cref="RemovedSeconds"/> 秒が消える。
/// </summary>
public sealed record SubtitlePhaseCutPlan(
    bool IsApplicable,
    string Reason,
    double CutStart,
    double CutEnd,
    IReadOnlyList<CutSegment> MergedCuts,
    double OutputStart,
    double OutputEnd,
    double RemovedSeconds,
    IReadOnlyList<PhaseCutItem> Subtitles,
    IReadOnlyList<PhaseCutItem> ShapeGroups,
    IReadOnlyList<string> RemovedSubtitleTexts)
{
    /// <summary>確認画面に出す、削除される字幕の文字の件数（先頭から）。</summary>
    public const int RemovedTextLimit = 5;

    public static SubtitlePhaseCutPlan NotApplicable(string reason, double start = 0, double end = 0)
        => new(false, reason, start, end, [], 0, 0, 0, [], [], []);

    /// <summary>既存のカットとまとめるか（まとめる件数）。</summary>
    public int MergedCount => MergedCuts.Count;

    public int Count(PhaseCutOutcome outcome) => Subtitles.Count(i => i.Outcome == outcome) + ShapeGroups.Count(i => i.Outcome == outcome);
    public int SubtitleCount(PhaseCutOutcome outcome) => Subtitles.Count(i => i.Outcome == outcome);
    public int ShapeGroupCount(PhaseCutOutcome outcome) => ShapeGroups.Count(i => i.Outcome == outcome);
}

/// <summary>
/// 字幕編集中の追加カット（設計 1.21.0 §5.2）の純粋な計算。
/// 元動画の区間 [s, e] のうち、いま残っている部分は、いまの出力時刻では連続した 1 つの区間 [a, b] になる
/// （すでに削除済みの部分は出力上の長さが 0 のため）。字幕・図形の開始・終了を次の f で写す。
/// f(t) = t（t ≤ a）、a（a &lt; t &lt; b）、t − d（t ≥ b）。d = b − a。f は単調なので、字幕どうしの重なりは新しく生じない。
/// </summary>
public static class SubtitlePhaseCut
{
    /// <summary>出力上で消える長さがこれ未満なら「すでにカット済み」とする（秒）。</summary>
    public const double MinRemovedSeconds = 0.05;

    public const string AlreadyCutMessage = "この範囲はすでにカットされています。";
    public const string AllRemovedMessage = "すべての区間が削除されるため、この範囲はカットできません。";
    public const string UnknownDurationMessage = "動画の長さが分からないため、カットを追加できません。";

    /// <summary>
    /// 追加カットの内訳を計算する。<paramref name="start"/>・<paramref name="end"/> は元動画基準。
    /// 既存のカットと重なる（接する）ときは、<see cref="ProjectEditor.AddCut"/> と同じく 1 つのカットにまとめた範囲を使う。
    /// </summary>
    public static SubtitlePhaseCutPlan Plan(IReadOnlyList<CutSegment> cuts, double videoDuration, double start, double end,
        IReadOnlyList<SubtitleSegment> subtitles, IReadOnlyList<ShapeGroup> shapeGroups)
    {
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        if (videoDuration <= 0) return SubtitlePhaseCutPlan.NotApplicable(UnknownDurationMessage, start, end);

        var touching = ProjectEditor.FindTouching(cuts, start, end);
        var (ms, me) = ProjectEditor.Union(start, end, touching);
        var v = CutSegmentValidator.Validate(ms, me, SegmentAction.Remove, 0, videoDuration, cuts.Where(c => !touching.Contains(c)));
        if (!v.IsValid) return SubtitlePhaseCutPlan.NotApplicable(v.Message, start, end);

        // まとめた範囲で写す。既存のカット部分は出力上の長さが 0 なので [s, e] で写した結果と同じになる
        // （古い「短縮」が残っていた場合も、残す部分ごと消えることを正しく数えられる）。
        var keep = EditTimelineCalculator.ComputeKeepRanges(videoDuration, cuts);
        double a = EditTimelineCalculator.MapToOutputClamped(ms, keep);
        double b = EditTimelineCalculator.MapToOutputClamped(me, keep);
        double d = Math.Round(b - a, 3);
        if (d < MinRemovedSeconds - CutSegmentValidator.Epsilon) return SubtitlePhaseCutPlan.NotApplicable(AlreadyCutMessage, start, end);
        if (EditTimelineCalculator.OutputDuration(keep) - d < CutSegmentValidator.MinDuration)
            return SubtitlePhaseCutPlan.NotApplicable(AllRemovedMessage, start, end);

        var subs = subtitles.Select(s => Map(s.Id, s.Start, s.End, a, b, SubtitleValidator.MinDuration)).ToList();
        var shapes = shapeGroups.Select(g => Map(g.Id, g.Start, g.End, a, b, ShapeGroupValidator.MinDuration)).ToList();
        var removedIds = subs.Where(i => i.Outcome == PhaseCutOutcome.Removed).Select(i => i.Id).ToHashSet();
        var removedTexts = subtitles.Where(s => removedIds.Contains(s.Id)).OrderBy(s => s.Start)
            .Take(SubtitlePhaseCutPlan.RemovedTextLimit).Select(s => s.Text.Replace('\n', ' ')).ToList();
        return new SubtitlePhaseCutPlan(true, "", ms, me, touching, a, b, d, subs, shapes, removedTexts);
    }

    /// <summary>出力時刻 t を写す（f）。</summary>
    public static double MapTime(double t, double a, double b)
    {
        const double eps = CutSegmentValidator.Epsilon;
        if (t <= a + eps) return t;
        if (t >= b - eps) return Math.Round(t - (b - a), 3);
        return a;
    }

    private static PhaseCutItem Map(string id, double start, double end, double a, double b, double minDuration)
    {
        const double eps = CutSegmentValidator.Epsilon;
        double ns = MapTime(start, a, b);
        double ne = MapTime(end, a, b);
        PhaseCutOutcome outcome;
        if (ne - ns < minDuration - eps) outcome = PhaseCutOutcome.Removed;
        else if (Math.Abs(ns - start) < eps && Math.Abs(ne - end) < eps) outcome = PhaseCutOutcome.Unchanged;
        else if (ne - ns < end - start - eps) outcome = PhaseCutOutcome.Shortened;
        else outcome = PhaseCutOutcome.Shifted;
        return new PhaseCutItem(id, outcome, start, end, ns, ne);
    }
}
