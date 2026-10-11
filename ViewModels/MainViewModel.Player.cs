using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>再生・シーク。プレビューは MediaElement、Windows で再生できない形式は ffmpeg で切り出した静止画で代替表示する。</summary>
public sealed partial class MainViewModel
{
    private IVideoPlayer? _player;
    private CancellationTokenSource? _fallbackCts;

    [ObservableProperty] private double _currentTime;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _playerError = "";
    [ObservableProperty] private ImageSource? _fallbackFrame;

    public bool IsPlayerFailed => PlayerError.Length > 0;
    public string CurrentTimeText => TimeFormat.Format(CurrentTime);
    public string DurationText => TimeFormat.Format(Duration);
    public double FrameDuration => Metadata is { Fps: > 0 } m ? 1.0 / m.Fps : 1.0 / 30;
    public string FrameText => Metadata is { Fps: > 0 } m ? $"フレーム {(long)Math.Floor(CurrentTime * m.Fps + 1e-6)}" : "";

    public void AttachPlayer(IVideoPlayer player)
    {
        _player = player;
        player.PositionChanged += (_, t) => SetCurrentTimeFromPlayer(t);
        player.Ended += (_, _) => IsPlaying = false;
        player.Opened += (_, _) =>
        {
            PlayerError = "";
            player.Position = CurrentTime;
        };
        player.Failed += (_, message) =>
        {
            IsPlaying = false;
            PlayerError = $"Windows ではこの動画をプレビュー再生できません（{message}）。ffmpeg で切り出した静止画で表示します。画像出力・書き出しは利用できます。";
            _logger.LogWarning("Preview failed: {Message}", message);
            _ = UpdateFallbackFrameAsync();
        };
    }

    partial void OnPlayerErrorChanged(string value) => OnPropertyChanged(nameof(IsPlayerFailed));

    partial void OnCurrentTimeChanged(double value)
    {
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(FrameText));
        OnPropertyChanged(nameof(CurrentOutputTimeText));
        OnPropertyChanged(nameof(SubtitleMarkInText));
        OnPropertyChanged(nameof(SubtitleMarkOutText));
        UpdateCurrentSubtitle();
        UpdateCurrentShapes();
    }

    private void SetCurrentTimeFromPlayer(double t)
    {
        if (Duration > 0) t = Math.Min(t, Duration);
        if (StopPreviewIfReached(t)) return;
        if (SkipRemovedWhilePlaying(t)) return;
        CurrentTime = t;
    }

    /// <summary>指定時刻へ移動する（タイムラインのクリック・一覧からの移動）。</summary>
    public void SeekTo(double time)
    {
        if (!HasProject) return;
        ClearPreviewState(pause: false); // 自分で移動したら、試し再生・つなぎ目の再生は終わり
        time = Math.Clamp(time, 0, Duration > 0 ? Duration : double.MaxValue);
        CurrentTime = Math.Round(time, 3);
        if (_player is { IsOpened: true }) _player.Position = CurrentTime;
        else if (IsPlayerFailed) _ = UpdateFallbackFrameAsync();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is not { IsOpened: true }) return;
        if (_player.IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            // 一時停止位置をフレーム境界の値として確定する。
            SeekTo(_player.Position);
        }
        else
        {
            ClearPreviewState(pause: false);
            if (Duration > 0 && CurrentTime >= Duration - 0.05) SeekTo(0);
            _player.Play();
            IsPlaying = true;
        }
    }

    private void PauseIfPlaying()
    {
        if (_player is { IsPlaying: true })
        {
            _player.Pause();
            IsPlaying = false;
            CurrentTime = Math.Round(_player.Position, 3);
        }
    }

    /// <summary>1 フレーム移動。parameter は "1" / "-1"。</summary>
    [RelayCommand]
    private void StepFrame(string? direction)
    {
        PauseIfPlaying();
        var dir = direction == "-1" ? -1 : 1;
        SeekTo(CurrentTime + dir * FrameDuration);
    }

    /// <summary>秒単位で移動。parameter は "+1" / "-1" / "+5" など（省略時は設定の移動秒数）。</summary>
    [RelayCommand]
    private void Jump(string? seconds)
    {
        PauseIfPlaying();
        double delta = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : Settings.Ui.JumpSeconds;
        SeekTo(CurrentTime + delta);
    }

    [RelayCommand] private void SeekStart() { PauseIfPlaying(); SeekTo(0); }
    [RelayCommand] private void SeekEnd() { PauseIfPlaying(); SeekTo(Duration); }

    /// <summary>前のカット位置（確定カットの開始）へ移動する。</summary>
    [RelayCommand]
    private void SeekPrevCut() => SeekToCutBoundary(next: false);

    /// <summary>次のカット位置（確定カットの開始）へ移動する。</summary>
    [RelayCommand]
    private void SeekNextCut() => SeekToCutBoundary(next: true);

    private void SeekToCutBoundary(bool next)
    {
        if (Project is null) return;
        PauseIfPlaying();
        var starts = Project.CutSegments.Select(c => c.Start).OrderBy(x => x).ToList();
        double? target = next
            ? starts.Where(t => t > CurrentTime + 0.01).Cast<double?>().FirstOrDefault()
            : starts.Where(t => t < CurrentTime - 0.01).Cast<double?>().LastOrDefault();
        if (target is null)
        {
            StatusMessage = Loc.Get(next ? "Seek.NoNextCut" : "Seek.NoPrevCut");
            return;
        }
        SeekTo(target.Value);
    }

    /// <summary>プレビュー再生できない動画の代替表示（現在位置の静止画）。連続シーク時は最後の要求だけ反映する。</summary>
    private async Task UpdateFallbackFrameAsync()
    {
        if (!IsPlayerFailed || FFmpeg.FfmpegPath is null || !HasProject) return;
        _fallbackCts?.Cancel();
        var cts = _fallbackCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cts.Token);
            var path = await _frames.GetThumbnailAsync(FFmpeg.FfmpegPath, VideoPath, CurrentTime, 960, cts.Token);
            if (!cts.IsCancellationRequested) FallbackFrame = ImageLoader.Load(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Fallback frame failed"); }
    }
}
