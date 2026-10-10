using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services.Recording;

namespace ManualVideoStudio.Views;

/// <summary>録画中の小さなコントローラー（状態・経過時間・一時停止 / 再開・停止）。画面キャプチャから除外し、録画に写らない。</summary>
public partial class RecordingControllerWindow : Window
{
    public RecordingControllerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_NOACTIVATE);
            ScreenScale.ExcludeFromCapture(this);
        };
    }

    /// <summary>録画範囲の外（下 → 上 → 範囲内の右上の順）に置く。範囲内に置いても録画には写らない。</summary>
    public void PlaceNear(CaptureRegion region, CaptureRegion screen)
    {
        UpdateLayout();
        var s = ScreenScale.Of(this);
        double w = ActualWidth * s, h = ActualHeight * s;
        const double gap = 16;
        double x = Math.Clamp(region.X + region.Width - w, screen.X, Math.Max(screen.X, screen.Right - w));
        double y = region.Bottom + gap + h <= screen.Bottom ? region.Bottom + gap
            : region.Y - gap - h >= screen.Y ? region.Y - gap - h
            : region.Y + gap;
        Left = x / s;
        Top = y / s;
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }
}
