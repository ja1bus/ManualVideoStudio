using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 再生速度と J / K / L（v1.21.0。設計 3.2）。順方向は MediaElement の SpeedRatio（×1・×2・×4）、
/// 逆方向は 100 ms ごとに「現在 − 速度 × 0.1 秒」へ移動するコマ送りの連続で行う（音は出ない）。
/// Space の再生・試し再生・つなぎ目の再生は常に ×1 から。自分で移動・一時停止したら逆方向の再生は止める。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>逆方向の再生で移動する間隔（秒）。</summary>
    public const double ReverseInterval = 0.1;

    /// <summary>逆方向の再生で削除部分を飛ばすとき、手前の残る区間の終端からさらに戻る秒数（終端は区間の外のため）。</summary>
    private const double ReverseBackoff = 0.01;

    /// <summary>再生速度（-4, -2, -1, 1, 2, 4。負は逆方向）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackRateText), nameof(IsReversePlaying))]
    private double _playbackRate = 1;

    /// <summary>再生行に出す速度（「×2」「◀×2」）。</summary>
    public string PlaybackRateText => FormatRate(PlaybackRate);

    public static string FormatRate(double rate)
    {
        var abs = Math.Abs(rate).ToString("0.##", CultureInfo.InvariantCulture);
        return rate < 0 ? $"◀×{abs}" : $"×{abs}";
    }

    /// <summary>逆方向に再生中か。</summary>
    public bool IsReversePlaying => _reversing;

    private bool _reversing;
    /// <summary>逆方向の再生が自分で移動している間（SeekTo で逆方向の再生を止めない）。</summary>
    private bool _reverseStepping;
    private DispatcherTimer? _reverseTimer;

    partial void OnPlaybackRateChanged(double value)
    {
        if (value > 0 && _player is not null) _player.SpeedRatio = value;
    }

    /// <summary>次の速さ（1 → 2 → 4。4 のまま）。</summary>
    private static double Faster(double absRate) => absRate switch { < 2 => 2, _ => 4 };

    /// <summary>L: 順方向に再生する。再生中なら ×1 → ×2 → ×4 と速くする。</summary>
    [RelayCommand]
    private void PlayForward()
    {
        if (!HasProject) return;
        if (_reversing)
        {
            StopReverse();
            StartForward(1);
            return;
        }
        if (_player is { IsPlaying: true })
        {
            PlaybackRate = Faster(PlaybackRate);
            return;
        }
        StartForward(PlaybackRate > 0 ? PlaybackRate : 1);
    }

    /// <summary>J: 逆方向に再生する。逆方向に再生中なら ×1 → ×2 → ×4 と速くする。</summary>
    [RelayCommand]
    private void PlayReverse()
    {
        if (!HasProject) return;
        if (_reversing)
        {
            PlaybackRate = -Faster(-PlaybackRate);
            return;
        }
        PauseIfPlaying();
        ClearPreviewState(pause: false);
        if (CurrentTime <= 0)
        {
            StatusMessage = Loc.Get("Play.ReverseAtStart");
            return;
        }
        PlaybackRate = -1;
        _reversing = true;
        IsPlaying = true;
        OnPropertyChanged(nameof(IsReversePlaying));
        _reverseTimer ??= CreateReverseTimer();
        _reverseTimer.Start();
    }

    /// <summary>K: 停止して速度を ×1 に戻す。</summary>
    [RelayCommand]
    private void StopPlayback()
    {
        if (!HasProject) return;
        bool wasPlaying = _player is { IsPlaying: true };
        PauseIfPlaying();
        PlaybackRate = 1;
        if (wasPlaying) SeekTo(CurrentTime); // 一時停止位置をフレーム境界の値として確定する（Space と同じ）
    }

    /// <summary>再生行の速度ボタン: 順方向の速度を ×1 → ×2 → ×4 → ×1 と切り替える（再生中ならすぐ反映）。</summary>
    [RelayCommand]
    private void CyclePlaybackRate()
    {
        if (_reversing)
        {
            StopReverse();
            PlaybackRate = 1;
            return;
        }
        PlaybackRate = PlaybackRate switch { >= 4 => 1, >= 2 => 4, > 0 => 2, _ => 1 };
        StatusMessage = Loc.Format("Play.RateChanged", PlaybackRateText);
    }

    private void StartForward(double rate)
    {
        if (_player is not { IsOpened: true } player) return;
        ClearPreviewState(pause: false);
        if (Duration > 0 && CurrentTime >= Duration - 0.05) SeekTo(0);
        PlaybackRate = rate;
        player.SpeedRatio = rate;
        player.Play();
        IsPlaying = true;
    }

    private DispatcherTimer CreateReverseTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(ReverseInterval) };
        timer.Tick += (_, _) => ReverseStep();
        return timer;
    }

    /// <summary>
    /// 逆方向の再生の 1 回分（100 ms ごと）。先頭に達したら止める。削除部分を飛ばす再生（字幕編集中・［カットを反映して再生］）では、
    /// 削除部分に入ったら手前の残る区間の終端へ飛ぶ（手前に残る区間が無ければ、最初に残る位置で止める）。
    /// </summary>
    public void ReverseStep()
    {
        if (!_reversing) return;
        if (!HasProject) { StopReverse(); return; }
        double t = CurrentTime + PlaybackRate * ReverseInterval;
        bool stop = false;
        var (active, keep) = PlaybackSkipPlan();
        if (active && keep.Count > 0)
        {
            if (EditTimelineCalculator.PrevKeepEnd(t, keep) is { } end) t = end - ReverseBackoff;
            else if (EditTimelineCalculator.IsRemoved(t, keep))
            {
                t = keep[0].Start;
                stop = true;
            }
        }
        if (t <= 0)
        {
            t = 0;
            stop = true;
        }
        _reverseStepping = true;
        try { SeekTo(t); }
        finally { _reverseStepping = false; }
        if (stop) StopReverse();
    }

    /// <summary>逆方向の再生を止める（速度の表示は ×1 に戻す）。逆方向に再生していなければ何もしない。</summary>
    private void StopReverse()
    {
        if (!_reversing) return;
        _reversing = false;
        _reverseTimer?.Stop();
        IsPlaying = false;
        PlaybackRate = 1;
        OnPropertyChanged(nameof(IsReversePlaying));
    }

    /// <summary>プロジェクトを開き直したとき・試し再生を始めるときに、速度を ×1 に戻す。</summary>
    private void ResetPlaybackRate()
    {
        StopReverse();
        PlaybackRate = 1;
        if (_player is not null) _player.SpeedRatio = 1;
    }
}
