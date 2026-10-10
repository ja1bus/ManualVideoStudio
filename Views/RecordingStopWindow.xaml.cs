using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>
/// 録画の確認（v1.6.1）。停止の直後に表示し、保存する / 破棄して録り直す / 破棄して閉じる を選ばせる。
/// 録画に失敗した（写す画面を間違えた・操作を誤った）場合に、録画ファイルとプロジェクトフォルダーを作らずにやり直せるようにする。
/// 録画の最後に写らないよう、画面キャプチャから除外する（v1.6.2）。
/// 録画の完了画面と同じく、閉じるのはボタンのクリックだけ（Esc・Enter・× では閉じない）。
/// 停止のショートカットの直後の誤クリック・打鍵を防ぐため、ボタンは <see cref="RecordingDoneWindow.ButtonGuard"/> の後に押せるようにする。
/// </summary>
public partial class RecordingStopWindow : Window
{
    private bool _closeAllowed;

    public RecordingStopWindow(RecordingStopSummary summary)
    {
        InitializeComponent();
        DurationText.Text = TimeFormat.Format(summary.Duration.TotalSeconds);
        InfoText.Text = RecordingDoneWindow.Describe(summary.Info, summary.SegmentCount);
        SourceInitialized += (_, _) =>
        {
            NativeMethods.RemoveStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_SYSMENU);
            // 停止の直後はまだ ffmpeg が画面を取り込んでいるため、この画面が録画の最後に写らないようにする（v1.6.2）。
            ScreenScale.ExcludeFromCapture(this);
        };
        Loaded += async (_, _) =>
        {
            await Task.Delay(RecordingDoneWindow.ButtonGuard);
            SaveButton.IsEnabled = true;
            RetakeButton.IsEnabled = true;
            DiscardButton.IsEnabled = true;
            WaitText.Text = "Esc・Enter キーでは閉じません。ボタンを押して選んでください。";
        };
    }

    public RecordingStopChoice Choice { get; private set; } = RecordingStopChoice.Save;

    private void Save_Click(object sender, RoutedEventArgs e) => CloseWith(RecordingStopChoice.Save);

    private void Retake_Click(object sender, RoutedEventArgs e) => CloseWith(RecordingStopChoice.Retake);

    private void Discard_Click(object sender, RoutedEventArgs e) => CloseWith(RecordingStopChoice.DiscardAndClose);

    private void CloseWith(RecordingStopChoice choice)
    {
        Choice = choice;
        _closeAllowed = true;
        DialogResult = true;
    }

    /// <summary>Alt+F4 などボタン以外で閉じようとしても閉じない。</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeAllowed && !Dispatcher.HasShutdownStarted) e.Cancel = true;
    }
}
