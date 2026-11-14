using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using Xunit;

namespace ManualVideoStudio.Tests;

/// <summary>字幕編集中の追加カット（v1.21。設計 1.21.0 §5）。</summary>
public class SubtitlePhaseCutTests
{
    /// <summary>60 秒の動画。既存カットを入れてから字幕編集へ進める。</summary>
    private static ProjectEditor Editor(params (double Start, double End)[] cuts)
    {
        var e = new ProjectEditor(TestProjects.Create(60));
        foreach (var (s, en) in cuts) Assert.True(e.AddCut(s, en, SegmentAction.Remove, 0).Result.Success);
        Assert.True(e.ConfirmCuts().Success);
        return e;
    }

    private static SubtitleSegment Sub(ProjectEditor e, double s, double en, string text)
    {
        var (r, seg) = e.AddSubtitle(s, en, text);
        Assert.True(r.Success, r.Message);
        return seg!;
    }

    private static ShapeGroup Shape(ProjectEditor e, double s, double en, bool zoom = false)
    {
        List<Annotation> shapes = [new RectangleAnnotation { X = 10, Y = 20, Width = 100, Height = 50 }];
        if (zoom) shapes.Add(new ZoomAnnotation { X = 100, Y = 100, Width = 960, Height = 540 });
        var (r, g) = e.AddShapeGroup(s, en, shapes, 1920, 1080);
        Assert.True(r.Success, r.Message);
        return g!;
    }

    private static SubtitleSegment SubById(ProjectEditor e, string id) => e.Project.SubtitleSegments.Single(s => s.Id == id);
    private static ShapeGroup ShapeById(ProjectEditor e, string id) => e.Project.ShapeGroups.Single(g => g.Id == id);

    /// <summary>結果の字幕・図形がすべて既存の検証を通る（重なりなし・最短以上・出力の長さ以内）。</summary>
    private static void AssertAllValid(ProjectEditor e)
    {
        var subs = e.Project.SubtitleSegments;
        foreach (var s in subs)
            Assert.True(SubtitleValidator.Validate(s.Start, s.End, s.Text, e.OutputDuration, subs, s.Id).IsValid, $"{s.Start}-{s.End}");
        foreach (var g in e.Project.ShapeGroups)
            Assert.True(ShapeGroupValidator.Validate(g.Start, g.End, e.OutputDuration, g.ImageWidth, g.ImageHeight).IsValid, $"{g.Start}-{g.End}");
    }

    [Fact]
    public void MapTimeFollowsDesign()
    {
        Assert.Equal(5, SubtitlePhaseCut.MapTime(5, 10, 14));
        Assert.Equal(10, SubtitlePhaseCut.MapTime(10, 10, 14));
        Assert.Equal(10, SubtitlePhaseCut.MapTime(12, 10, 14));
        Assert.Equal(10, SubtitlePhaseCut.MapTime(14, 10, 14));
        Assert.Equal(16, SubtitlePhaseCut.MapTime(20, 10, 14));
    }

    [Fact]
    public void SubtitlesBeforeAfterAndAcrossBoundaries()
    {
        var e = Editor();
        var before = Sub(e, 2, 8, "前");
        var acrossStart = Sub(e, 9, 12, "開始をまたぐ");
        var inside = Sub(e, 12.5, 13.5, "中");
        var acrossEnd = Sub(e, 13.8, 16, "終了をまたぐ");
        var after = Sub(e, 20, 25, "後");

        var (r, plan) = e.AddCutInSubtitlePhase(10, 14);
        Assert.True(r.Success, r.Message);
        Assert.Equal(4, plan!.RemovedSeconds, 3);
        Assert.Equal(10, plan.OutputStart);
        Assert.Equal(14, plan.OutputEnd);

        Assert.Equal((2.0, 8.0), (SubById(e, before.Id).Start, SubById(e, before.Id).End));
        Assert.Equal((9.0, 10.0), (SubById(e, acrossStart.Id).Start, SubById(e, acrossStart.Id).End));
        Assert.DoesNotContain(e.Project.SubtitleSegments, s => s.Id == inside.Id);
        Assert.Equal((10.0, 12.0), (SubById(e, acrossEnd.Id).Start, SubById(e, acrossEnd.Id).End));
        Assert.Equal((16.0, 21.0), (SubById(e, after.Id).Start, SubById(e, after.Id).End));

        Assert.Equal(PhaseCutOutcome.Unchanged, plan.Subtitles.Single(i => i.Id == before.Id).Outcome);
        Assert.Equal(PhaseCutOutcome.Shortened, plan.Subtitles.Single(i => i.Id == acrossStart.Id).Outcome);
        Assert.Equal(PhaseCutOutcome.Removed, plan.Subtitles.Single(i => i.Id == inside.Id).Outcome);
        Assert.Equal(PhaseCutOutcome.Shortened, plan.Subtitles.Single(i => i.Id == acrossEnd.Id).Outcome);
        Assert.Equal(PhaseCutOutcome.Shifted, plan.Subtitles.Single(i => i.Id == after.Id).Outcome);
        Assert.Equal(["中"], plan.RemovedSubtitleTexts);

        var cut = Assert.Single(e.Project.CutSegments);
        Assert.Equal((10.0, 14.0), (cut.Start, cut.End));
        Assert.Equal(56, e.OutputDuration, 3);
        AssertAllValid(e);
    }

    [Fact]
    public void ShortenedBelowMinimumIsRemoved()
    {
        var e = Editor();
        var s = Sub(e, 9.9, 12, "ほぼ中");        // 残るのは 0.1 秒（最短 0.2 秒未満）
        var (r, plan) = e.AddCutInSubtitlePhase(10, 14);
        Assert.True(r.Success);
        Assert.Equal(PhaseCutOutcome.Removed, plan!.Subtitles.Single().Outcome);
        Assert.DoesNotContain(e.Project.SubtitleSegments, x => x.Id == s.Id);
    }

    [Fact]
    public void ShapeGroupsAreMappedIncludingZoom()
    {
        var e = Editor();
        var before = Shape(e, 1, 5);
        var acrossStart = Shape(e, 8, 11);
        var inside = Shape(e, 10.5, 13);
        var acrossEnd = Shape(e, 12, 18, zoom: true);
        var after = Shape(e, 30, 33, zoom: true);

        var (r, plan) = e.AddCutInSubtitlePhase(10, 14);
        Assert.True(r.Success, r.Message);

        Assert.Equal((1.0, 5.0), (ShapeById(e, before.Id).Start, ShapeById(e, before.Id).End));
        Assert.Equal((8.0, 10.0), (ShapeById(e, acrossStart.Id).Start, ShapeById(e, acrossStart.Id).End));
        Assert.DoesNotContain(e.Project.ShapeGroups, g => g.Id == inside.Id);
        var z = ShapeById(e, acrossEnd.Id);
        Assert.Equal((10.0, 14.0), (z.Start, z.End));
        Assert.NotNull(z.Zoom);
        var za = ShapeById(e, after.Id);
        Assert.Equal((26.0, 29.0), (za.Start, za.End));
        Assert.NotNull(za.Zoom);

        Assert.Equal(1, plan!.ShapeGroupCount(PhaseCutOutcome.Unchanged));
        Assert.Equal(2, plan.ShapeGroupCount(PhaseCutOutcome.Shortened));
        Assert.Equal(1, plan.ShapeGroupCount(PhaseCutOutcome.Removed));
        Assert.Equal(1, plan.ShapeGroupCount(PhaseCutOutcome.Shifted));
        Assert.Empty(plan.RemovedSubtitleTexts);
        AssertAllValid(e);
    }

    [Fact]
    public void OverlappingExistingCutIsMergedAndAlreadyRemovedPartCountsZero()
    {
        // 既存のカット 20～30（元動画）。出力では 20 秒の位置に継ぎ目がある。
        var e = Editor((20, 30));
        var after = Sub(e, 25, 28, "後");          // 元動画 35～38
        var keep = e.KeepRanges;

        // 元動画 15～25 をカット: 15～20 だけが新しく消え、20～25 は削除済み。
        var plan = e.PlanCutInSubtitlePhase(15, 25);
        Assert.True(plan.IsApplicable, plan.Reason);
        Assert.Equal((15.0, 30.0), (plan.CutStart, plan.CutEnd));
        Assert.Equal(1, plan.MergedCount);
        Assert.Equal(5, plan.RemovedSeconds, 3);
        // [s, e] で写しても、まとめた範囲で写しても同じ a・b。
        Assert.Equal(EditTimelineCalculator.MapToOutputClamped(15, keep), plan.OutputStart);
        Assert.Equal(EditTimelineCalculator.MapToOutputClamped(25, keep), plan.OutputEnd);
        Assert.Equal(EditTimelineCalculator.MapToOutputClamped(30, keep), plan.OutputEnd);

        var before = e.OutputDuration;
        var (r, _) = e.AddCutInSubtitlePhase(15, 25);
        Assert.True(r.Success, r.Message);
        var cut = Assert.Single(e.Project.CutSegments);
        Assert.Equal((15.0, 30.0), (cut.Start, cut.End));
        Assert.Equal(before - 5, e.OutputDuration, 3);
        Assert.Equal((20.0, 23.0), (SubById(e, after.Id).Start, SubById(e, after.Id).End));
        AssertAllValid(e);
    }

    [Fact]
    public void RangeSpanningTwoExistingCutsMergesAll()
    {
        var e = Editor((10, 12), (20, 22));
        var s = Sub(e, 30, 32, "後");                // 元動画 34～36
        var (r, plan) = e.AddCutInSubtitlePhase(11, 21);
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, plan!.MergedCount);
        Assert.Equal(8, plan.RemovedSeconds, 3);     // 新しく消えるのは 12～20 の 8 秒
        var cut = Assert.Single(e.Project.CutSegments);
        Assert.Equal((10.0, 22.0), (cut.Start, cut.End));
        Assert.Equal((22.0, 24.0), (SubById(e, s.Id).Start, SubById(e, s.Id).End));
        Assert.Equal(48, e.OutputDuration, 3);
    }

    [Fact]
    public void RangeEntirelyAlreadyCutIsNotApplicable()
    {
        var e = Editor((20, 30));
        Sub(e, 18, 22, "字幕");
        var plan = e.PlanCutInSubtitlePhase(22, 28);
        Assert.False(plan.IsApplicable);
        Assert.Equal(SubtitlePhaseCut.AlreadyCutMessage, plan.Reason);

        var (r, _) = e.AddCutInSubtitlePhase(22, 28);
        Assert.False(r.Success);
        Assert.Equal(SubtitlePhaseCut.AlreadyCutMessage, r.Message);
        Assert.Equal((20.0, 30.0), (e.Project.CutSegments.Single().Start, e.Project.CutSegments.Single().End));
        Assert.Equal((18.0, 22.0), (e.Project.SubtitleSegments.Single().Start, e.Project.SubtitleSegments.Single().End));
    }

    [Fact]
    public void InvalidRangeFails()
    {
        var e = Editor();
        Assert.False(e.AddCutInSubtitlePhase(10, 10.01).Result.Success);
        Assert.False(e.AddCutInSubtitlePhase(50, 61).Result.Success);
        Assert.False(e.AddCutInSubtitlePhase(0, 60).Result.Success);   // すべて消える
        Assert.Empty(e.Project.CutSegments);
    }

    [Fact]
    public void NotInSubtitlePhaseFails()
    {
        var e = new ProjectEditor(TestProjects.Create(60));
        var (r, plan) = e.AddCutInSubtitlePhase(10, 14);
        Assert.False(r.Success);
        Assert.Null(plan);
        Assert.False(e.PlanCutInSubtitlePhase(10, 14).IsApplicable);
        Assert.Empty(e.Project.CutSegments);
        Assert.False(e.History.CanUndo);
    }

    [Fact]
    public void OneUndoRestoresCutsSubtitlesAndShapes()
    {
        var e = Editor((40, 45));
        Sub(e, 2, 8, "前");
        Sub(e, 12, 13, "中");
        Sub(e, 20, 25, "後");
        Shape(e, 11, 13, zoom: true);
        Shape(e, 30, 33);
        string Snapshot() => System.Text.Json.JsonSerializer.Serialize(
            new { e.Project.CutSegments, e.Project.SubtitleSegments, e.Project.ShapeGroups }, ManualVideoStudio.Core.Persistence.JsonDefaults.Options);
        var before = Snapshot();

        Assert.True(e.AddCutInSubtitlePhase(10, 14).Result.Success);
        Assert.NotEqual(before, Snapshot());
        e.Undo();
        Assert.Equal(before, Snapshot());
        Assert.Equal(EditPhase.Subtitle, e.Project.EditPhase);
        e.Redo();
        Assert.Equal(2, e.Project.CutSegments.Count);
        Assert.Equal(2, e.Project.SubtitleSegments.Count);
        Assert.Single(e.Project.ShapeGroups);
    }

    [Fact]
    public void CapturePointsAndTitleAreNotChanged()
    {
        var e = Editor();
        Assert.True(e.AddCapture(30).Result.Success);
        Assert.True(e.SetTitle("タイトル", 3, false).Success);
        Assert.True(e.AddCutInSubtitlePhase(10, 14).Result.Success);
        Assert.Equal(30, e.Project.CapturePoints.Single().SelectedFrameTime);
        Assert.Equal("タイトル", e.Project.Title.Text);
    }

    [Fact]
    public void ManyCutsKeepSubtitlesValidAndPlanMatchesApply()
    {
        var e = Editor((5, 6), (33, 35));
        // 0.5 秒刻みで重ならない字幕・図形を並べる（出力 57 秒）。
        for (double t = 0; t + 0.4 <= 56; t += 0.5) Sub(e, t, t + 0.4, $"字幕{t}");
        for (double t = 0; t + 1 <= 56; t += 3) Shape(e, t, t + 1);

        foreach (var (s, en) in new[] { (2.1, 3.37), (5.5, 9.13), (17.21, 17.3), (34, 40.05), (50.2, 52.9) })
        {
            var plan = e.PlanCutInSubtitlePhase(s, en);
            Assert.True(plan.IsApplicable, plan.Reason);
            var outputBefore = e.OutputDuration;
            var (r, applied) = e.AddCutInSubtitlePhase(s, en);
            Assert.True(r.Success, r.Message);
            Assert.Equal(plan, applied!, new PlanComparer());
            Assert.Equal(outputBefore - plan.RemovedSeconds, e.OutputDuration, 3);

            // 計画どおりの時刻・件数になっている。
            var kept = plan.Subtitles.Where(i => i.Outcome != PhaseCutOutcome.Removed).ToList();
            Assert.Equal(kept.Count, e.Project.SubtitleSegments.Count);
            foreach (var i in kept) Assert.Equal((i.NewStart, i.NewEnd), (SubById(e, i.Id).Start, SubById(e, i.Id).End));
            var keptShapes = plan.ShapeGroups.Where(i => i.Outcome != PhaseCutOutcome.Removed).ToList();
            Assert.Equal(keptShapes.Count, e.Project.ShapeGroups.Count);
            foreach (var i in keptShapes) Assert.Equal((i.NewStart, i.NewEnd), (ShapeById(e, i.Id).Start, ShapeById(e, i.Id).End));
            AssertAllValid(e);
            Assert.True(e.Project.SubtitleSegments.Zip(e.Project.SubtitleSegments.Skip(1)).All(p => p.First.Start <= p.Second.Start));
        }
        foreach (var c in e.Project.CutSegments)
            Assert.True(CutSegmentValidator.Validate(c, e.VideoDuration, e.Project.CutSegments).IsValid);
    }

    /// <summary>計画の内容（一覧を含む）が同じか。</summary>
    private sealed class PlanComparer : IEqualityComparer<SubtitlePhaseCutPlan>
    {
        public bool Equals(SubtitlePhaseCutPlan? x, SubtitlePhaseCutPlan? y)
            => x is not null && y is not null && x.IsApplicable == y.IsApplicable && x.Reason == y.Reason
               && x.CutStart == y.CutStart && x.CutEnd == y.CutEnd && x.RemovedSeconds == y.RemovedSeconds
               && x.OutputStart == y.OutputStart && x.OutputEnd == y.OutputEnd
               && x.MergedCuts.Select(c => c.Id).SequenceEqual(y.MergedCuts.Select(c => c.Id))
               && x.Subtitles.SequenceEqual(y.Subtitles) && x.ShapeGroups.SequenceEqual(y.ShapeGroups)
               && x.RemovedSubtitleTexts.SequenceEqual(y.RemovedSubtitleTexts);

        public int GetHashCode(SubtitlePhaseCutPlan obj) => 0;
    }
}
