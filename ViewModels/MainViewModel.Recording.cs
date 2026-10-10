using CommunityToolkit.Mvvm.Input;

namespace ManualVideoStudio.ViewModels;

/// <summary>画面録画。録画画面（RecorderWindow）を開き、「編集を開始」なら録画した MP4 を既存の「動画を開く」処理で読み込む。</summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (_windows.IsRecorderOpen)
        {
            _windows.ActivateRecorder();
            return;
        }
        if (!EnsureFFmpeg()) return;
        PauseIfPlaying();
        var outcome = await _windows.ShowRecorderAsync();
        if (outcome is null) return;
        StatusMessage = $"録画を保存しました: {outcome.OutputPath}";
        TouchProjectIndex(outcome.OutputPath);
        if (!outcome.StartEditing) return;
        await OpenVideoFileAsync(outcome.OutputPath);
        // 録画から作ったプロジェクトには録画の条件を記録する（参考情報）。
        if (Editor is not null && string.Equals(VideoPath, outcome.OutputPath, StringComparison.OrdinalIgnoreCase))
        {
            Editor.Project.RecordingSettings = outcome.Info;
            StatusMessage = $"録画した動画を開きました: {System.IO.Path.GetFileName(outcome.OutputPath)}。不要部分をカットしてから［カットを確定して字幕編集へ］で字幕を付けられます。";
        }
    }

    /// <summary>主画面を閉じてよいか（録画画面が開いている間は閉じない）。</summary>
    public bool CanClose()
    {
        if (!_windows.IsRecorderOpen) return true;
        _dialogs.ShowWarning("録画画面が開いています。録画画面を閉じてから終了してください。");
        _windows.ActivateRecorder();
        return false;
    }
}
