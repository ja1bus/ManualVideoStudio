using CommunityToolkit.Mvvm.ComponentModel;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.Services;

/// <summary>現在の ffmpeg / ffprobe の検出結果（アプリ全体で共有）。</summary>
public sealed partial class FFmpegStatus(FFmpegLocator locator, IFFmpegInfoService info, ISettingsService settings, ILogger<FFmpegStatus> logger)
    : ObservableObject
{
    [ObservableProperty] private FFmpegLocation? _location;
    [ObservableProperty] private FFmpegCapabilities? _capabilities;
    [ObservableProperty] private string _statusText = "FFmpeg 未検出";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isDetecting;

    public string? FfmpegPath => Location?.Ffmpeg.Path;
    public string? FfprobePath => Location?.Ffprobe.Path;
    public int MajorVersion => Capabilities?.MajorVersion ?? 0;

    /// <summary>
    /// 設定のパスで探索し、見つかればバージョン・機能を検出する。
    /// 引数のパスは設定画面の［検出］（保存前のパスで試す）。その場合は記録（<see cref="FFmpegCache"/>）を使わず、書き換えもしない。
    /// </summary>
    public async Task RefreshAsync(string? ffmpegPath = null, string? ffprobePath = null)
    {
        IsDetecting = true;
        StatusText = "FFmpeg を確認しています…";
        try
        {
            var s = settings.Current;
            bool trial = ffmpegPath is not null || ffprobePath is not null;
            var cache = trial ? null : s.FfmpegCache;
            var loc = locator.Locate(ffmpegPath ?? s.FfmpegPath, ffprobePath ?? s.FfprobePath, cache?.FfmpegPath, cache?.FfprobePath);
            Location = loc;
            Capabilities = null;
            if (!loc.Ffmpeg.Found)
            {
                IsAvailable = false;
                StatusText = "FFmpeg が見つかりません";
                DetailText = loc.Ffmpeg.Message;
                logger.LogWarning("FFmpeg not found: {Message} PATH={Path}", loc.Ffmpeg.Message, Environment.GetEnvironmentVariable("PATH"));
                return;
            }
            var caps = cache?.Match(loc, FFmpegCache.StampOf);
            bool cached = caps is not null;
            caps ??= await info.DetectAsync(loc);
            Capabilities = caps;
            IsAvailable = loc.IsAvailable;
            var missing = caps.MissingFeatures();
            StatusText = loc.Ffprobe.Found
                ? $"FFmpeg {caps.FfmpegVersion}" + (missing.Count > 0 ? "（一部機能なし）" : "")
                : "ffprobe が見つかりません";
            DetailText = string.Join(Environment.NewLine, new[] { loc.Ffmpeg.Message, loc.Ffprobe.Message }
                .Concat(missing.Count > 0 ? ["利用できない機能: " + string.Join("、", missing)] : []));
            logger.LogInformation("FFmpeg detected{Cached}: {Ffmpeg} ({Source}) / {Ffprobe} ({ProbeSource}) / {Version}",
                cached ? " (saved result)" : "", loc.Ffmpeg.Path, loc.Ffmpeg.Source, loc.Ffprobe.Path, loc.Ffprobe.Source, caps.FfmpegVersion);
            if (!trial && !cached) Remember(loc, caps);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FFmpeg detection failed");
            IsAvailable = false;
            StatusText = "FFmpeg を実行できません";
            DetailText = ex.Message;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    /// <summary>見つけたパスと調べた版・機能を設定ファイルに記録し、次回の起動で使う（v1.6.1）。</summary>
    private void Remember(FFmpegLocation loc, FFmpegCapabilities caps)
    {
        if (!loc.IsAvailable) return;
        if (FFmpegCache.StampOf(loc.Ffmpeg.Path!) is not { } ffmpeg || FFmpegCache.StampOf(loc.Ffprobe.Path!) is not { } ffprobe) return;
        settings.Current.FfmpegCache = FFmpegCache.Create(loc, caps, ffmpeg, ffprobe);
        try
        {
            settings.Save();
            logger.LogInformation("Saved FFmpeg detection result to settings");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save FFmpeg detection result");
        }
    }
}
