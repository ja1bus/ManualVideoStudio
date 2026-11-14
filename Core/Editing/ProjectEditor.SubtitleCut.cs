using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>
/// 字幕編集中の追加カット（v1.21。設計 1.21.0 §5）。字幕編集中に変えられるカットは、これによる追加だけ
/// （既存のカットと重なる・接する場合は 1 つにまとめる＝広げられる）。取り消し・縮小はできない。
/// </summary>
public sealed partial class ProjectEditor
{
    /// <summary>字幕編集中でないときの説明。</summary>
    public const string SubtitlePhaseCutOnlyMessage = "この操作は字幕編集中だけ使えます。カット編集中は通常のカットを使ってください。";

    /// <summary>
    /// 字幕編集中に元動画の区間 [start, end] をカットしたときの内訳（確認画面用）。
    /// <see cref="AddCutInSubtitlePhase"/> と同じ計算を使うので、表示と結果は食い違わない。
    /// </summary>
    public SubtitlePhaseCutPlan PlanCutInSubtitlePhase(double start, double end)
    {
        if (!IsCutLocked) return SubtitlePhaseCutPlan.NotApplicable(SubtitlePhaseCutOnlyMessage, Math.Round(start, 3), Math.Round(end, 3));
        return SubtitlePhaseCut.Plan(Project.CutSegments, VideoDuration, start, end, Project.SubtitleSegments, Project.ShapeGroups);
    }

    /// <summary>
    /// 字幕編集中にカットを追加し、字幕・図形のまとまりの時刻を合わせて直す（1 回の元に戻す操作で、カットも字幕・図形も戻る）。
    /// カットの中にほぼ全体が入る字幕・図形は削除し、一部だけ重なるものは短くする。拡大は図形のまとまりの中にあるので一緒に動く。
    /// 画像ポイント・タイトル・クリックの強調は元動画基準・先頭固定なので変えない。
    /// </summary>
    public (EditResult Result, SubtitlePhaseCutPlan? Plan) AddCutInSubtitlePhase(double start, double end)
    {
        if (!IsCutLocked) return (EditResult.Fail(SubtitlePhaseCutOnlyMessage), null);
        var plan = PlanCutInSubtitlePhase(start, end);
        if (!plan.IsApplicable) return (EditResult.Fail(plan.Reason), plan);

        var subtitleMap = plan.Subtitles.ToDictionary(i => i.Id);
        var shapeMap = plan.ShapeGroups.ToDictionary(i => i.Id);
        Apply("字幕編集中のカットの追加", () =>
        {
            if (plan.MergedCuts.Count > 0)
            {
                // まとめる対象は計画時のカット（Id で現在の一覧から取り直す）。
                var ids = plan.MergedCuts.Select(c => c.Id).ToHashSet();
                var over = Project.CutSegments.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Start).ToList();
                ApplyMerge(plan.CutStart, plan.CutEnd, over, null, SegmentSource.Manual, "", null);
            }
            else
            {
                Project.CutSegments.Add(new CutSegment { Start = plan.CutStart, End = plan.CutEnd, Action = SegmentAction.Remove, Source = SegmentSource.Manual });
                SortCuts();
            }

            Project.SubtitleSegments.RemoveAll(s => subtitleMap.TryGetValue(s.Id, out var i) && i.Outcome == PhaseCutOutcome.Removed);
            foreach (var s in Project.SubtitleSegments)
            {
                if (!subtitleMap.TryGetValue(s.Id, out var i)) continue;
                s.Start = i.NewStart;
                s.End = i.NewEnd;
            }
            SortSubtitles();

            Project.ShapeGroups.RemoveAll(g => shapeMap.TryGetValue(g.Id, out var i) && i.Outcome == PhaseCutOutcome.Removed);
            foreach (var g in Project.ShapeGroups)
            {
                if (!shapeMap.TryGetValue(g.Id, out var i)) continue;
                g.Start = i.NewStart;
                g.End = i.NewEnd;
            }
            SortShapeGroups();
        });
        return (EditResult.Ok(SubtitlePhaseCutMessage(plan)), plan);
    }

    private static string SubtitlePhaseCutMessage(SubtitlePhaseCutPlan plan)
    {
        var parts = new List<string> { $"カットを追加し、動画を {plan.RemovedSeconds:0.0##} 秒短くしました" };
        int shifted = plan.Count(PhaseCutOutcome.Shifted), shortened = plan.Count(PhaseCutOutcome.Shortened), removed = plan.Count(PhaseCutOutcome.Removed);
        if (shifted > 0) parts.Add($"前へずらした字幕・図形 {shifted} 件");
        if (shortened > 0) parts.Add($"短くした字幕・図形 {shortened} 件");
        if (removed > 0) parts.Add($"削除した字幕・図形 {removed} 件");
        var text = string.Join("。", parts) + "。";
        return plan.MergedCount > 0 ? text + MergeMessage(plan.MergedCount, plan.CutStart, plan.CutEnd) : text;
    }
}
