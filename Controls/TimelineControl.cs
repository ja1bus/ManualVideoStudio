using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Controls;

/// <summary>
/// 動画全体の時間軸（元動画基準）。上から「目盛り」「候補レーン（ローカル解析=青 / AI=紫）」「カット（編集トラック。通常=灰 / 削除=赤 / 短縮=橙）」
/// 「字幕トラック（青緑。編集後の時刻の字幕を元動画の位置に写して描く）」「図形トラック（赤紫。v1.6。字幕と同じく元動画の位置に写す。
/// モザイクを含むまとまりは斜線付き）」「手順書画像レーン（緑）」を描く。確定編集と候補は別レーンで区別する。
/// 字幕編集中（カット確定済み）はカットのトラックに斜線と「カット確定済み」を重ねる。
/// 操作: クリック・ドラッグでシーク、Ctrl+ホイールで拡大縮小、ホイールで左右移動（拡大時）、ダブルクリックで全体表示。
/// 確定カット・候補をクリックすると、シークに加えて対応する項目を通知する（<see cref="ItemClicked"/>。一覧の行を選ぶ。v1.17.0）。
/// 一覧で選択中のカット・候補は枠線で強調し、拡大表示で画面の外にあれば見える位置へ移動する。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 18;
    private const double CandidateTop = 22, CandidateHeight = 14;
    private const double TrackTop = 40, TrackHeight = 34;
    private const double SubtitleTop = 78, SubtitleHeight = 18;
    private const double ShapeTop = 98, ShapeHeight = 16;
    private const double CaptureTop = 118, CaptureHeight = 20;
    private const double SidePadding = 8;

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

    private double _viewStart;
    private double _viewLength;
    private bool _dragging;

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
    public static readonly DependencyProperty HoverTextProperty = DependencyProperty.Register(nameof(HoverText), typeof(string), typeof(TimelineControl), new PropertyMetadata(""));

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

    /// <summary>クリック・ドラッグでシーク要求。</summary>
    public event EventHandler<double>? SeekRequested;

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
        _dragging = true;
        CaptureMouse();
        var pos = e.GetPosition(this);
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
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) SeekRequested?.Invoke(this, t);
        HoverText = Describe(t, p.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e) => HoverText = "";

    private string Describe(double t, double y)
    {
        var parts = new List<string> { TimeFormat.Format(t) };
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

/// <summary>タイムラインで確定カット・候補をクリックしたときの内容。</summary>
public sealed class TimelineItemClickedEventArgs(ViewModels.ListKind kind, string id, bool additive) : EventArgs
{
    public ViewModels.ListKind Kind { get; } = kind;
    public string Id { get; } = id;
    /// <summary>Ctrl+クリック（選択への追加・解除）。</summary>
    public bool Additive { get; } = additive;
}
