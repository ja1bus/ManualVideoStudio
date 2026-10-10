using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>
/// 録画対象外の領域を暗くする幕（仮想スクリーン全体を覆い、録画範囲だけ穴を空ける）。
/// クリックは透過し（下のアプリを操作できる）、画面キャプチャからも除外する。
/// </summary>
public sealed class DimWindow : Window
{
    private readonly Path _path = new() { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) };

    public DimWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "録画範囲の外側";
        Content = _path;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            NativeMethods.AddExStyle(h, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            ScreenScale.ExcludeFromCapture(this);
        };
    }

    /// <summary>screen（物理 px）全体を覆い、hole を穴にする。dark = false は録画中の薄い表示。</summary>
    public void Update(CaptureRegion screen, CaptureRegion hole, bool dark)
    {
        var s = ScreenScale.Of(this);
        Left = screen.X / s;
        Top = screen.Y / s;
        Width = screen.Width / s;
        Height = screen.Height / s;
        var outer = new RectangleGeometry(new Rect(0, 0, screen.Width / s, screen.Height / s));
        var inner = new RectangleGeometry(new Rect((hole.X - screen.X) / s, (hole.Y - screen.Y) / s, hole.Width / s, hole.Height / s));
        _path.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        _path.Opacity = dark ? 1.0 : 0.45;
    }
}

/// <summary>
/// 録画範囲の赤い枠。枠は範囲の外側に描く（録画に写らない。さらに画面キャプチャからも除外）。
/// 範囲指定モードの録画前は、上のつまみ・中央の「✥」で移動（大きさは変えない）、辺・角のつまみで大きさを変えられる。
/// 中央のつまみ以外の内側は透明でクリックは下のアプリへ届く。上のつまみが画面の外に出る位置では、つまみを枠の内側の上端に置く。
/// 位置・大きさは物理 px で扱い（PointToScreen の値）、表示のときだけ DIP に換算する。
/// 大きさの計算は ViewModel（RecorderViewModel.ResizeRegionByDrag / MoveRegionByDrag）と Core の RegionAspect が行い、
/// 規格サイズの選択中は縦横比を保つ（v1.16）。
/// </summary>
public sealed class RegionFrameWindow : Window
{
    /// <summary>枠の太さ（DIP）。</summary>
    private const double Border = 3;
    /// <summary>つまみ・枠のために範囲の外側に取る余白（DIP）。</summary>
    private const double Pad = 8;
    /// <summary>上のつまみ（移動・サイズ表示）の高さ（DIP）。</summary>
    private const double TopBar = 26;

    private static readonly Brush FrameBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x2B, 0x2B));
    private readonly RecorderViewModel _vm;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _frame = new() { Stroke = FrameBrush, StrokeThickness = Border };
    private readonly Border _bar;
    private readonly Border _centerGrip;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(FrameworkElement Handle, int Dx, int Dy)> _handles = [];
    private CaptureRegion _region;
    private bool _interactive;
    // ドラッグ中の状態（物理 px）
    private Point _dragStart;
    private CaptureRegion _dragRegion;
    private (int Dx, int Dy, bool Move)? _drag;

    public RegionFrameWindow(RecorderViewModel vm)
    {
        _vm = vm;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "録画範囲";
        Content = _canvas;
        _canvas.Children.Add(_frame);

        var grip = new TextBlock { Text = "✥ 移動", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _bar = new Border
        {
            Background = FrameBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Height = TopBar - 2,
            Cursor = Cursors.SizeAll,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { grip, _sizeText } },
            ToolTip = "ドラッグして録画範囲を移動",
        };
        AutomationProperties_SetName(_bar, "録画範囲の移動");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_sizeText, "Recorder.FrameLabel");
        _bar.MouseLeftButtonDown += (_, e) => BeginDrag(e, 0, 0, move: true);
        _canvas.Children.Add(_bar);

        // 中央の移動つまみ（v1.4）。枠の大きさを変えずに動かす。つまみ以外の内側はクリックが下のアプリへ届く。
        _centerGrip = new Border
        {
            Width = 44, Height = 44,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xE5, 0x2B, 0x2B)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(22),
            Cursor = Cursors.SizeAll,
            Child = new TextBlock { Text = "✥", Foreground = Brushes.White, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "ドラッグして録画範囲を移動（大きさは変わりません）",
        };
        AutomationProperties_SetName(_centerGrip, "録画範囲の移動（中央）");
        _centerGrip.MouseLeftButtonDown += (_, e) => BeginDrag(e, 0, 0, move: true);
        _canvas.Children.Add(_centerGrip);

        // 8 方向のつまみ（Dx, Dy: -1 = 左 / 上の辺、1 = 右 / 下の辺、0 = 動かさない）
        foreach (var (dx, dy) in new[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) })
        {
            var h = new Rectangle
            {
                Width = 10, Height = 10, Fill = Brushes.White, Stroke = FrameBrush, StrokeThickness = 2,
                Cursor = (dx, dy) switch
                {
                    (0, _) => Cursors.SizeNS,
                    (_, 0) => Cursors.SizeWE,
                    _ when dx == dy => Cursors.SizeNWSE,
                    _ => Cursors.SizeNESW,
                },
            };
            int cdx = dx, cdy = dy;
            h.MouseLeftButtonDown += (_, e) => BeginDrag(e, cdx, cdy, move: false);
            _handles.Add((h, dx, dy));
            _canvas.Children.Add(h);
        }
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => _drag = null;
        SourceInitialized += (_, _) =>
        {
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            ScreenScale.ExcludeFromCapture(this);
        };
    }

    private static void AutomationProperties_SetName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

    /// <summary>枠の位置（物理 px）と操作の可否を更新する。</summary>
    public void Update(CaptureRegion region, bool interactive, bool recording)
    {
        _region = region;
        _interactive = interactive;
        var s = ScreenScale.Of(this);
        double w = region.Width / s, h = region.Height / s;
        Left = region.X / s - Pad;
        Top = region.Y / s - Pad - TopBar;
        Width = w + Pad * 2;
        Height = h + Pad * 2 + TopBar;

        double ix = Pad, iy = Pad + TopBar;
        // 上のつまみが画面（仮想スクリーン）の外に出るなら、枠の内側の上端に置く（v1.4）。
        bool barInside = (region.Y - (Pad + TopBar) * s) < _vm.VirtualScreen.Y;
        Canvas.SetLeft(_frame, ix - Border);
        Canvas.SetTop(_frame, iy - Border);
        _frame.Width = w + Border * 2;
        _frame.Height = h + Border * 2;
        _frame.StrokeDashArray = recording ? null : new DoubleCollection { 4, 2 };

        // 固定中は「1280×720・横16:9」、自由サイズは「1280×720・自由サイズ」（v1.16）。録画中は実際に録画する範囲の大きさ。
        _sizeText.Text = recording || !interactive ? $"{region.Width} x {region.Height}" : _vm.FrameLabel;
        _bar.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_bar, ix - Border);
        Canvas.SetTop(_bar, barInside ? iy : iy - Border - _bar.Height);
        _bar.CornerRadius = barInside ? new CornerRadius(0, 0, 4, 4) : new CornerRadius(4, 4, 0, 0);
        _centerGrip.Visibility = interactive && w >= _centerGrip.Width + 20 && h >= _centerGrip.Height + 20 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_centerGrip, ix + (w - _centerGrip.Width) / 2);
        Canvas.SetTop(_centerGrip, iy + (h - _centerGrip.Height) / 2);
        foreach (var (handle, dx, dy) in _handles)
        {
            handle.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(handle, ix + (dx + 1) / 2.0 * w - handle.Width / 2);
            Canvas.SetTop(handle, iy + (dy + 1) / 2.0 * h - handle.Height / 2);
        }
        // 録画中はクリックを完全に透過させる（枠を誤って掴まないように）。
        IsHitTestVisible = interactive;
    }

    private void BeginDrag(MouseButtonEventArgs e, int dx, int dy, bool move)
    {
        if (!_interactive) return;
        _drag = (dx, dy, move);
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragRegion = _region;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PointToScreen(e.GetPosition(this));
        int mx = (int)Math.Round(p.X - _dragStart.X), my = (int)Math.Round(p.Y - _dragStart.Y);
        // 大きさの計算（自由サイズ / 縦横比の固定）は ViewModel と Core の RegionAspect に任せる（v1.16）。
        if (d.Move) _vm.MoveRegionByDrag(_dragRegion, mx, my);
        else _vm.ResizeRegionByDrag(_dragRegion, d.Dx, d.Dy, mx, my);
    }

    private void EndDrag()
    {
        if (_drag is null) return;
        _drag = null;
        ReleaseMouseCapture();
    }
}

/// <summary>録画開始前のカウントダウン（録画範囲の中央に 3・2・1 を出す。録画には写らない）。Esc またはボタンで取り消し。</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number = new() { FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TaskCompletionSource<bool> _canceled = new();

    public CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 220;
        Height = 230;
        Title = "録画開始のカウントダウン";
        var cancel = new Button { Content = "取り消し (Esc)", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => _canceled.TrySetResult(true);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "録画を開始します", Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
                    _number,
                    cancel,
                },
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _canceled.TrySetResult(true); };
        SourceInitialized += (_, _) => ScreenScale.ExcludeFromCapture(this);
    }

    /// <summary>カウントダウンする。最後まで進めば true、取り消したら false。</summary>
    public async Task<bool> RunAsync(int seconds, CaptureRegion region)
    {
        var s = ScreenScale.Of(this);
        Left = (region.X + region.Width / 2.0) / s - Width / 2;
        Top = (region.Y + region.Height / 2.0) / s - Height / 2;
        Show();
        Activate();
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                _number.Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var done = await Task.WhenAny(Task.Delay(1000), _canceled.Task);
                if (done == _canceled.Task) return false;
            }
            return true;
        }
        finally
        {
            Close();
            // 閉じた直後の画面の再描画が録画の先頭に写らないよう少し待つ。
            await Task.Delay(150);
        }
    }
}
