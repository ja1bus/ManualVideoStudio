using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>
/// 録画画面。View 固有の処理（録画枠・暗幕・カウントダウン・録画中コントローラーの表示、グローバルショートカット）を持つ。
/// 録画中はこの画面を隠し、小さなコントローラーだけを出す。コントローラー・枠・カウントダウンは録画に写らないよう
/// 画面キャプチャから除外する（SetWindowDisplayAffinity）。
/// </summary>
public partial class RecorderWindow : Window
{
    private const int HotkeyStartStop = 0x5301;
    private const int HotkeyPause = 0x5302;
    private const uint VK_F8 = 0x77, VK_F9 = 0x78;

    private readonly RecorderViewModel _vm;
    private readonly RegionFrameWindow _frame;
    private readonly DimWindow _dim;
    private RecordingControllerWindow? _controller;
    private HwndSource? _source;
    private bool _closingConfirmed;

    public RecorderWindow(RecorderViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _frame = new RegionFrameWindow(vm);
        _dim = new DimWindow();
        vm.RecordingUiChanged += (_, _) => UpdateOverlays();
        vm.CountdownHandler = RunCountdownAsync;
        vm.AskStartEditing = AskStartEditing;
        vm.AskAfterStop = AskAfterStop;
        vm.CloseRequested += outcome =>
        {
            Outcome = outcome;
            _closingConfirmed = true;
            Close();
        };
        Loaded += async (_, _) =>
        {
            RegisterHotkeys();
            PlaceAwayFromRegion();
            UpdateOverlays();
            await vm.LoadMicrophonesAsync();
        };
        Closed += (_, _) =>
        {
            UnregisterHotkeys();
            _controller?.Close();
            _frame.Close();
            _dim.Close();
        };
    }

    /// <summary>録画の結果（閉じた後に参照する。中止・閉じるだけなら null）。</summary>
    public RecordingOutcome? Outcome { get; private set; }

    // ---------------- 枠・暗幕・コントローラー ----------------

    private void UpdateOverlays()
    {
        if (!IsLoaded) return;
        if (_vm.IsFinishing)
        {
            // 停止後は完了画面だけを出す（録画画面・枠・暗幕・コントローラーを消し、完了画面の裏で操作されないようにする）。
            if (IsVisible) Hide();
            _frame.Hide();
            _dim.Hide();
            _controller?.Close();
            _controller = null;
            return;
        }
        var busy = _vm.IsBusy;
        var recordingStarted = _vm.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Starting or RecordingState.Stopping;
        CaptureRegion? region = busy ? _vm.RecordingRegion : _vm.PreviewRegion;

        // 録画中は設定画面を隠す（カウントダウン中も隠して、録画対象を見せる）。
        if (busy && IsVisible) Hide();
        else if (!busy && !IsVisible) { Show(); Activate(); }

        if (region is { } r && !r.IsEmpty)
        {
            var screen = _vm.VirtualScreen;
            // モニター全体の録画では枠がモニターの外になるため、暗幕だけで示す。
            bool hideFrame = _vm.IsMonitorMode || (r.Width >= screen.Width && r.Height >= screen.Height);
            _dim.Update(screen, r, dark: !busy);
            if (!_dim.IsVisible) _dim.Show();
            _frame.Update(r, interactive: !busy && _vm.IsRegionMode, recording: recordingStarted);
            if (hideFrame) _frame.Hide();
            else if (!_frame.IsVisible) _frame.Show();
        }
        else
        {
            _frame.Hide();
            _dim.Hide();
        }

        if (recordingStarted)
        {
            if (_controller is null)
            {
                _controller = new RecordingControllerWindow { DataContext = _vm };
                _controller.Show();
                _controller.PlaceNear(_vm.RecordingRegion, _vm.VirtualScreen);
            }
        }
        else if (_controller is not null)
        {
            _controller.Close();
            _controller = null;
        }
    }

    /// <summary>画面を枠と重ならない位置へ寄せる（枠の右、入らなければ左、それも無理なら右下）。</summary>
    private void PlaceAwayFromRegion()
    {
        var r = _vm.PreviewRegion;
        if (r is null) return;
        var s = ScreenScale.Of(this);
        var screen = _vm.VirtualScreen;
        double w = ActualWidth * s, h = ActualHeight * s;
        double x = r.Value.Right + 16 <= screen.Right - w ? r.Value.Right + 16
            : r.Value.X - 16 - w >= screen.X ? r.Value.X - 16 - w
            : screen.Right - w - 16;
        double y = Math.Clamp(r.Value.Y, screen.Y, Math.Max(screen.Y, screen.Bottom - h - 48));
        Left = x / s;
        Top = y / s;
    }

    private async Task<bool> RunCountdownAsync(int seconds, CaptureRegion region)
    {
        UpdateOverlays();
        var w = new CountdownWindow();
        return await w.RunAsync(seconds, region);
    }

    private static RecordingStopChoice AskAfterStop(RecordingStopSummary summary)
    {
        var w = new RecordingStopWindow(summary);
        w.ShowDialog();
        return w.Choice;
    }

    private RecordingDoneChoice? AskStartEditing(Task<RecordingResult> saving, RecordingInfo info)
    {
        var w = new RecordingDoneWindow(saving, info, _vm.OutputDirectory);
        w.ShowDialog();
        return w.Failed ? null : new RecordingDoneChoice(w.StartEditing, w.ProjectName);
    }

    // ---------------- ショートカット ----------------

    private void RegisterHotkeys()
    {
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        const uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        var failed = new List<string>();
        if (!NativeMethods.RegisterHotKey(helper.Handle, HotkeyStartStop, mods, VK_F9)) failed.Add("Ctrl+Shift+F9");
        if (!NativeMethods.RegisterHotKey(helper.Handle, HotkeyPause, mods, VK_F8)) failed.Add("Ctrl+Shift+F8");
        if (failed.Count > 0)
            _vm.StatusText = $"{string.Join("・", failed)} は他のアプリが使用中のため使えません。コントローラーのボタンで操作してください。";
    }

    private void UnregisterHotkeys()
    {
        var h = new WindowInteropHelper(this).Handle;
        NativeMethods.UnregisterHotKey(h, HotkeyStartStop);
        NativeMethods.UnregisterHotKey(h, HotkeyPause);
        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HotkeyStartStop:
                _vm.ToggleStartStop();
                handled = true;
                break;
            case HotkeyPause:
                _vm.TogglePause();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    // ---------------- 入力・閉じる ----------------

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.ApplyRegionSizeCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// サイズ・向きのボタン（v1.16）が選択されたとき。UI Automation の選択（SelectionItemPattern.Select）では Click が起きず
    /// Command が実行されないため、ここでも実行する（マウス・キーボードでは Click でも実行されるが、同じ選択の 2 回目は結果を変えない）。
    /// 実行できなかった場合（録画中など）も、表示を ViewModel の状態に戻す。
    /// </summary>
    private void ChoiceButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb) return;
        if (rb.Command is { } command && command.CanExecute(rb.CommandParameter)) command.Execute(rb.CommandParameter);
        // 同じ行のボタン（グループ）すべての表示を ViewModel に合わせる（グループの処理で外れた側も含む）。
        var siblings = rb.Parent is System.Windows.Controls.Panel panel ? panel.Children.OfType<System.Windows.Controls.RadioButton>() : [rb];
        foreach (var b in siblings)
            System.Windows.Data.BindingOperations.GetBindingExpression(b, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        if (_vm.IsBusy)
        {
            // 録画中に閉じる（アプリの終了を含む）場合は、録画を止めて途中のファイルを消す。
            e.Cancel = true;
            if (MessageBox.Show(this, "録画中です。録画を中止して閉じますか？（録画した内容は保存されません）", "録画の中止",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            await _vm.AbortAsync();
            _closingConfirmed = true;
            Close();
            return;
        }
        _vm.SaveSettings();
    }
}

/// <summary>物理 px と WPF の論理単位（DIP）の比。本アプリはシステム DPI で動くため、全モニター共通の値を使う。</summary>
internal static class ScreenScale
{
    public static double Of(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual);
        return source?.CompositionTarget?.TransformToDevice.M11 is { } m && m > 0 ? m : VisualTreeHelper.GetDpi(visual).DpiScaleX;
    }

    /// <summary>ウィンドウを画面キャプチャから除外する（コントローラー等が録画に写らないように）。</summary>
    public static void ExcludeFromCapture(Window window)
    {
        var h = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowDisplayAffinity(h, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }
}
