using ManualVideoStudio.Controls;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Tests;

/// <summary>
/// 1.21.0 の波 3: タイムラインのカットの端のドラッグ（純粋な計算）、クリック位置の強調の設定（元に戻す）、
/// 書き出しの重ね合わせ（拡大・クリック）の組み立て、プレビューの拡大・クリックの輪。
/// </summary>
public class Release121TimelinePreviewTests
{
    private static CutSegment Cut(string id, double start, double end) => new() { Id = id, Start = start, End = end, Action = SegmentAction.Remove };

    private static ZoomAnnotation Zoom(double x, double y, double w, double h) => new() { X = x, Y = y, Width = w, Height = h };

    // ---------------- カットの端のドラッグ ----------------

    [Fact]
    public void HitEdgeWithinFourPixels()
    {
        var cuts = new[] { Cut("a", 10, 20) };
        const double spp = 0.01; // 1px = 0.01 秒
        Assert.Equal(("a", CutEdge.Start), Pick(TimelineEdgeDrag.HitEdge(cuts, 10.04, spp)));
        Assert.Equal(("a", CutEdge.Start), Pick(TimelineEdgeDrag.HitEdge(cuts, 9.96, spp)));
        Assert.Equal(("a", CutEdge.End), Pick(TimelineEdgeDrag.HitEdge(cuts, 19.97, spp)));
        Assert.Null(TimelineEdgeDrag.HitEdge(cuts, 10.05, spp)); // 5px は外
        Assert.Null(TimelineEdgeDrag.HitEdge(cuts, 15, spp));
        Assert.Null(TimelineEdgeDrag.HitEdge(cuts, 10, 0)); // 幅が分からなければ当たらない
    }

    [Fact]
    public void HitEdgePrefersNearestThenInsideAtSharedBoundary()
    {
        var cuts = new[] { Cut("a", 10, 20), Cut("b", 20.02, 30) };
        const double spp = 0.01;
        Assert.Equal(("a", CutEdge.End), Pick(TimelineEdgeDrag.HitEdge(cuts, 20.005, spp)));
        Assert.Equal(("b", CutEdge.Start), Pick(TimelineEdgeDrag.HitEdge(cuts, 20.015, spp)));
        // 同じ距離なら、押した側（内側）のカット。
        var touching = new[] { Cut("a", 10, 20), Cut("b", 20, 30) };
        Assert.Equal(("b", CutEdge.Start), Pick(TimelineEdgeDrag.HitEdge(touching, 20.01, spp)));
        Assert.Equal(("a", CutEdge.End), Pick(TimelineEdgeDrag.HitEdge(touching, 19.99, spp)));
    }

    private static (string, CutEdge)? Pick((CutSegment Cut, CutEdge Edge)? hit) => hit is { } h ? (h.Cut.Id, h.Edge) : null;

    [Fact]
    public void SnapWithinFivePixelsUnlessShift()
    {
        double[] targets = [5.0, 5.2, 8.0];
        const double spp = 0.01;
        Assert.Equal(5.0, TimelineEdgeDrag.Snap(5.04, targets, spp));
        Assert.Equal(5.2, TimelineEdgeDrag.Snap(5.16, targets, spp)); // 近い方
        Assert.Equal(5.06, TimelineEdgeDrag.Snap(5.06, targets, spp), 9); // 6px は吸着しない
        Assert.Equal(5.0, TimelineEdgeDrag.Drag(2, 10, CutEdge.Start, 5.04, 60, targets, spp, noSnap: false).Start);
        Assert.Equal(5.04, TimelineEdgeDrag.Drag(2, 10, CutEdge.Start, 5.04, 60, targets, spp, noSnap: true).Start);
        Assert.Equal(8.0, TimelineEdgeDrag.Drag(2, 10, CutEdge.End, 7.97, 60, targets, spp, noSnap: false).End);
        Assert.Equal(7.97, TimelineEdgeDrag.Drag(2, 10, CutEdge.End, 7.97, 60, targets, spp, noSnap: true).End);
    }

    [Fact]
    public void ResizeKeepsMinimumLengthAndBounds()
    {
        Assert.Equal((19.95, 20.0), TimelineEdgeDrag.Resize(10, 20, CutEdge.Start, 19.99, 60));
        Assert.Equal((19.95, 20.0), TimelineEdgeDrag.Resize(10, 20, CutEdge.Start, 25, 60)); // 終了を越えても 0.05 秒残す
        Assert.Equal((10.0, 10.05), TimelineEdgeDrag.Resize(10, 20, CutEdge.End, 3, 60));
        Assert.Equal((0.0, 20.0), TimelineEdgeDrag.Resize(10, 20, CutEdge.Start, -4, 60));
        Assert.Equal((10.0, 60.0), TimelineEdgeDrag.Resize(10, 20, CutEdge.End, 75, 60));
        Assert.Equal((12.346, 20.0), TimelineEdgeDrag.Resize(10, 20, CutEdge.Start, 12.34567, 60)); // ミリ秒に丸める
        var (s, e) = TimelineEdgeDrag.Resize(10, 10.3, CutEdge.Start, 10.27, 60);
        Assert.True(e - s >= CutEdgeTarget.MinLength - 1e-9);
    }

    [Fact]
    public async Task EdgeDragUpdatesCutWithOneUndo()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        var (_, cut) = vm.Editor!.AddCut(10, 20, SegmentAction.Remove, 0);
        vm.ApplyCutEdgeDrag(cut!.Id, 8, 20);
        var updated = Assert.Single(vm.Editor.Project.CutSegments);
        Assert.Equal(8, updated.Start);
        Assert.Contains("カットの範囲を変えました", vm.StatusMessage);
        vm.UndoCommand.Execute(null);
        Assert.Equal(10, Assert.Single(vm.Editor.Project.CutSegments).Start);
    }

    [Fact]
    public async Task EdgeDragMergesWithNeighbour()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        var (_, a) = vm.Editor!.AddCut(10, 20, SegmentAction.Remove, 0);
        vm.Editor.AddCut(25, 30, SegmentAction.Remove, 0);
        vm.ApplyCutEdgeDrag(a!.Id, 10, 26);
        var merged = Assert.Single(vm.Editor.Project.CutSegments);
        Assert.Equal((10.0, 30.0), (merged.Start, merged.End));
        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Editor.Project.CutSegments.Count);
    }

    // ---------------- クリック位置の強調の設定 ----------------

    [Fact]
    public void SetClickHighlightIsUndoable()
    {
        var project = TestProjects.Create();
        var ed = new ProjectEditor(project);
        Assert.True(ed.SetClickHighlight(true, ClickHighlightSize.Large).Success);
        Assert.True(project.ClickHighlight.Enabled);
        Assert.Equal(ClickHighlightSize.Large, project.ClickHighlight.Size);
        Assert.True(ed.IsDirty);
        ed.SetClickHighlight(true, ClickHighlightSize.Small);
        Assert.Equal(ClickHighlightSize.Small, project.ClickHighlight.Size);
        ed.Undo();
        Assert.Equal(ClickHighlightSize.Large, project.ClickHighlight.Size);
        ed.Undo();
        Assert.False(project.ClickHighlight.Enabled);
        Assert.Equal(ClickHighlightSize.Medium, project.ClickHighlight.Size);
        ed.Redo();
        Assert.True(project.ClickHighlight.Enabled);
    }

    [Fact]
    public void SetClickHighlightWithoutChangeRecordsNothing()
    {
        var project = TestProjects.Create();
        var ed = new ProjectEditor(project);
        ed.SetClickHighlight(false, ClickHighlightSize.Medium);
        Assert.False(ed.IsDirty);
        Assert.False(ed.History.CanUndo);
    }

    [Fact]
    public async Task ClickHighlightUiNeedsRecordedClicks()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.False(vm.CanUseClickHighlight);
        vm.RecordingEvents = new RecordingEvents { Clicks = [new RecordedClick(5, 100, 100, RecordedClick.Left), new RecordedClick(9, 10, 10, RecordedClick.Left)] };
        Assert.True(vm.CanUseClickHighlight);
        Assert.Equal("クリック位置を強調する（2 件）", vm.ClickHighlightLabel);

        vm.ClickHighlightEnabled = true;
        Assert.True(vm.Editor!.Project.ClickHighlight.Enabled);
        Assert.True(vm.IsDirty);
        vm.ClickHighlightSizeIndex = 2;
        Assert.Equal(ClickHighlightSize.Large, vm.Editor.Project.ClickHighlight.Size);
        vm.UndoCommand.Execute(null);
        Assert.Equal(1, vm.ClickHighlightSizeIndex);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.ClickHighlightEnabled);
    }

    // ---------------- 書き出しの重ね合わせ ----------------

    private static readonly Func<double, byte[]> FakeRing = _ => [1, 2, 3];

    private static ProjectDocument ProjectWithZoom()
    {
        var p = TestProjects.Create();
        p.ShapeGroups.Add(new ShapeGroup { Id = "z", Start = 2, End = 6, ImageWidth = 1920, ImageHeight = 1080, Shapes = [Zoom(480, 270, 960, 540)] });
        return p;
    }

    private static readonly IReadOnlyList<RecordedClick> Clicks = [new RecordedClick(1, 100, 100, RecordedClick.Left), new RecordedClick(15, 200, 200, RecordedClick.Left)];

    [Fact]
    public void ExportLayersIncludeZooms()
    {
        var p = ProjectWithZoom();
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, p.CutSegments);
        var layers = MainViewModel.ComposeExportLayers(VideoShapeLayers.Empty, p, null, keep, 1920, 1080, FakeRing);
        var z = Assert.Single(layers!.Zooms);
        Assert.Equal(2, z.Factor, 6);
        Assert.False(layers.HasClicks);
    }

    [Fact]
    public void ExportLayersIncludeClicksOnlyWhenEnabled()
    {
        var p = TestProjects.Create();
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [Cut("c", 10, 20)]);
        Assert.Null(MainViewModel.ComposeExportLayers(null, p, Clicks, keep, 1920, 1080, FakeRing));

        p.ClickHighlight = new ClickHighlightSettings { Enabled = true, Size = ClickHighlightSize.Medium };
        double? radius = null;
        var layers = MainViewModel.ComposeExportLayers(null, p, Clicks, keep, 1920, 1080, r => { radius = r; return [9]; });
        Assert.NotNull(layers);
        Assert.False(layers!.IsEmpty); // 図形が無くてもクリックだけで重ねるものになる
        Assert.True(layers.HasClicks);
        var c = Assert.Single(layers.Clicks); // 15 秒のクリックは削除される位置
        Assert.Equal(1, c.OutputTime, 6);
        Assert.Equal(40, layers.ClickRadius, 6);
        Assert.Equal(40, radius);
        Assert.Empty(layers.Zooms);
    }

    [Fact]
    public void ExportLayersWithoutAnythingStayNull()
    {
        var p = TestProjects.Create();
        p.ClickHighlight = new ClickHighlightSettings { Enabled = true };
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, p.CutSegments);
        Assert.Null(MainViewModel.ComposeExportLayers(null, p, null, keep, 1920, 1080, FakeRing));
        Assert.Null(MainViewModel.ComposeExportLayers(null, p, [], keep, 1920, 1080, FakeRing));
        // 図形の重ね合わせが渡されたら（中身が空でも）そのまま返す（従来どおり）。
        Assert.Same(VideoShapeLayers.Empty, MainViewModel.ComposeExportLayers(VideoShapeLayers.Empty, p, null, keep, 1920, 1080, FakeRing));
    }

    // ---------------- プレビュー ----------------

    [Fact]
    public void PreviewZoomStateInsideAndOutside()
    {
        var p = ProjectWithZoom();
        Assert.True(MainViewModel.PreviewZoomAt(p.ShapeGroups, 1, 1920, 1080).IsIdentity);
        var mid = MainViewModel.PreviewZoomAt(p.ShapeGroups, 4, 1920, 1080);
        Assert.Equal(2, mid.Zoom, 6);
        Assert.Equal(480, mid.X, 6);
        Assert.Equal(270, mid.Y, 6);
        var ramp = MainViewModel.PreviewZoomAt(p.ShapeGroups, 2.15, 1920, 1080);
        Assert.InRange(ramp.Zoom, 1.01, 1.99);
        Assert.True(MainViewModel.PreviewZoomAt(p.ShapeGroups, 7, 1920, 1080).IsIdentity);
    }

    [Fact]
    public async Task PreviewFollowsCurrentTimeZoomAndClicks()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        vm.Editor!.AddCut(20, 30, SegmentAction.Remove, 0);
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        Assert.True(vm.Editor.AddShapeGroup(2, 6, [Zoom(480, 270, 960, 540)], 1920, 1080).Result.Success);
        Assert.Equal(1920, vm.PreviewFrameWidth);

        vm.SeekTo(4);
        Assert.Equal(2, vm.PreviewZoomScale, 6);
        Assert.Equal(-960, vm.PreviewZoomOffsetX, 6);
        Assert.Equal(-540, vm.PreviewZoomOffsetY, 6);
        vm.SeekTo(10);
        Assert.Equal(1, vm.PreviewZoomScale, 6);
        Assert.True(vm.PreviewZoom.IsIdentity);

        // クリック（元動画 12 秒と、削除される 25 秒）。強調が OFF なら出さない。
        vm.RecordingEvents = new RecordingEvents { Clicks = [new RecordedClick(12, 300, 200, RecordedClick.Left), new RecordedClick(25, 50, 50, RecordedClick.Left)] };
        vm.SeekTo(12.1);
        Assert.Empty(vm.CurrentClickMarks);
        vm.ClickHighlightEnabled = true;
        var mark = Assert.Single(vm.CurrentClickMarks);
        Assert.Equal((300.0, 200.0), (mark.X, mark.Y));
        Assert.Equal(40, vm.PreviewClickRadius, 6);
        vm.SeekTo(13);
        Assert.Empty(vm.CurrentClickMarks); // 0.5 秒で消える
        vm.SeekTo(25.1); // 削除される位置
        Assert.Empty(vm.CurrentClickMarks);
        Assert.True(vm.PreviewZoom.IsIdentity);
    }
}
