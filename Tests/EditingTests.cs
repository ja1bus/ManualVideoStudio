using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

internal static class TestProjects
{
    public static ProjectDocument Create(double duration = 60, bool hasAudio = true) => new()
    {
        SourceVideo = new SourceVideo { Path = @"C:\videos\manual.mp4" },
        VideoMetadata = new VideoMetadata { Duration = duration, Width = 1920, Height = 1080, Fps = 30, HasAudio = hasAudio, HasVideo = true },
    };
}

public class CutSegmentValidatorTests
{
    private static ValidationResult V(double s, double e, SegmentAction a = SegmentAction.Remove, double keep = 0,
        double duration = 60, params CutSegment[] existing)
        => CutSegmentValidator.Validate(s, e, a, keep, duration, existing);

    [Fact] public void ValidRemove() => Assert.True(V(1, 5).IsValid);
    [Fact] public void ValidShorten() => Assert.True(V(1, 5, SegmentAction.Shorten, 1.5).IsValid);
    [Fact] public void EndBeforeStart() => Assert.False(V(5, 1).IsValid);
    [Fact] public void ZeroLength() => Assert.False(V(5, 5).IsValid);
    [Fact] public void TooShort() => Assert.False(V(5, 5.01).IsValid);
    [Fact] public void NegativeStart() => Assert.False(V(-1, 5).IsValid);
    [Fact] public void BeyondDuration() => Assert.False(V(50, 61).IsValid);
    [Fact] public void UnknownDurationSkipsLengthCheck() => Assert.True(V(50, 610, duration: 0).IsValid);
    [Fact] public void ShortenKeepZero() => Assert.False(V(1, 5, SegmentAction.Shorten, 0).IsValid);
    [Fact] public void ShortenKeepTooLong() => Assert.False(V(1, 5, SegmentAction.Shorten, 4).IsValid);
    [Fact] public void KeepActionNotAllowed() => Assert.False(V(1, 5, SegmentAction.Keep).IsValid);

    [Fact]
    public void OverlapRejected()
    {
        var existing = new CutSegment { Start = 10, End = 20 };
        Assert.False(V(15, 25, existing: existing).IsValid);
        Assert.False(V(5, 11, existing: existing).IsValid);
        Assert.False(V(12, 18, existing: existing).IsValid);
        Assert.True(V(20, 25, existing: existing).IsValid); // 隣接は可
        Assert.True(V(5, 10, existing: existing).IsValid);
    }

    [Fact]
    public void IgnoreSelfWhenUpdating()
    {
        var self = new CutSegment { Start = 10, End = 20 };
        Assert.True(CutSegmentValidator.Validate(12, 22, SegmentAction.Remove, 0, 60, [self], self.Id).IsValid);
    }
}

public class EditTimelineCalculatorTests
{
    [Fact]
    public void NoCutsKeepsWholeVideo()
    {
        var r = EditTimelineCalculator.ComputeKeepRanges(60, []);
        Assert.Single(r);
        Assert.Equal(new TimeRange(0, 60), r[0]);
        Assert.True(EditTimelineCalculator.IsIdentity(60, r));
    }

    [Fact]
    public void RemoveAndShorten()
    {
        var cuts = new[]
        {
            new CutSegment { Start = 30, End = 40, Action = SegmentAction.Shorten, KeepSeconds = 2 },
            new CutSegment { Start = 10, End = 20, Action = SegmentAction.Remove },
        };
        var r = EditTimelineCalculator.ComputeKeepRanges(60, cuts);
        Assert.Equal([new TimeRange(0, 10), new TimeRange(20, 32), new TimeRange(40, 60)], r);
        Assert.Equal(42, EditTimelineCalculator.OutputDuration(r), 3);
    }

    [Fact]
    public void CutAtStartAndEnd()
    {
        var cuts = new[]
        {
            new CutSegment { Start = 0, End = 5 },
            new CutSegment { Start = 55, End = 60 },
        };
        var r = EditTimelineCalculator.ComputeKeepRanges(60, cuts);
        Assert.Equal([new TimeRange(5, 55)], r);
    }

    [Fact]
    public void AdjacentKeepRangesAreMerged()
    {
        // 短縮の残し部分 [10,11) は直前の通常区間 [0,10) と連続する。
        var r = EditTimelineCalculator.ComputeKeepRanges(30, [new CutSegment { Start = 10, End = 20, Action = SegmentAction.Shorten, KeepSeconds = 1 }]);
        Assert.Equal([new TimeRange(0, 11), new TimeRange(20, 30)], r);
    }

    [Fact]
    public void RemoveEverything()
        => Assert.Empty(EditTimelineCalculator.ComputeKeepRanges(10, [new CutSegment { Start = 0, End = 10 }]));

    [Fact]
    public void DefensiveAgainstOverlap()
    {
        var r = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 10, End = 30 }, new CutSegment { Start = 20, End = 40 }]);
        Assert.Equal([new TimeRange(0, 10), new TimeRange(40, 60)], r);
    }

    [Fact]
    public void MapToOutput()
    {
        var r = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 10, End = 20 }]);
        Assert.Equal(5, EditTimelineCalculator.MapToOutput(5, r));
        Assert.Null(EditTimelineCalculator.MapToOutput(15, r));
        Assert.Equal(15, EditTimelineCalculator.MapToOutput(25, r));
    }
}

public class UndoRedoServiceTests
{
    [Fact]
    public void UndoRedoCycle()
    {
        var u = new UndoRedoService<string>();
        string state = "a";
        u.Record("to b", state); state = "b";
        u.Record("to c", state); state = "c";
        Assert.Equal("to c", u.UndoLabel);
        state = u.Undo(state); Assert.Equal("b", state);
        state = u.Undo(state); Assert.Equal("a", state);
        Assert.False(u.CanUndo);
        state = u.Redo(state); Assert.Equal("b", state);
        u.Record("to d", state); state = "d";
        Assert.False(u.CanRedo); // 新しい操作で Redo 履歴は破棄
    }

    [Fact]
    public void CapacityDropsOldest()
    {
        var u = new UndoRedoService<int>(3);
        for (int i = 0; i < 5; i++) u.Record("x", i);
        Assert.Equal(3, u.UndoCount);
        Assert.Equal(4, u.Undo(99));
        Assert.Equal(3, u.Undo(4));
        Assert.Equal(2, u.Undo(3));
        Assert.False(u.CanUndo);
    }

    [Fact]
    public void UndoWhenEmptyThrows() => Assert.Throws<InvalidOperationException>(() => new UndoRedoService<int>().Undo(0));
}

public class ProjectEditorTests
{
    [Fact]
    public void AddCutAndUndoRedo()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (res, seg) = ed.AddCut(10, 20, SegmentAction.Remove, 0);
        Assert.True(res.Success);
        Assert.Single(ed.Project.CutSegments);
        Assert.True(ed.IsDirty);
        ed.Undo();
        Assert.Empty(ed.Project.CutSegments);
        ed.Redo();
        Assert.Single(ed.Project.CutSegments);
        Assert.Equal(seg!.Id, ed.Project.CutSegments[0].Id);
    }

    [Fact]
    public void InvalidCutDoesNotRecordHistory()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (res, _) = ed.AddCut(20, 10, SegmentAction.Remove, 0);
        Assert.False(res.Success);
        Assert.False(ed.History.CanUndo);
    }

    [Fact]
    public void UpdateAndRemoveCut()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (_, seg) = ed.AddCut(10, 20, SegmentAction.Remove, 0);
        Assert.True(ed.UpdateCut(seg!.Id, 10, 25, SegmentAction.Shorten, 2).Success);
        Assert.Equal(SegmentAction.Shorten, ed.Project.CutSegments[0].Action);
        Assert.Equal(25, ed.Project.CutSegments[0].End);
        ed.Undo();
        Assert.Equal(20, ed.Project.CutSegments[0].End);
        Assert.True(ed.RemoveCut(seg.Id).Success);
        Assert.Empty(ed.Project.CutSegments);
        ed.Undo();
        Assert.Single(ed.Project.CutSegments);
    }

    [Fact]
    public void CutsAreSortedByStart()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCut(30, 35, SegmentAction.Remove, 0);
        ed.AddCut(5, 8, SegmentAction.Remove, 0);
        Assert.Equal([5.0, 30.0], ed.Project.CutSegments.Select(c => c.Start));
    }

    [Fact]
    public void AcceptCandidateCreatesSegmentAndRemovingRevertsCandidate()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var cand = new CutCandidate { Start = 10, End = 20, SuggestedAction = SegmentAction.Shorten, KeepSeconds = 1.5, Confidence = 0.9, Reason = "静止＋無音" };
        ed.ReplaceCandidates(SegmentSource.LocalAnalysis, [cand], "解析");
        Assert.True(ed.AcceptCandidate(cand.Id).Success);
        var seg = Assert.Single(ed.Project.CutSegments);
        Assert.Equal(SegmentSource.LocalAnalysis, seg.Source);
        Assert.Equal(1.5, seg.KeepSeconds);
        Assert.Equal(CandidateStatus.Accepted, ed.Project.CutCandidates[0].Status);
        Assert.False(ed.AcceptCandidate(cand.Id).Success);

        ed.RemoveCut(seg.Id);
        Assert.Equal(CandidateStatus.Pending, ed.Project.CutCandidates[0].Status);
    }

    [Fact]
    public void CandidatesAreNotAppliedUntilAccepted()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.ReplaceCandidates(SegmentSource.LocalAnalysis, [new CutCandidate { Start = 1, End = 5 }], "解析");
        Assert.Empty(ed.Project.CutSegments);
        Assert.Single(EditTimelineCalculator.ComputeKeepRanges(60, ed.Project.CutSegments));
    }

    [Fact]
    public void AcceptOverlappingCandidateMergesWithTheExistingCut()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCut(10, 20, SegmentAction.Remove, 0);
        var cand = new CutCandidate { Start = 15, End = 25, SuggestedAction = SegmentAction.Remove };
        ed.AddCandidates([cand], "AI");
        Assert.True(ed.AcceptCandidate(cand.Id).Success);
        Assert.Equal(CandidateStatus.Accepted, ed.Project.CutCandidates[0].Status);
        var cut = Assert.Single(ed.Project.CutSegments);   // 既存のカットも含めて 1 つにまとめる
        Assert.Equal((10, 25), (cut.Start, cut.End));
        Assert.Equal(cut.Id, ed.Project.CutCandidates[0].AcceptedSegmentId);
        ed.Undo();
        Assert.Equal((10, 20), (ed.Project.CutSegments[0].Start, ed.Project.CutSegments[0].End));
        Assert.Equal(CandidateStatus.Pending, ed.Project.CutCandidates[0].Status);
    }

    [Fact]
    public void ReplaceCandidatesKeepsAcceptedAndOtherSources()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var local1 = new CutCandidate { Start = 1, End = 5, SuggestedAction = SegmentAction.Remove };
        var local2 = new CutCandidate { Start = 10, End = 15 };
        var ai = new CutCandidate { Start = 30, End = 35, Source = SegmentSource.Ai };
        ed.ReplaceCandidates(SegmentSource.LocalAnalysis, [local1, local2], "解析");
        ed.AddCandidates([ai], "AI");
        ed.AcceptCandidate(local1.Id);
        ed.ReplaceCandidates(SegmentSource.LocalAnalysis, [new CutCandidate { Start = 40, End = 45 }], "再解析");
        Assert.Contains(ed.Project.CutCandidates, c => c.Id == local1.Id);
        Assert.DoesNotContain(ed.Project.CutCandidates, c => c.Id == local2.Id);
        Assert.Contains(ed.Project.CutCandidates, c => c.Id == ai.Id);
        Assert.Equal(3, ed.Project.CutCandidates.Count);
    }

    [Fact]
    public void DismissCandidate()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var c = new CutCandidate { Start = 1, End = 5 };
        ed.AddCandidates([c], "x");
        Assert.True(ed.SetCandidateStatus(c.Id, CandidateStatus.Dismissed).Success);
        Assert.Equal(CandidateStatus.Dismissed, ed.Project.CutCandidates[0].Status);
        ed.Undo();
        Assert.Equal(CandidateStatus.Pending, ed.Project.CutCandidates[0].Status);
    }
}

public class CapturePointManagementTests
{
    [Fact]
    public void AddAssignsIdsAndSequenceByTime()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (_, a) = ed.AddCapture(20);
        var (_, b) = ed.AddCapture(5);
        var (_, c) = ed.AddCapture(12.4);
        Assert.Equal([1, 2, 3], new[] { a!.Id, b!.Id, c!.Id });
        Assert.Equal([2, 3, 1], ed.Project.CapturePoints.Select(p => p.Id));
        Assert.Equal([1, 2, 3], ed.Project.CapturePoints.Select(p => p.Sequence));
        Assert.Equal(12.4, c.Timestamp);
        Assert.Equal(12.4, c.SelectedFrameTime);
    }

    [Fact]
    public void IdsAreNotReusedAfterDelete()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(1);
        var (_, b) = ed.AddCapture(2);
        ed.RemoveCapture(b!.Id);
        var (_, c) = ed.AddCapture(3);
        Assert.Equal(3, c!.Id);
    }

    [Fact]
    public void UndoRestoresNextId()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(1);
        ed.Undo();
        Assert.Empty(ed.Project.CapturePoints);
        Assert.Equal(1, ed.Project.NextCaptureId);
    }

    [Fact]
    public void DuplicateTimeRejected()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(10);
        Assert.False(ed.AddCapture(10.0004).Result.Success);
        Assert.False(ed.AddCapture(-1).Result.Success);
        Assert.False(ed.AddCapture(61).Result.Success);
    }

    [Fact]
    public void SelectedFrameTimeChangeKeepsTimestampAndResequences()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (_, a) = ed.AddCapture(10);
        ed.AddCapture(20);
        Assert.True(ed.SetSelectedFrameTime(a!.Id, 25).Success);
        var moved = ed.Project.CapturePoints.Single(p => p.Id == a.Id);
        Assert.Equal(10, moved.Timestamp);
        Assert.Equal(25, moved.SelectedFrameTime);
        Assert.Equal(2, moved.Sequence);
        ed.Undo();
        Assert.Equal(1, ed.Project.CapturePoints.Single(p => p.Id == a.Id).Sequence);
    }

    [Fact]
    public void CaptionEditAndUndo()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (_, a) = ed.AddCapture(10);
        ed.SetCaption(a!.Id, "  設定を開く ");
        Assert.Equal("設定を開く", ed.Project.CapturePoints[0].Caption);
        Assert.Equal(CaptionSource.Manual, ed.Project.CapturePoints[0].CaptionSource);
        ed.Undo();
        Assert.Equal("", ed.Project.CapturePoints[0].Caption);
        Assert.Equal(CaptionSource.None, ed.Project.CapturePoints[0].CaptionSource);
    }

    [Fact]
    public void ApplyCaptionsIsSingleUndoStep()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCapture(1);
        ed.AddCapture(2);
        int before = ed.History.UndoCount;
        var n = ed.ApplyCaptions([(1, "A"), (2, "B"), (99, "X")], CaptionSource.Ai);
        Assert.Equal(2, n);
        Assert.Equal(before + 1, ed.History.UndoCount);
        Assert.All(ed.Project.CapturePoints, p => Assert.Equal(CaptionSource.Ai, p.CaptionSource));
        ed.Undo();
        Assert.All(ed.Project.CapturePoints, p => Assert.Equal("", p.Caption));
    }
}
