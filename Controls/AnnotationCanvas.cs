using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Controls;

/// <summary>
/// 注釈編集の描画面。画像と注釈を画像 px 座標で描き、表示倍率（Fit / 指定倍率）への変換はここだけで行う。
/// マウス操作は画像座標に直して ImageEditorViewModel / AnnotationSession へ渡す。保存画像と同じ描画処理（AnnotationDrawing）を使う。
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    /// <summary>画面上のつまみの大きさ（表示 px）。</summary>
    private const double HandleSize = 9;
    /// <summary>当たり判定の余裕（表示 px）。</summary>
    private const double HitTolerance = 5;
    private const double FitMargin = 16;

    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor), typeof(ImageEditorViewModel),
        typeof(AnnotationCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnEditorChanged));

    private enum DragMode { None, Create, Move, Handle }

    private DragMode _drag;
    private ImagePoint _dragStart;
    private ImagePoint _dragCurrent;
    private AnnotationHandle _handle;
    private string? _dragId;

    private BitmapSource? _mosaicked;
    private string _mosaicKey = "";
    /// <summary>モザイクを焼き込んだ元の画像（コマ送りで背景が替わったら作り直す）。</summary>
    private BitmapSource? _mosaicBase;

    public AnnotationCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public ImageEditorViewModel? Editor
    {
        get => (ImageEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private AnnotationSession? Session => Editor?.Session;

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (AnnotationCanvas)d;
        if (e.OldValue is ImageEditorViewModel old) old.RenderRequested -= c.OnRenderRequested;
        if (e.NewValue is ImageEditorViewModel vm) vm.RenderRequested += c.OnRenderRequested;
        c._mosaicKey = "";
        c.InvalidateVisual();
    }

    private void OnRenderRequested()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ---------------- 座標変換 ----------------

    /// <summary>現在の表示変換（画像 px → 表示座標）。Fit では表示領域に収め、それ以外は指定倍率で中央寄せ。</summary>
    private ViewTransform Transform
    {
        get
        {
            var vm = Editor;
            if (vm is null) return new ViewTransform(1);
            int w = vm.BaseImage.PixelWidth, h = vm.BaseImage.PixelHeight;
            if (vm.IsFit)
            {
                var fit = ViewTransform.Fit(w, h, Math.Max(1, ActualWidth - 2 * FitMargin), Math.Max(1, ActualHeight - 2 * FitMargin));
                return fit with { OffsetX = fit.OffsetX + FitMargin, OffsetY = fit.OffsetY + FitMargin };
            }
            var s = vm.Zoom;
            return new ViewTransform(s, Math.Max(FitMargin, (ActualWidth - w * s) / 2), Math.Max(FitMargin, (ActualHeight - h * s) / 2));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var vm = Editor;
        if (vm is null || vm.IsFit)
            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        return new Size(vm.BaseImage.PixelWidth * vm.Zoom + 2 * FitMargin, vm.BaseImage.PixelHeight * vm.Zoom + 2 * FitMargin);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateFitZoom();
    }

    private void UpdateFitZoom()
    {
        if (Editor is { IsFit: true } vm) vm.Zoom = Transform.Scale;
    }

    private ImagePoint ToImage(Point p) => Transform.ToImage(p.X, p.Y);

    // ---------------- 描画 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var vm = Editor;
        var session = Session;
        if (vm is null || session is null) return;
        UpdateFitZoom();
        var t = Transform;
        int w = session.ImageWidth, h = session.ImageHeight;

        dc.PushTransform(new MatrixTransform(t.Scale, 0, 0, t.Scale, t.OffsetX, t.OffsetY));
        dc.DrawImage(MosaickedImage(vm), new Rect(0, 0, w, h));
        AnnotationDrawing.DrawVectors(dc, session.Items.Where(a => a is not MosaicAnnotation), session.StyleScale);
        if (_drag == DragMode.Create && vm.CreateShape(_dragStart, _dragCurrent) is { } preview)
        {
            if (preview is MosaicAnnotation m) DrawMosaicPreview(dc, m, t.Scale);
            else AnnotationDrawing.Draw(dc, preview, session.StyleScale);
        }
        dc.Pop();

        if (session.Selected is { } sel) DrawSelection(dc, sel, t, session.StyleScale);
    }

    /// <summary>モザイクを焼き込んだ画像（モザイクが変わったときだけ作り直す）。表示も保存画像と同じ画素になる。</summary>
    private BitmapSource MosaickedImage(ImageEditorViewModel vm)
    {
        var mosaics = vm.Session.Items.OfType<MosaicAnnotation>().ToList();
        var key = JsonSerializer.Serialize(mosaics, JsonDefaults.Options);
        if (_mosaicked is null || key != _mosaicKey || !ReferenceEquals(vm.BaseImage, _mosaicBase))
        {
            _mosaicked = mosaics.Count == 0 ? vm.BaseImage : AnnotationRenderer.ApplyMosaics(vm.BaseImage, mosaics);
            _mosaicKey = key;
            _mosaicBase = vm.BaseImage;
        }
        return _mosaicked;
    }

    private static void DrawMosaicPreview(DrawingContext dc, MosaicAnnotation m, double scale)
    {
        var pen = new Pen(Brushes.White, 1.5 / scale) { DashStyle = DashStyles.Dash };
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)), pen, new Rect(m.X, m.Y, m.Width, m.Height));
    }

    private void DrawSelection(DrawingContext dc, Annotation a, ViewTransform t, double styleScale)
    {
        var b = AnnotationGeometry.Bounds(a, styleScale, MeasureText);
        var (x1, y1) = t.ToView(new ImagePoint(b.X, b.Y));
        var (x2, y2) = t.ToView(new ImagePoint(b.Right, b.Bottom));
        var rect = new Rect(new Point(x1, y1), new Point(x2, y2));
        rect.Inflate(3, 3);
        // 白と青の二重線で、明るい画面でも暗い画面でも見えるようにする。
        dc.DrawRectangle(null, new Pen(Brushes.White, 3), rect);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), 1.5) { DashStyle = DashStyles.Dash }, rect);

        foreach (var (_, p) in AnnotationGeometry.Handles(a))
        {
            var (hx, hy) = t.ToView(p);
            var r = new Rect(hx - HandleSize / 2, hy - HandleSize / 2, HandleSize, HandleSize);
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), 1.5), r);
        }
    }

    private (double Width, double Height) MeasureText(TextAnnotation t, double styleScale) => AnnotationDrawing.MeasureText(t, styleScale);

    // ---------------- マウス操作 ----------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var vm = Editor;
        var session = Session;
        if (vm is null || session is null) return;
        Focus();
        var p = ToImage(e.GetPosition(this));
        var scale = Transform.Scale;
        var tol = HitTolerance / scale;

        // 選択中の注釈のつまみ（どのツールでも変形できる）
        if (session.Selected is { } sel && AnnotationGeometry.HitHandle(sel, p, (HandleSize / 2 + 2) / scale) is var handle and not AnnotationHandle.None)
        {
            StartDrag(DragMode.Handle, p, sel.Id, handle, "注釈の変形");
            e.Handled = true;
            return;
        }

        if (vm.Tool == EditorTool.Select)
        {
            var hit = AnnotationGeometry.HitTestTopmost(session.Items, p, tol, session.StyleScale, MeasureText);
            session.Select(hit?.Id);
            if (hit is null) return;
            vm.SyncStyleFromSelection();
            if (e.ClickCount == 2 && hit is TextAnnotation)
            {
                vm.EditTextCommand.Execute(null);
                e.Handled = true;
                return;
            }
            StartDrag(DragMode.Move, p, hit.Id, AnnotationHandle.None, "注釈の移動");
            e.Handled = true;
            return;
        }

        if (vm.Tool == EditorTool.Text)
        {
            // 既存の文字をクリックしたら編集、何も無い場所なら追加
            if (AnnotationGeometry.HitTestTopmost(session.Items, p, tol, session.StyleScale, MeasureText) is TextAnnotation existing)
            {
                session.Select(existing.Id);
                vm.SyncStyleFromSelection();
                vm.EditTextCommand.Execute(null);
            }
            else if (IsInsideImage(p, session))
            {
                vm.AddTextAt(p);
            }
            e.Handled = true;
            return;
        }

        if (!IsInsideImage(p, session)) return;
        session.Select(null);
        _drag = DragMode.Create;
        _dragStart = _dragCurrent = p;
        CaptureMouse();
        e.Handled = true;
    }

    private void StartDrag(DragMode mode, ImagePoint p, string id, AnnotationHandle handle, string label)
    {
        _drag = mode;
        _dragStart = _dragCurrent = p;
        _dragId = id;
        _handle = handle;
        Session!.BeginInteractive(label);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var session = Session;
        if (session is null) return;
        var p = ToImage(e.GetPosition(this));
        UpdateCursor(p);
        if (_drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed) return;
        _dragCurrent = p;
        switch (_drag)
        {
            case DragMode.Create:
                InvalidateVisual();
                break;
            case DragMode.Move when _dragId is { } id && session.InteractiveOriginal(id) is { } original:
            {
                var bounds = AnnotationGeometry.ShapeBounds(original, session.StyleScale, MeasureText);
                var (dx, dy) = AnnotationGeometry.ClampOffset(bounds, p.X - _dragStart.X, p.Y - _dragStart.Y, session.ImageWidth, session.ImageHeight);
                session.UpdateInteractive(id, a =>
                {
                    CopyPosition(original, a);
                    a.Offset(dx, dy);
                });
                break;
            }
            case DragMode.Handle when _dragId is { } id && session.InteractiveOriginal(id) is { } original:
                session.UpdateInteractive(id, a => AnnotationGeometry.DragHandle(original, a, _handle, p, session.ImageWidth, session.ImageHeight));
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var vm = Editor;
        var session = Session;
        var mode = _drag;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (vm is null || session is null) return;
        switch (mode)
        {
            case DragMode.Create:
                vm.CommitShape(_dragStart, ToImage(e.GetPosition(this)));
                break;
            case DragMode.Move:
            case DragMode.Handle:
                if (mode == DragMode.Handle && IsTooSmall(session.Selected))
                    session.CancelInteractive(); // つまみで潰した形は元に戻す
                else
                    session.EndInteractive();
                break;
        }
        _dragId = null;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragMode.None) return;
        // ウィンドウ外での解放・Alt+Tab など。作成中は捨て、移動・変形は確定する。
        if (_drag != DragMode.Create) Session?.EndInteractive();
        _drag = DragMode.None;
        InvalidateVisual();
    }

    /// <summary>Esc: 作成中・ドラッグ中の操作を取り消す。取り消した場合 true。</summary>
    public bool CancelDrag()
    {
        if (_drag == DragMode.None) return false;
        if (_drag != DragMode.Create) Session?.CancelInteractive();
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Editor is not { } vm || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        vm.SetZoom(e.Delta > 0 ? vm.Zoom * 1.25 : vm.Zoom / 1.25);
        e.Handled = true;
    }

    private void UpdateCursor(ImagePoint p)
    {
        var vm = Editor;
        var session = Session;
        if (vm is null || session is null) return;
        var scale = Transform.Scale;
        if (_drag == DragMode.Move) { Cursor = Cursors.SizeAll; return; }
        if (session.Selected is { } sel && AnnotationGeometry.HitHandle(sel, p, (HandleSize / 2 + 2) / scale) is var h and not AnnotationHandle.None)
        {
            Cursor = h is AnnotationHandle.TopLeft or AnnotationHandle.BottomRight ? Cursors.SizeNWSE
                : h is AnnotationHandle.TopRight or AnnotationHandle.BottomLeft ? Cursors.SizeNESW
                : Cursors.Cross;
            return;
        }
        Cursor = vm.Tool switch
        {
            EditorTool.Select => AnnotationGeometry.HitTestTopmost(session.Items, p, HitTolerance / scale, session.StyleScale, MeasureText) is null ? Cursors.Arrow : Cursors.SizeAll,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    private static bool IsInsideImage(ImagePoint p, AnnotationSession s) => p.X >= 0 && p.Y >= 0 && p.X <= s.ImageWidth && p.Y <= s.ImageHeight;

    private static bool IsTooSmall(Annotation? a) => a switch
    {
        BoxAnnotation b => b.Width < AnnotationStyle.MinBoxSize || b.Height < AnnotationStyle.MinBoxSize,
        SegmentAnnotation ar => AnnotationGeometry.DistanceToPoint(new(ar.X1, ar.Y1), new(ar.X2, ar.Y2)) < AnnotationStyle.MinArrowLength,
        _ => false,
    };

    /// <summary>移動中の注釈を、ドラッグ開始時の位置に戻す（移動量は開始点からの差で毎回計算する）。</summary>
    private static void CopyPosition(Annotation from, Annotation to)
    {
        switch (from, to)
        {
            case (BoxAnnotation f, BoxAnnotation t): t.X = f.X; t.Y = f.Y; break;
            case (SegmentAnnotation f, SegmentAnnotation t): t.X1 = f.X1; t.Y1 = f.Y1; t.X2 = f.X2; t.Y2 = f.Y2; break;
            case (TextAnnotation f, TextAnnotation t): t.X = f.X; t.Y = f.Y; break;
        }
    }
}
