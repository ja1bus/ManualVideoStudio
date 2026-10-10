using System.Windows.Controls;
using System.Windows.Threading;

namespace ManualVideoStudio.Services;

/// <summary>プレビュー再生の抽象。ViewModel は MediaElement を直接触らない。</summary>
public interface IVideoPlayer
{
    bool IsOpened { get; }
    bool IsPlaying { get; }
    /// <summary>秒。</summary>
    double Position { get; set; }
    void Open(string path);
    void Close();
    void Play();
    void Pause();

    event EventHandler? Opened;
    event EventHandler<string>? Failed;
    event EventHandler? Ended;
    /// <summary>再生中に定期的（約 30 回/秒）に発生する。</summary>
    event EventHandler<double>? PositionChanged;
}

/// <summary>
/// WPF MediaElement（Windows Media Foundation）による再生。プレビュー専用で、書き出し・画像生成は ffmpeg で行う。
/// ScrubbingEnabled により一時停止中のシークでもフレームを表示する。
/// </summary>
public sealed class MediaElementPlayer : IVideoPlayer
{
    private readonly MediaElement _media;
    private readonly DispatcherTimer _timer;

    public MediaElementPlayer(MediaElement media)
    {
        _media = media;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => PositionChanged?.Invoke(this, Position);
        _media.LoadedBehavior = MediaState.Manual;
        _media.UnloadedBehavior = MediaState.Manual;
        _media.ScrubbingEnabled = true;
        _media.MediaOpened += (_, _) =>
        {
            IsOpened = true;
            Opened?.Invoke(this, EventArgs.Empty);
        };
        _media.MediaFailed += (_, e) =>
        {
            IsOpened = false;
            IsPlaying = false;
            _timer.Stop();
            Failed?.Invoke(this, e.ErrorException?.Message ?? "不明なエラー");
        };
        _media.MediaEnded += (_, _) =>
        {
            Pause();
            Ended?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsOpened { get; private set; }
    public bool IsPlaying { get; private set; }

    public double Position
    {
        get => _media.Position.TotalSeconds;
        set => _media.Position = TimeSpan.FromSeconds(Math.Max(0, value));
    }

    public event EventHandler? Opened;
    public event EventHandler<string>? Failed;
    public event EventHandler? Ended;
    public event EventHandler<double>? PositionChanged;

    public void Open(string path)
    {
        Close();
        _media.Source = new Uri(path, UriKind.Absolute);
        // Manual では Play → Pause で最初のフレームを表示させる。
        _media.Play();
        _media.Pause();
    }

    public void Close()
    {
        _timer.Stop();
        IsPlaying = false;
        IsOpened = false;
        _media.Stop();
        _media.Close();
        _media.Source = null;
    }

    public void Play()
    {
        if (!IsOpened) return;
        _media.Play();
        IsPlaying = true;
        _timer.Start();
    }

    public void Pause()
    {
        _media.Pause();
        IsPlaying = false;
        _timer.Stop();
    }
}
