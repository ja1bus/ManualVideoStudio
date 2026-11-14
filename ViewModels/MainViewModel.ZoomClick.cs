using CommunityToolkit.Mvvm.ComponentModel;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 拡大表示とクリック位置の強調（v1.21。設計 7.3〜7.5）の主画面側: 図形タブのクリックの強調の設定、プレビューの拡大・輪、
/// 書き出しの重ね合わせ（拡大・クリック）の組み立て、タイムラインのカットの端のドラッグの反映。
/// </summary>
public sealed partial class MainViewModel
{
    // ---------------- クリック位置の強調（図形タブの上部） ----------------

    /// <summary>大きさの選択肢（ClickHighlightSize の順）。</summary>
    public IReadOnlyList<string> ClickHighlightSizeNames { get; } = [Loc.Get("Click.Size.Small"), Loc.Get("Click.Size.Medium"), Loc.Get("Click.Size.Large")];

    /// <summary>クリックの強調を使えるか（プロジェクトを開いていて、録画の記録にクリックがある）。</summary>
    public bool CanUseClickHighlight => HasProject && HasRecordedClicks;

    /// <summary>チェックボックスの文言「クリック位置を強調する（N 件）」。</summary>
    public string ClickHighlightLabel => Loc.Format("Click.Enabled", RecordedClickCount);

    /// <summary>クリック位置を強調するか。変えると元に戻せる編集として記録する。</summary>
    public bool ClickHighlightEnabled
    {
        get => Project?.ClickHighlight.Enabled ?? false;
        set => SetClickHighlight(value, ClickHighlightSizeValue);
    }

    /// <summary>輪の大きさ（0: 小 / 1: 中 / 2: 大）。</summary>
    public int ClickHighlightSizeIndex
    {
        get => (int)ClickHighlightSizeValue;
        set
        {
            if (value < 0 || value > (int)ClickHighlightSize.Large) return;
            SetClickHighlight(ClickHighlightEnabled, (ClickHighlightSize)value);
        }
    }

    private ClickHighlightSize ClickHighlightSizeValue => Project?.ClickHighlight.Size ?? ClickHighlightSize.Medium;

    private void SetClickHighlight(bool enabled, ClickHighlightSize size)
    {
        if (Editor is null) return;
        if (enabled == ClickHighlightEnabled && size == ClickHighlightSizeValue) return;
        bool enabledChanged = enabled != ClickHighlightEnabled;
        var r = Editor.SetClickHighlight(enabled, size);
        if (!r.Success)
        {
            StatusMessage = r.Message;
            return;
        }
        StatusMessage = enabledChanged
            ? Loc.Get(enabled ? "Click.TurnedOn" : "Click.TurnedOff")
            : Loc.Format("Click.SizeChanged", ClickHighlightSizeNames[(int)size]);
    }

    partial void OnRecordingEventsChanged(RecordingEvents? value)
    {
        NotifyClickHighlight();
        UpdatePreviewOverlays();
    }

    private void NotifyClickHighlight()
    {
        OnPropertyChanged(nameof(CanUseClickHighlight));
        OnPropertyChanged(nameof(ClickHighlightLabel));
        OnPropertyChanged(nameof(ClickHighlightEnabled));
        OnPropertyChanged(nameof(ClickHighlightSizeIndex));
    }

    // ---------------- プレビュー（拡大・クリックの輪） ----------------

    /// <summary>プレビューの拡大の倍率（1 なら等倍）。</summary>
    [ObservableProperty] private double _previewZoomScale = 1;
    /// <summary>プレビューの拡大の移動量（動画の px。拡大の入れ物の RenderTransform に使う: −X·Z）。</summary>
    [ObservableProperty] private double _previewZoomOffsetX;
    /// <summary>プレビューの拡大の移動量（動画の px。−Y·Z）。</summary>
    [ObservableProperty] private double _previewZoomOffsetY;
    /// <summary>今表示中のクリックの輪（動画の px）。</summary>
    [ObservableProperty] private IReadOnlyList<ClickWindow> _currentClickMarks = [];
    /// <summary>プレビューの輪の半径（動画の px）。</summary>
    [ObservableProperty] private double _previewClickRadius;
    /// <summary>拡大の入れ物の幅（動画の px。大きさが分からなければ NaN＝中身に合わせる）。</summary>
    [ObservableProperty] private double _previewFrameWidth = double.NaN;
    /// <summary>拡大の入れ物の高さ（動画の px。大きさが分からなければ NaN）。</summary>
    [ObservableProperty] private double _previewFrameHeight = double.NaN;

    /// <summary>今のプレビューの拡大の状態（試験用にも公開）。</summary>
    public ZoomState PreviewZoom { get; private set; } = ZoomState.None;

    /// <summary>
    /// 再生位置の拡大とクリックの輪を求めてプレビューへ渡す（再生位置の変化・編集のたび）。図形のプレビューと同じく字幕編集中だけで、
    /// 削除される位置・拡大の表示時間の外では等倍。クリックは強調が ON で、元動画の時刻が残る位置のものだけ。
    /// </summary>
    private void UpdatePreviewOverlays()
    {
        var p = Project;
        var meta = Metadata;
        int w = meta?.Width ?? 0, h = meta?.Height ?? 0;
        PreviewFrameWidth = w > 0 && h > 0 ? w : double.NaN;
        PreviewFrameHeight = w > 0 && h > 0 ? h : double.NaN;

        var zoom = ZoomState.None;
        IReadOnlyList<ClickWindow> marks = [];
        double radius = 0;
        if (p is not null && w > 0 && h > 0 && IsSubtitlePhase)
        {
            var keep = KeepRanges;
            if (!EditTimelineCalculator.IsRemoved(CurrentTime, keep))
            {
                var t = EditTimelineCalculator.MapToOutputClamped(CurrentTime, keep);
                zoom = PreviewZoomAt(p.ShapeGroups, t, w, h);
                if (p.ClickHighlight.Enabled && RecordingEvents is { Clicks.Count: > 0 } ev)
                {
                    radius = VideoShapeLayers.ClickRadiusFor(p.ClickHighlight.Size, h);
                    marks = ClickWindowsAt(ev.Clicks, keep, w, h, t);
                }
            }
        }
        PreviewZoom = zoom;
        PreviewZoomScale = zoom.Zoom;
        PreviewZoomOffsetX = -zoom.X * zoom.Zoom;
        PreviewZoomOffsetY = -zoom.Y * zoom.Zoom;
        PreviewClickRadius = radius;
        if (marks.Count != CurrentClickMarks.Count || !marks.SequenceEqual(CurrentClickMarks)) CurrentClickMarks = marks;
    }

    /// <summary>出力時刻 outputTime の拡大の状態（書き出しと同じ <see cref="ZoomMath"/> の式）。</summary>
    public static ZoomState PreviewZoomAt(IEnumerable<ShapeGroup> groups, double outputTime, int videoWidth, int videoHeight)
        => ZoomMath.At(VideoShapeLayers.ZoomsOf(groups, videoWidth, videoHeight), outputTime);

    /// <summary>出力時刻 outputTime に表示中のクリックの輪（書き出しと同じ時間の決め方）。</summary>
    public static IReadOnlyList<ClickWindow> ClickWindowsAt(IEnumerable<RecordedClick> clicks, IReadOnlyList<TimeRange> keep, int videoWidth, int videoHeight, double outputTime)
    {
        var windows = VideoShapeLayers.ClickWindowsOf(VideoShapeLayers.ClicksOf(clicks.Select(c => (c.T, (double)c.X, (double)c.Y)), keep, videoWidth, videoHeight));
        return windows.Where(win => outputTime >= win.Start && outputTime < win.End).ToList();
    }

    // ---------------- 書き出し（拡大・クリックの強調） ----------------

    /// <summary>
    /// 書き出しで重ねるものに、拡大と（ON なら）クリックの強調を足す。shapes は図形の画像・モザイク（図形が無ければ null）。
    /// 輪の画像は renderRing(半径) で作る（主画面では UI スレッドの <c>ClickRingRenderer.RenderPng</c>）。
    /// 図形が無く、拡大・クリックも無ければ null（従来どおり重ねるものなし）。図形が無くてもクリックがあれば重ねるものを返す。
    /// </summary>
    public static VideoShapeLayers? ComposeExportLayers(VideoShapeLayers? shapes, ProjectDocument project, IReadOnlyList<RecordedClick>? clicks,
        IReadOnlyList<TimeRange> keep, int videoWidth, int videoHeight, Func<double, byte[]> renderRing)
    {
        var layers = shapes ?? VideoShapeLayers.Empty;
        var zooms = VideoShapeLayers.ZoomsOf(project.ShapeGroups, videoWidth, videoHeight);
        if (zooms.Count > 0) layers = layers with { Zooms = zooms };
        if (project.ClickHighlight.Enabled && clicks is { Count: > 0 })
        {
            var marks = VideoShapeLayers.ClicksOf(clicks.Select(c => (c.T, (double)c.X, (double)c.Y)), keep, videoWidth, videoHeight);
            var radius = VideoShapeLayers.ClickRadiusFor(project.ClickHighlight.Size, videoHeight);
            if (marks.Count > 0 && radius > 0)
                layers = layers with { Clicks = marks, ClickRadius = radius, ClickRingPng = renderRing(radius) };
        }
        return shapes is null && layers.IsEmpty ? null : layers;
    }

    // ---------------- タイムラインのカットの端のドラッグ ----------------

    /// <summary>タイムラインでカットの端をドラッグして離した（1 回の元に戻す操作。他のカットと重なればまとめる）。</summary>
    public void ApplyCutEdgeDrag(string id, double start, double end)
    {
        if (Editor is null || GuardCutLocked()) return;
        var cut = Editor.Project.CutSegments.FirstOrDefault(c => c.Id == id);
        if (cut is null) return;
        // 短縮のカットは、残す秒数が新しい長さに収まるよう縮める。
        var keepSeconds = cut.Action == SegmentAction.Shorten ? Math.Min(cut.KeepSeconds, Math.Max(0, end - start - 0.05)) : 0;
        var r = Editor.UpdateCut(id, start, end, cut.Action, keepSeconds);
        if (!r.Success)
        {
            StatusMessage = Loc.Format("Timeline.EdgeDrag.Failed", r.Message);
            return;
        }
        StatusMessage = Loc.Format("Timeline.EdgeDrag.Done", TimeFormat.FormatShort(start), TimeFormat.FormatShort(end))
            + (r.Message.Length > 0 ? " " + r.Message : "");
    }
}
