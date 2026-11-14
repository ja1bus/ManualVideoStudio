using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;

namespace ManualVideoStudio.Controls;

/// <summary>
/// 動画全体の時間軸（元動画基準）。上から「目盛り」「候補レーン（ローカル解析=青 / AI=紫）」「カット（編集トラック。通常=灰 / 削除=赤 / 短縮=橙）」
/// 「字幕トラック（青緑。編集後の時刻の字幕を元動画の位置に写して描く）」「図形トラック（赤紫。v1.6。字幕と同じく元動画の位置に写す。
/// モザイクを含むまとまりは斜線付き）」「手順書画像レーン（緑）」を描く。確定編集と候補は別レーンで区別する。
/// 字幕編集中（カット確定済み）はカットのトラックに斜線と「カット確定済み」を重ねる。
/// 操作: クリック・ドラッグでシーク、Ctrl+ホイールで拡大縮小、ホイールで左右移動（拡大時）、ダブルクリックで全体表示。
/// 確定カット・候補をクリックすると、シークに加えて対応する項目を通知する（<see cref="ItemClicked"/>。一覧の行を選ぶ。v1.17.0）。
/// 一覧で選択中のカット・候補は枠線で強調し、拡大表示で画面の外にあれば見える位置へ移動する。
/// v1.21: 録画時の操作の記録がある動画では、目盛りと候補の間に「操作」の段（クリック＝点、キー入力＝細い帯、マウス移動＝淡い帯、
/// 不要マーカー＝橙の旗）を足し、下の段を 12px 下げる（記録が無ければ従来と同じ配置・高さ）。
/// 確定カットの端（±4px）はドラッグで動かせる（字幕編集中は無効。吸着 ±5px、Shift で吸着なし、Esc で取り消し）。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 18;
    // 操作の段（v1.21。録画時の操作の記録がある動画だけ。目盛りと候補の間に置き、下の段を OpsShift ずつ下げる）。
    private const double OpsTop = 21, OpsHeight = 10, OpsShift = 12;
    private const double CandidateHeight = 14;
    private const double TrackHeight = 34;
    private const double SubtitleHeight = 18;
    private const double ShapeHeight = 16;
    private const double CaptureHeight = 20;
    private const double SidePadding = 8;

    /// <summary>操作の段を出すか（記録が無い動画では出さず、高さも取らない）。</summary>
    private bool ShowOps => RecordingEvents is not null;
    private double Shift => ShowOps ? OpsShift : 0;
    private double CandidateTop => 22 + Shift;
    private double TrackTop => 40 + Shift;
    private double SubtitleTop => 78 + Shift;
    private double ShapeTop => 98 + Shift;
    private double CaptureTop => 118 + Shift;

    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xDC, 0xE4)));
    private static readonly Brush RemoveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Brush ShortenCutBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush ShortenKeepBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFC, 0xD9, 0xA0)));
    private static readonly Brush LocalCandidateBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush AiCandidateBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)));
    private static readonly Brush DismissedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x9C, 0xA3, 0xAF)));
    private static readonly Brush CaptureBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xA0, 0x5A)));
    private static readonly Brush SceneBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x6B, 0x72, 0x80)));
    private static readonly Brush MarkBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0xFA, 0xCC, 0x15)));
    private static readonly Brush SubtitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xA4)));
    private static readonly Brush SubtitleLaneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF5)));
    private static readonly Brush ShapeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0x26, 0xD3)));
    private static readonly Pen ShapeMosaicHatchPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 2));
    private static readonly Brush LockBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x41, 0x55)));
    private static readonly Pen LockHatchPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 2));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)));
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC)));
    private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 2));
    private static readonly Pen MarkPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04)), 1.5));
    private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xB0, 0xB7, 0xC0)), 1));
    private static readonly Pen CapturePen = Freeze(new Pen(CaptureBrush, 1.5));
    private static readonly Pen SelectOuterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 3));
    private static readonly Pen SelectInnerPen = Freeze(new Pen(Brushes.White, 1.2));
    private static readonly Typeface Face = new("Segoe UI");
    // 操作の段（v1.21）: クリック＝点、キー入力＝細い帯、マウス移動＝さらに淡く細い帯、不要マーカー＝橙の旗。
    private static readonly Brush OpsLaneBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xF6)));
    private static readonly Brush ClickDotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)));
    private static readonly Brush KeyRangeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush MoveRangeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x94, 0xA3, 0xB8)));
    private static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)));
    private static readonly Pen MarkerPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)), 1.2));
    // カットの端のドラッグ中の仮の範囲（v1.21）。
    private static readonly Brush EdgeDragBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xE5, 0x48, 0x4D)));
    private static readonly Pen EdgeDragPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen EdgeLinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 2));

    private double _viewStart;
    private double _viewLength;
    private bool _dragging;

    /// <summary>カットの端のドラッグ中の状態（v1.21。ドラッグしていなければ null）。</summary>
    private EdgeDragState? _edgeDrag;

    private sealed class EdgeDragState(CutSegment cut, CutEdge edge, double playhead)
    {
        public CutSegment Cut { get; } = cut;
        public CutEdge Edge { get; } = edge;
        /// <summary>ドラッグを始めたときの再生位置（ドラッグ中はシークで動くため、吸着先は始めた時点の値）。</summary>
        public double Playhead { get; } = playhead;
        public double Start { get; set; } = cut.Start;
        public double End { get; set; } = cut.End;
    }

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    // ---------------- Dependency properties ----------------

    public static readonly DependencyProperty DurationProperty = Reg(nameof(Duration), 0.0);
    public static readonly DependencyProperty CurrentTimeProperty = Reg(nameof(CurrentTime), 0.0);
    public static readonly DependencyProperty SegmentsProperty = Reg<IEnumerable?>(nameof(Segments), null);
    public static readonly DependencyProperty CandidatesProperty = Reg<IEnumerable?>(nameof(Candidates), null);
    public static readonly DependencyProperty CapturesProperty = Reg<IEnumerable?>(nameof(Captures), null);
    public static readonly DependencyProperty SceneChangesProperty = Reg<IEnumerable?>(nameof(SceneChanges), null);
    public static readonly DependencyProperty SubtitlesProperty = Reg<IEnumerable?>(nameof(Subtitles), null);
    public static readonly DependencyProperty ShapesProperty = Reg<IEnumerable?>(nameof(Shapes), null);
    public static readonly DependencyProperty CutLockedProperty = Reg(nameof(CutLocked), false);
    public static readonly DependencyProperty MarkInProperty = Reg<double?>(nameof(MarkIn), null);
    public static readonly DependencyProperty MarkOutProperty = Reg<double?>(nameof(MarkOut), null);
    public static readonly DependencyProperty RevisionProperty = Reg(nameof(Revision), 0);
    public static readonly DependencyProperty SelectedCutIdsProperty = Reg<IEnumerable?>(nameof(SelectedCutIds), null);
    public static readonly DependencyProperty SelectedCandidateIdsProperty = Reg<IEnumerable?>(nameof(SelectedCandidateIds), null);
    public static readonly DependencyProperty SelectedSubtitleIdsProperty = Reg<IEnumerable?>(nameof(SelectedSubtitleIds), null);
    public static readonly DependencyProperty SelectedShapeIdsProperty = Reg<IEnumerable?>(nameof(SelectedShapeIds), null);
    /// <summary>録画時の操作の記録（v1.21）。null なら操作の段を出さない。</summary>
    public static readonly DependencyProperty RecordingEventsProperty = DependencyProperty.Register(nameof(RecordingEvents), typeof(RecordingEvents), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoverTextProperty =DependencyProperty.Register(nameof(HoverText), typeof(string), typeof(TimelineControl), new PropertyMetadata(""));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double CurrentTime { get => (double)GetValue(CurrentTimeProperty); set => SetValue(CurrentTimeProperty, value); }
    public IEnumerable? Segments { get => (IEnumerable?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IEnumerable? Candidates { get => (IEnumerable?)GetValue(CandidatesProperty); set => SetValue(CandidatesProperty, value); }
    public IEnumerable? Captures { get => (IEnumerable?)GetValue(CapturesProperty); set => SetValue(CapturesProperty, value); }
    public IEnumerable? SceneChanges { get => (IEnumerable?)GetValue(SceneChangesProperty); set => SetValue(SceneChangesProperty, value); }
    /// <summary>字幕（SubtitleTimelineItem: 元動画上の区間に写したもの）。</summary>
    public IEnumerable? Subtitles { get => (IEnumerable?)GetValue(SubtitlesProperty); set => SetValue(SubtitlesProperty, value); }
    /// <summary>図形のまとまり（ShapeTimelineItem: 元動画上の区間に写したもの。v1.6）。</summary>
    public IEnumerable? Shapes { get => (IEnumerable?)GetValue(ShapesProperty); set => SetValue(ShapesProperty, value); }
    /// <summary>字幕編集中でカットがロックされているか。</summary>
    public bool CutLocked { get => (bool)GetValue(CutLockedProperty); set => SetValue(CutLockedProperty, value); }
    public double? MarkIn { get => (double?)GetValue(MarkInProperty); set => SetValue(MarkInProperty, value); }
    public double? MarkOut { get => (double?)GetValue(MarkOutProperty); set => SetValue(MarkOutProperty, value); }
    /// <summary>編集のたびに ViewModel が増やす値。コレクションの中身の変化で再描画させる。</summary>
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    /// <summary>一覧で選択中の確定カットの Id（枠線で強調する）。</summary>
    public IEnumerable? SelectedCutIds { get => (IEnumerable?)GetValue(SelectedCutIdsProperty); set => SetValue(SelectedCutIdsProperty, value); }
    /// <summary>一覧で選択中の候補の Id（枠線で強調する）。</summary>
    public IEnumerable? SelectedCandidateIds { get => (IEnumerable?)GetValue(SelectedCandidateIdsProperty); set => SetValue(SelectedCandidateIdsProperty, value); }
    /// <summary>一覧で選択中の字幕の Id（枠線で強調する）。</summary>
    public IEnumerable? SelectedSubtitleIds { get => (IEnumerable?)GetValue(SelectedSubtitleIdsProperty); set => SetValue(SelectedSubtitleIdsProperty, value); }
    /// <summary>一覧で選択中の図形の Id（枠線で強調する）。</summary>
    public IEnumerable? SelectedShapeIds { get => (IEnumerable?)GetValue(SelectedShapeIdsProperty); set => SetValue(SelectedShapeIdsProperty, value); }
    /// <summary>マウス位置の時刻と要素の説明（下部の表示用）。</summary>
    public string HoverText { get => (string)GetValue(HoverTextProperty); set => SetValue(HoverTextProperty, value); }
    /// <summary>録画時の操作の記録（クリック・キー入力・マウス移動の範囲・不要マーカー。時刻は元動画の秒）。</summary>
    public RecordingEvents? RecordingEvents { get => (RecordingEvents?)GetValue(RecordingEventsProperty); set => SetValue(RecordingEventsProperty, value); }

    /// <summary>クリック・ドラッグでシーク要求。</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>確定カットの端をドラッグして離した（v1.21。範囲が変わったときだけ）。</summary>
    public event EventHandler<CutEdgeDraggedEventArgs>? CutEdgeDragged;

    /// <summary>カットの端をドラッグ中か。</summary>
    public bool IsEdgeDragging => _edgeDrag is not null;

    /// <summary>確定カット（赤・橙）または候補（青・紫）をクリックした。Ctrl+クリックは選択への追加・解除。</summary>
    public event EventHandler<TimelineItemClickedEventArgs>? ItemClicked;

    private static DependencyProperty Reg<T>(string name, T defaultValue)
        => DependencyProperty.Register(name, typeof(T), typeof(TimelineControl),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var t = (TimelineControl)d;
        if (e.Property == DurationProperty) t.ResetZoom();
        else if (e.Property == CurrentTimeProperty) t.FollowPlayhead();
        else if (e.Property == SelectedCutIdsProperty || e.Property == SelectedCandidateIdsProperty
                 || e.Property == SelectedSubtitleIdsProperty || e.Property == SelectedShapeIdsProperty) t.EnsureSelectionVisible();
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // ---------------- 座標変換・ズーム ----------------

    private double ViewLength => _viewLength > 0 ? _viewLength : Math.Max(Duration, 0.001);
    private double TrackWidth => Math.Max(1, ActualWidth - SidePadding * 2);
    private double X(double t) => SidePadding + (t - _viewStart) / ViewLength * TrackWidth;
    private double T(double x) => Math.Clamp(_viewStart + (x - SidePadding) / TrackWidth * ViewLength, 0, Math.Max(0, Duration));

    public void ResetZoom()
    {
        _viewStart = 0;
        _viewLength = Duration;
        InvalidateVisual();
    }

    private void FollowPlayhead()
    {
        if (_viewLength <= 0 || _viewLength >= Duration) return;
        var t = CurrentTime;
        if (t < _viewStart || t > _viewStart + _viewLength)
        {
            _viewStart = Math.Clamp(t - _viewLength * 0.1, 0, Math.Max(0, Duration - _viewLength));
            InvalidateVisual();
        }
    }

    /// <summary>選択中の項目が拡大表示の範囲の外なら、見える位置へ寄せる（再生位置は動かさない）。</summary>
    private void EnsureSelectionVisible()
    {
        if (_viewLength <= 0 || _viewLength >= Duration) return;
        var ranges = new List<(double Start, double End)>();
        var cutIds = Ids(SelectedCutIds);
        var candIds = Ids(SelectedCandidateIds);
        ranges.AddRange(Items<CutSegment>(Segments).Where(s => cutIds.Contains(s.Id)).Select(s => (s.Start, s.End)));
        ranges.AddRange(Items<CutCandidate>(Candidates).Where(c => candIds.Contains(c.Id)).Select(c => (c.Start, c.End)));
        var subIds = Ids(SelectedSubtitleIds);
        var shapeIds = Ids(SelectedShapeIds);
        foreach (var sub in Items<ViewModels.SubtitleTimelineItem>(Subtitles).Where(x => subIds.Contains(x.Id))) ranges.AddRange(sub.SourceRanges.Select(r => (r.Start, r.End)));
        foreach (var shape in Items<ViewModels.ShapeTimelineItem>(Shapes).Where(x => shapeIds.Contains(x.Id))) ranges.AddRange(shape.SourceRanges.Select(r => (r.Start, r.End)));
        if (ranges.Count == 0) return;
        var first = ranges.OrderBy(r => r.Start).First();
        var viewEnd = _viewStart + _viewLength;
        if (first.End >= _viewStart && first.Start <= viewEnd) return;
        _viewStart = Math.Clamp(first.Start - _viewLength * 0.1, 0, Math.Max(0, Duration - _viewLength));
        InvalidateVisual();
    }

    private static HashSet<string> Ids(IEnumerable? source) => source?.OfType<string>().ToHashSet() ?? [];

    /// <summary>クリック位置の確定カット・候補。重なる場合は短いものを先にし、選択中のものがあれば次のもの（繰り返しクリックで切り替え）。</summary>
    private (ViewModels.ListKind Kind, string Id)? HitItem(double t, double y)
    {
        // 端のわずかなずれでも選べるよう、2px 分の時間を許容する。
        var tol = 2 / TrackWidth * ViewLength;
        if (y >= CandidateTop && y < TrackTop)
        {
            var selected = Ids(SelectedCandidateIds);
            var hits = Items<CutCandidate>(Candidates).Where(c => c.Status != CandidateStatus.Accepted && c.Start - tol <= t && t <= c.End + tol)
                .OrderBy(c => c.Duration).ThenBy(c => c.Start).ToList();
            if (hits.Count == 0) return null;
            var pick = hits.FirstOrDefault(c => !selected.Contains(c.Id)) ?? hits[0];
            if (hits.Count > 1 && selected.Count == 1 && selected.Contains(hits[0].Id)) pick = hits[1];
            return (ViewModels.ListKind.Candidates, pick.Id);
        }
        if (y >= SubtitleTop && y < SubtitleTop + SubtitleHeight)
        {
            var selected = Ids(SelectedSubtitleIds);
            var hits = Items<ViewModels.SubtitleTimelineItem>(Subtitles).Where(x => x.SourceRanges.Any(r => r.Start - tol <= t && t <= r.End + tol))
                .OrderBy(x => x.SourceRanges.Sum(r => r.Duration)).ToList();
            if (hits.Count == 0) return null;
            var pick = hits.FirstOrDefault(x => !selected.Contains(x.Id)) ?? hits[0];
            if (hits.Count > 1 && selected.Count == 1 && selected.Contains(hits[0].Id)) pick = hits[1];
            return (ViewModels.ListKind.Subtitles, pick.Id);
        }
        if (y >= ShapeTop && y < ShapeTop + ShapeHeight)
        {
            var selected = Ids(SelectedShapeIds);
            var hits = Items<ViewModels.ShapeTimelineItem>(Shapes).Where(x => x.SourceRanges.Any(r => r.Start - tol <= t && t <= r.End + tol))
                .OrderBy(x => x.SourceRanges.Sum(r => r.Duration)).ToList();
            if (hits.Count == 0) return null;
            var pick = hits.FirstOrDefault(x => !selected.Contains(x.Id)) ?? hits[0];
            if (hits.Count > 1 && selected.Count == 1 && selected.Contains(hits[0].Id)) pick = hits[1];
            return (ViewModels.ListKind.Shapes, pick.Id);
        }
        if (y >= TrackTop && y < TrackTop + TrackHeight)
        {
            var selected = Ids(SelectedCutIds);
            var hits = Items<CutSegment>(Segments).Where(s => s.Start - tol <= t && t <= s.End + tol).OrderBy(s => s.Duration).ThenBy(s => s.Start).ToList();
            if (hits.Count == 0) return null;
            var pick = hits.FirstOrDefault(s => !selected.Contains(s.Id)) ?? hits[0];
            if (hits.Count > 1 && selected.Count == 1 && selected.Contains(hits[0].Id)) pick = hits[1];
            return (ViewModels.ListKind.Cuts, pick.Id);
        }
        return null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Duration <= 0) return;
        var pos = e.GetPosition(this).X;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var anchor = T(pos);
            var factor = e.Delta > 0 ? 0.7 : 1 / 0.7;
            var newLen = Math.Clamp(ViewLength * factor, Math.Min(2.0, Duration), Duration);
            _viewStart = Math.Clamp(anchor - (pos - SidePadding) / TrackWidth * newLen, 0, Math.Max(0, Duration - newLen));
            _viewLength = newLen;
        }
        else if (ViewLength < Duration)
        {
            _viewStart = Math.Clamp(_viewStart - Math.Sign(e.Delta) * ViewLength * 0.15, 0, Math.Max(0, Duration - ViewLength));
        }
        else return;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        if (e.ClickCount == 2) { ResetZoom(); return; }
        var pos = e.GetPosition(this);
        // 確定カットの端（±4px）で押したら、シークではなく端のドラッグを始める（v1.21。字幕編集中は無効）。
        if (EdgeAt(pos) is { } edgeHit)
        {
            _edgeDrag = new EdgeDragState(edgeHit.Cut, edgeHit.Edge, CurrentTime);
            CaptureMouse();
            ItemClicked?.Invoke(this, new TimelineItemClickedEventArgs(ViewModels.ListKind.Cuts, edgeHit.Cut.Id, false));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        var time = T(pos.X);
        SeekRequested?.Invoke(this, time);
        if (HitItem(time, pos.Y) is { } hit)
            ItemClicked?.Invoke(this, new TimelineItemClickedEventArgs(hit.Kind, hit.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Duration <= 0) return;
        var p = e.GetPosition(this);
        var t = T(p.X);
        if (_edgeDrag is { } drag)
        {
            if (e.LeftButton == MouseButtonState.Pressed) UpdateEdgeDrag(drag, t);
            HoverText = $"{TimeFormat.Format(drag.Edge == CutEdge.Start ? drag.Start : drag.End)}　{Loc.Format("Timeline.EdgeDrag.Hover", TimeFormat.FormatShort(drag.Start), TimeFormat.FormatShort(drag.End))}";
            return;
        }
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) SeekRequested?.Invoke(this, t);
        Cursor = !_dragging && EdgeAt(p) is not null ? Cursors.SizeWE : Cursors.Hand;
        HoverText = Describe(t, p.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        if (_edgeDrag is { } drag)
        {
            _edgeDrag = null;
            ReleaseMouseCapture();
            InvalidateVisual();
            if (Math.Abs(drag.Start - drag.Cut.Start) > 1e-9 || Math.Abs(drag.End - drag.Cut.End) > 1e-9)
                CutEdgeDragged?.Invoke(this, new CutEdgeDraggedEventArgs(drag.Cut.Id, drag.Start, drag.End));
            return;
        }
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // 別の画面へ切り替わったなどでマウスを離さないまま終わったら、端のドラッグは取り消す。
        if (_edgeDrag is not null) CancelEdgeDrag();
        _dragging = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_edgeDrag is null) HoverText = "";
    }

    /// <summary>端のドラッグを取り消す（Esc。主画面がキーを受けて呼ぶ）。ドラッグ中でなければ false。</summary>
    public bool CancelEdgeDrag()
    {
        if (_edgeDrag is not { } drag) return false;
        _edgeDrag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        // 画面は元の端の位置へ戻す。
        SeekRequested?.Invoke(this, drag.Edge == CutEdge.Start ? drag.Cut.Start : drag.Cut.End);
        HoverText = Loc.Get("Timeline.EdgeDrag.Cancelled");
        return true;
    }

    /// <summary>マウス位置が確定カットの端（±4px）なら、そのカットと端。カットの段の中だけ、字幕編集中は null。</summary>
    private (CutSegment Cut, CutEdge Edge)? EdgeAt(Point p)
    {
        if (CutLocked || Duration <= 0 || p.Y < TrackTop || p.Y >= TrackTop + TrackHeight) return null;
        return TimelineEdgeDrag.HitEdge(Items<CutSegment>(Segments).ToList(), T(p.X), SecondsPerPixel);
    }

    private double SecondsPerPixel => ViewLength / TrackWidth;

    private void UpdateEdgeDrag(EdgeDragState drag, double t)
    {
        var (start, end) = TimelineEdgeDrag.Drag(drag.Cut.Start, drag.Cut.End, drag.Edge, t, Duration, SnapTargets(drag), SecondsPerPixel,
            noSnap: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        drag.Start = start;
        drag.End = end;
        InvalidateVisual();
        SeekRequested?.Invoke(this, drag.Edge == CutEdge.Start ? start : end);
    }

    /// <summary>吸着先: 画面変化の目印・入力の範囲の端・クリック・不要マーカー・ドラッグを始めたときの再生位置。</summary>
    private List<double> SnapTargets(EdgeDragState drag)
    {
        var list = new List<double>(Items<double>(SceneChanges)) { drag.Playhead };
        if (RecordingEvents is { } ev)
        {
            list.AddRange(ev.KeyRanges.SelectMany(r => new[] { r.Start, r.End }));
            list.AddRange(ev.MoveRanges.SelectMany(r => new[] { r.Start, r.End }));
            list.AddRange(ev.Clicks.Select(c => c.T));
            list.AddRange(ev.Markers);
        }
        return list;
    }

    private string Describe(double t, double y)
    {
        var parts = new List<string> { TimeFormat.Format(t) };
        if (RecordingEvents is { } ev)
        {
            // 不要マーカーは縦線が候補の段まで伸びるので、その高さまで。クリック・キー入力は操作の段の中だけ。
            if (y < CandidateTop + CandidateHeight && ev.Markers.Any(m => Math.Abs(X(m) - X(t)) <= 4)) parts.Add(Loc.Get("Timeline.Hover.Marker"));
            if (y >= OpsTop - 1 && y < OpsTop + OpsHeight + 1)
            {
                if (ev.Clicks.Any(c => Math.Abs(X(c.T) - X(t)) <= 3)) parts.Add(Loc.Get("Timeline.Hover.Click"));
                if (ev.KeyRanges.Any(r => r.Start <= t && t <= r.End)) parts.Add(Loc.Get("Timeline.Hover.Key"));
            }
        }
        if (y >= CandidateTop && y < TrackTop)
            foreach (var c in Items<CutCandidate>(Candidates).Where(c => c.Start <= t && t <= c.End && c.Status != CandidateStatus.Accepted))
                parts.Add($"{(c.Source == SegmentSource.Ai ? "AI 候補" : "ローカル候補")}{(c.Status == CandidateStatus.Dismissed ? "（却下）" : "")}: {c.Reason}（{c.Confidence:0%}）");
        foreach (var s in Items<CutSegment>(Segments).Where(s => s.Start <= t && t <= s.End))
            parts.Add(s.Action == SegmentAction.Shorten ? $"確定 短縮（{s.KeepSeconds:0.0#} 秒残す）" : "確定 削除");
        foreach (var sub in Items<ViewModels.SubtitleTimelineItem>(Subtitles).Where(x => x.SourceRanges.Any(r => r.Start <= t && t <= r.End)))
            parts.Add($"字幕: {sub.Text.Replace('\n', ' ')}");
        foreach (var shape in Items<ViewModels.ShapeTimelineItem>(Shapes).Where(x => x.SourceRanges.Any(r => r.Start <= t && t <= r.End)))
            parts.Add($"図形: {shape.Label}");
        if (CutLocked && y >= TrackTop && y < TrackTop + TrackHeight) parts.Add("カット確定済み（字幕編集中は変更できません）");
        var near = Items<CapturePoint>(Captures).Where(c => Math.Abs(X(c.SelectedFrameTime) - X(t)) < 5).Select(c => $"画像 No.{c.Sequence} {c.Caption}");
        parts.AddRange(near);
        return string.Join("　", parts);
    }

    private static IEnumerable<T> Items<T>(IEnumerable? source) => source?.OfType<T>() ?? [];

    // ---------------- 描画 ----------------

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, CaptureTop + CaptureHeight + 4);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));
        if (Duration <= 0)
        {
            DrawText(dc, "動画を開くとタイムラインが表示されます", SidePadding, TrackTop + 8, 12);
            return;
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var viewEnd = _viewStart + ViewLength;

        DrawRuler(dc, viewEnd, dpi);
        if (RecordingEvents is { } events) DrawOperations(dc, events, viewEnd);

        // 編集トラック（通常区間）
        dc.DrawRoundedRectangle(NormalBrush, null, new Rect(X(0), TrackTop, X(Duration) - X(0), TrackHeight), 3, 3);

        // 画面変化（トラック上端の細い目盛り）
        foreach (var s in Items<double>(SceneChanges).Where(s => s >= _viewStart && s <= viewEnd))
            dc.DrawRectangle(SceneBrush, null, new Rect(X(s) - 0.5, TrackTop, 1, 8));

        // 確定区間
        foreach (var s in Items<CutSegment>(Segments))
        {
            if (s.End < _viewStart || s.Start > viewEnd) continue;
            var x1 = X(s.Start);
            var x2 = X(s.End);
            if (s.Action == SegmentAction.Shorten)
            {
                var xk = X(Math.Min(s.End, s.Start + s.KeepSeconds));
                dc.DrawRectangle(ShortenKeepBrush, null, new Rect(x1, TrackTop, Math.Max(1, xk - x1), TrackHeight));
                dc.DrawRectangle(ShortenCutBrush, null, new Rect(xk, TrackTop, Math.Max(1, x2 - xk), TrackHeight));
            }
            else
            {
                dc.DrawRectangle(RemoveBrush, null, new Rect(x1, TrackTop, Math.Max(1, x2 - x1), TrackHeight));
            }
        }

        // 候補（未判断・却下）。採用済みは確定区間として表示されるので描かない。
        foreach (var c in Items<CutCandidate>(Candidates))
        {
            if (c.Status == CandidateStatus.Accepted || c.End < _viewStart || c.Start > viewEnd) continue;
            var brush = c.Status == CandidateStatus.Dismissed ? DismissedBrush : c.Source == SegmentSource.Ai ? AiCandidateBrush : LocalCandidateBrush;
            var r = new Rect(X(c.Start), CandidateTop, Math.Max(2, X(c.End) - X(c.Start)), CandidateHeight);
            dc.PushOpacity(c.Status == CandidateStatus.Dismissed ? 1 : 0.35 + 0.65 * Math.Clamp(c.Confidence, 0, 1));
            dc.DrawRoundedRectangle(brush, null, r, 3, 3);
            dc.Pop();
        }

        // 一覧で選択中のカット・候補を枠線で強調する（ズーム・スクロール中も、見えている範囲に描く）。
        var selCuts = Ids(SelectedCutIds);
        var selCands = Ids(SelectedCandidateIds);
        if (selCuts.Count > 0)
            foreach (var s in Items<CutSegment>(Segments).Where(s => selCuts.Contains(s.Id) && s.End >= _viewStart && s.Start <= viewEnd))
                DrawSelection(dc, new Rect(X(s.Start), TrackTop, Math.Max(3, X(s.End) - X(s.Start)), TrackHeight));
        if (selCands.Count > 0)
            foreach (var c in Items<CutCandidate>(Candidates).Where(c => selCands.Contains(c.Id) && c.End >= _viewStart && c.Start <= viewEnd))
                DrawSelection(dc, new Rect(X(c.Start), CandidateTop, Math.Max(3, X(c.End) - X(c.Start)), CandidateHeight));

        // カットの端のドラッグ中: 元の範囲を通常の色で隠し、仮の範囲を破線の枠で描く（v1.21）。
        if (_edgeDrag is { } drag)
        {
            dc.DrawRectangle(NormalBrush, null, new Rect(X(drag.Cut.Start), TrackTop, Math.Max(1, X(drag.Cut.End) - X(drag.Cut.Start)), TrackHeight));
            var r = new Rect(X(drag.Start), TrackTop, Math.Max(2, X(drag.End) - X(drag.Start)), TrackHeight);
            dc.DrawRectangle(EdgeDragBrush, EdgeDragPen, r);
            var ex = drag.Edge == CutEdge.Start ? r.Left : r.Right;
            dc.DrawLine(EdgeLinePen, new Point(ex, TrackTop - 3), new Point(ex, TrackTop + TrackHeight + 3));
        }

        // 不要マーカー（橙の旗。操作の段の上から候補の段まで縦線。v1.21）
        if (RecordingEvents is { } ev)
            foreach (var m in ev.Markers.Where(m => m >= _viewStart && m <= viewEnd))
                DrawMarker(dc, X(m));

        // カット範囲の指定中（開始・終了）
        if (MarkIn is not null || MarkOut is not null)
        {
            var a = MarkIn ?? MarkOut!.Value;
            var b = MarkOut ?? a;
            if (MarkIn is not null && MarkOut is not null && b > a)
                dc.DrawRectangle(MarkBrush, null, new Rect(X(a), CandidateTop, X(b) - X(a), TrackTop + TrackHeight - CandidateTop));
            if (MarkIn is { } i) dc.DrawLine(MarkPen, new Point(X(i), CandidateTop), new Point(X(i), TrackTop + TrackHeight));
            if (MarkOut is { } o) dc.DrawLine(MarkPen, new Point(X(o), CandidateTop), new Point(X(o), TrackTop + TrackHeight));
        }

        // 字幕編集中はカットのトラックをロック表示にする。
        if (CutLocked)
        {
            var trackRect = new Rect(X(0), TrackTop, X(Duration) - X(0), TrackHeight);
            dc.PushClip(new RectangleGeometry(trackRect));
            dc.DrawRectangle(LockBrush, null, trackRect);
            for (double hx = trackRect.Left - TrackHeight; hx < trackRect.Right; hx += 10)
                dc.DrawLine(LockHatchPen, new Point(hx, TrackTop + TrackHeight), new Point(hx + TrackHeight, TrackTop));
            dc.Pop();
            DrawText(dc, "🔒 カット確定済み（字幕編集中）", SidePadding + 4, TrackTop + 2, 10, Brushes.White);
        }

        // 字幕トラック（編集後の時刻の字幕を元動画の位置へ写したもの。削除区間をまたぐと分かれて表示される）
        dc.DrawRectangle(SubtitleLaneBrush, null, new Rect(X(0), SubtitleTop, X(Duration) - X(0), SubtitleHeight));
        foreach (var sub in Items<ViewModels.SubtitleTimelineItem>(Subtitles))
        {
            bool first = true;
            foreach (var r in sub.SourceRanges)
            {
                if (r.End < _viewStart || r.Start > viewEnd) { first = false; continue; }
                var rect = new Rect(X(r.Start), SubtitleTop + 1, Math.Max(2, X(r.End) - X(r.Start)), SubtitleHeight - 2);
                dc.DrawRoundedRectangle(SubtitleBrush, null, rect, 3, 3);
                if (first && rect.Width > 24)
                {
                    dc.PushClip(new RectangleGeometry(rect));
                    DrawText(dc, sub.Text.Replace('\n', ' '), rect.Left + 3, SubtitleTop + 2, 10, Brushes.White);
                    dc.Pop();
                }
                first = false;
            }
        }

        // 図形トラック（v1.6。字幕と同じく元動画の位置へ写す。まとまりどうしは重なることがあるため半透明で重ねる）
        dc.DrawRectangle(SubtitleLaneBrush, null, new Rect(X(0), ShapeTop, X(Duration) - X(0), ShapeHeight));
        foreach (var shape in Items<ViewModels.ShapeTimelineItem>(Shapes))
        {
            bool first = true;
            foreach (var r in shape.SourceRanges)
            {
                if (r.End < _viewStart || r.Start > viewEnd) { first = false; continue; }
                var rect = new Rect(X(r.Start), ShapeTop + 1, Math.Max(2, X(r.End) - X(r.Start)), ShapeHeight - 2);
                dc.PushOpacity(0.85);
                dc.DrawRoundedRectangle(ShapeBrush, null, rect, 3, 3);
                dc.Pop();
                dc.PushClip(new RectangleGeometry(rect));
                if (shape.HasMosaic)
                    for (double hx = rect.Left - rect.Height; hx < rect.Right; hx += 7)
                        dc.DrawLine(ShapeMosaicHatchPen, new Point(hx, rect.Bottom), new Point(hx + rect.Height, rect.Top));
                if (first && rect.Width > 24) DrawText(dc, shape.Label, rect.Left + 3, ShapeTop + 1, 10, Brushes.White);
                dc.Pop();
                first = false;
            }
        }

        // 一覧で選択中の字幕・図形を枠線で強調する。
        var selSubs = Ids(SelectedSubtitleIds);
        var selShapes = Ids(SelectedShapeIds);
        if (selSubs.Count > 0)
            foreach (var sub in Items<ViewModels.SubtitleTimelineItem>(Subtitles).Where(x => selSubs.Contains(x.Id)))
                foreach (var r in sub.SourceRanges.Where(r => r.End >= _viewStart && r.Start <= viewEnd))
                    DrawSelection(dc, new Rect(X(r.Start), SubtitleTop + 1, Math.Max(3, X(r.End) - X(r.Start)), SubtitleHeight - 2));
        if (selShapes.Count > 0)
            foreach (var shape in Items<ViewModels.ShapeTimelineItem>(Shapes).Where(x => selShapes.Contains(x.Id)))
                foreach (var r in shape.SourceRanges.Where(r => r.End >= _viewStart && r.Start <= viewEnd))
                    DrawSelection(dc, new Rect(X(r.Start), ShapeTop + 1, Math.Max(3, X(r.End) - X(r.Start)), ShapeHeight - 2));

        // 手順書画像ポイント
        foreach (var p in Items<CapturePoint>(Captures))
        {
            var t = p.SelectedFrameTime;
            if (t < _viewStart || t > viewEnd) continue;
            var x = X(t);
            dc.DrawLine(CapturePen, new Point(x, TrackTop + TrackHeight - 6), new Point(x, CaptureTop + 2));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x - 5, CaptureTop + 2), true, true);
                g.LineTo(new Point(x + 5, CaptureTop + 2), true, false);
                g.LineTo(new Point(x, CaptureTop + 9), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(CaptureBrush, null, geo);
            DrawText(dc, p.Sequence.ToString(CultureInfo.InvariantCulture), x - 4, CaptureTop + 8, 10, CaptureBrush);
        }

        // 再生位置
        var px = X(CurrentTime);
        dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(px - 6, 0), true, true);
            g.LineTo(new Point(px + 6, 0), true, false);
            g.LineTo(new Point(px, 8), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(PlayheadPen.Brush, null, head);

        if (ViewLength < Duration - 0.001)
            DrawText(dc, $"拡大表示中 {TimeFormat.FormatShort(_viewStart)}～{TimeFormat.FormatShort(viewEnd)}（ダブルクリックで全体）", w - 330, CaptureTop + 6, 10);
    }

    /// <summary>
    /// 操作の段（v1.21）: マウス移動の範囲（淡い帯・段の下半分）、キー入力の範囲（細い帯）、クリック（小さな点）。
    /// 見えている範囲の外は描かない。同じ画素に重なる点・帯は 1 つにまとめて描く（長い録画でも描画を軽くする）。
    /// </summary>
    private void DrawOperations(DrawingContext dc, RecordingEvents ev, double viewEnd)
    {
        dc.DrawRectangle(OpsLaneBrush, null, new Rect(X(0), OpsTop, X(Duration) - X(0), OpsHeight));
        foreach (var r in ev.MoveRanges.Where(r => r.End >= _viewStart && r.Start <= viewEnd))
            dc.DrawRectangle(MoveRangeBrush, null, new Rect(X(r.Start), OpsTop + OpsHeight - 3, Math.Max(1, X(r.End) - X(r.Start)), 2));
        foreach (var r in ev.KeyRanges.Where(r => r.End >= _viewStart && r.Start <= viewEnd))
            dc.DrawRectangle(KeyRangeBrush, null, new Rect(X(r.Start), OpsTop + 2, Math.Max(1.5, X(r.End) - X(r.Start)), 3));
        double lastX = double.NegativeInfinity;
        foreach (var c in ev.Clicks)
        {
            if (c.T < _viewStart || c.T > viewEnd) continue;
            var x = X(c.T);
            if (x - lastX < 1) continue;
            lastX = x;
            dc.DrawEllipse(ClickDotBrush, null, new Point(x, OpsTop + OpsHeight / 2 + 1), 2, 2);
        }
    }

    /// <summary>不要マーカーの旗（段の上端に小さな橙の旗、候補の段の下端まで縦線）。</summary>
    private void DrawMarker(DrawingContext dc, double x)
    {
        dc.DrawLine(MarkerPen, new Point(x, OpsTop - 1), new Point(x, CandidateTop + CandidateHeight));
        var flag = new StreamGeometry();
        using (var g = flag.Open())
        {
            g.BeginFigure(new Point(x, OpsTop - 1), true, true);
            g.LineTo(new Point(x + 7, OpsTop + 2), true, false);
            g.LineTo(new Point(x, OpsTop + 5), true, false);
        }
        flag.Freeze();
        dc.DrawGeometry(MarkerBrush, null, flag);
    }

    private static void DrawSelection(DrawingContext dc, Rect r)
    {
        r.Inflate(1.5, 1.5);
        dc.DrawRoundedRectangle(null, SelectOuterPen, r, 3, 3);
        dc.DrawRoundedRectangle(null, SelectInnerPen, r, 3, 3);
    }

    private void DrawRuler(DrawingContext dc, double viewEnd, double dpi)
    {
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        var step = steps.FirstOrDefault(s => s / ViewLength * TrackWidth >= 70, 3600);
        var first = Math.Ceiling(_viewStart / step) * step;
        for (var t = first; t <= viewEnd + 1e-9; t += step)
        {
            var x = X(t);
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            var label = step < 1 ? TimeFormat.FormatShort(t)[..^2] : TimeFormat.FormatShort(t)[..^4];
            // 右端で切れる目盛り文字は左側に寄せる。
            DrawText(dc, label, x + 40 > ActualWidth ? x - 36 : x + 2, 1, 10);
        }
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush? brush = null)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush ?? TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }
}

/// <summary>確定カットの端をドラッグして離したときの新しい範囲（元動画の秒）。</summary>
public sealed class CutEdgeDraggedEventArgs(string id, double start, double end) : EventArgs
{
    public string Id { get; } = id;
    public double Start { get; } = start;
    public double End { get; } = end;
}

/// <summary>
/// カットの端のドラッグ（v1.21。設計 3.3）の純粋な計算。画面に依存しないので単体で試験する。
/// 端の当たり判定は ±<see cref="EdgeTolerancePx"/>px、吸着は ±<see cref="SnapTolerancePx"/>px、長さは <see cref="CutEdgeTarget.MinLength"/> 秒以上。
/// </summary>
public static class TimelineEdgeDrag
{
    public const double EdgeTolerancePx = 4;
    public const double SnapTolerancePx = 5;

    /// <summary>
    /// time（元動画の秒）が確定カットの開始・終了の ±tolerancePx px（secondsPerPixel で秒に直す）に入れば、最も近い端。
    /// 同じ距離なら、カットの内側にある方（開始なら time ≥ 開始、終了なら time ≤ 終了）を選ぶ（隣り合うカットの境目で、押した側のカットを動かす）。
    /// </summary>
    public static (CutSegment Cut, CutEdge Edge)? HitEdge(IReadOnlyList<CutSegment> cuts, double time, double secondsPerPixel, double tolerancePx = EdgeTolerancePx)
    {
        if (!(secondsPerPixel > 0)) return null;
        var tol = tolerancePx * secondsPerPixel;
        (CutSegment Cut, CutEdge Edge)? best = null;
        double bestScore = double.MaxValue;
        foreach (var c in cuts)
        {
            Consider(c, CutEdge.Start, c.Start, time >= c.Start);
            Consider(c, CutEdge.End, c.End, time <= c.End);
        }
        return best;

        void Consider(CutSegment c, CutEdge edge, double at, bool inside)
        {
            var d = Math.Abs(time - at);
            if (d > tol + 1e-12) return;
            // 内側の端を少しだけ優先する（同じ距離のときだけ効く）。
            var score = d + (inside ? 0 : 1e-9);
            if (score < bestScore)
            {
                bestScore = score;
                best = (c, edge);
            }
        }
    }

    /// <summary>time を、±tolerancePx px 以内で最も近い吸着先へ寄せる。無ければそのまま。</summary>
    public static double Snap(double time, IEnumerable<double> targets, double secondsPerPixel, double tolerancePx = SnapTolerancePx)
    {
        if (!(secondsPerPixel > 0)) return time;
        var tol = tolerancePx * secondsPerPixel;
        double best = time, bestD = double.MaxValue;
        foreach (var s in targets)
        {
            if (!double.IsFinite(s)) continue;
            var d = Math.Abs(s - time);
            if (d <= tol + 1e-12 && d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best;
    }

    /// <summary>
    /// ドラッグ中のマウス位置 time に対する新しい範囲。noSnap（Shift）でなければ吸着先へ寄せてから <see cref="Resize"/> する。
    /// </summary>
    public static (double Start, double End) Drag(double start, double end, CutEdge edge, double time, double duration,
        IEnumerable<double> snapTargets, double secondsPerPixel, bool noSnap)
        => Resize(start, end, edge, noSnap ? time : Snap(time, snapTargets, secondsPerPixel), duration);

    /// <summary>
    /// 端を time に動かした新しい範囲（ミリ秒に丸める）。動かさない側は元のまま、長さは <see cref="CutEdgeTarget.MinLength"/> 秒以上、[0, duration] の中。
    /// duration が 0 以下なら上限を設けない。
    /// </summary>
    public static (double Start, double End) Resize(double start, double end, CutEdge edge, double time, double duration)
    {
        var max = duration > 0 ? duration : double.MaxValue;
        time = Math.Round(time, 3);
        if (edge == CutEdge.Start)
            return (Math.Clamp(time, 0, Math.Max(0, Math.Round(end - CutEdgeTarget.MinLength, 3))), end);
        return (start, Math.Clamp(time, Math.Min(max, Math.Round(start + CutEdgeTarget.MinLength, 3)), max));
    }
}

/// <summary>タイムラインで確定カット・候補をクリックしたときの内容。</summary>
public sealed class TimelineItemClickedEventArgs(ViewModels.ListKind kind, string id, bool additive) : EventArgs
{
    public ViewModels.ListKind Kind { get; } = kind;
    public string Id { get; } = id;
    /// <summary>Ctrl+クリック（選択への追加・解除）。</summary>
    public bool Additive { get; } = additive;
}
