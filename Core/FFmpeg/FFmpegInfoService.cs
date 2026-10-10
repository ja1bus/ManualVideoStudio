using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.FFmpeg;

/// <summary>ffmpeg のバージョンと、本アプリが使う機能の有無。</summary>
public sealed record FFmpegCapabilities
{
    public string FfmpegVersion { get; init; } = "";
    public string FfprobeVersion { get; init; } = "";
    public int MajorVersion { get; init; }
    public IReadOnlySet<string> Filters { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Encoders { get; init; } = new HashSet<string>();

    public bool HasFreezeDetect => Filters.Contains("freezedetect");
    public bool HasSilenceDetect => Filters.Contains("silencedetect");
    public bool HasSceneDetect => Filters.Contains("select") && Filters.Contains("showinfo");
    public bool HasLibx264 => Encoders.Contains("libx264");
    public bool HasAac => Encoders.Contains("aac");
    public bool HasPng => Encoders.Contains("png");
    public bool HasMjpeg => Encoders.Contains("mjpeg");
    /// <summary>字幕の焼き込み（libass の subtitles フィルタ）。</summary>
    public bool HasSubtitles => Filters.Contains("subtitles");
    /// <summary>Animated GIF（palettegen / paletteuse）。</summary>
    public bool HasGifPalette => Filters.Contains("palettegen") && Filters.Contains("paletteuse");

    /// <summary>必須機能が欠けている場合の説明（空なら問題なし）。</summary>
    public IReadOnlyList<string> MissingFeatures()
    {
        var list = new List<string>();
        if (!HasLibx264) list.Add("libx264（動画書き出し）");
        if (!HasAac) list.Add("aac（動画書き出しの音声）");
        if (!HasPng) list.Add("png（画像出力）");
        if (!HasMjpeg) list.Add("mjpeg（JPEG 出力・サムネイル）");
        if (!HasFreezeDetect) list.Add("freezedetect（静止区間解析）");
        if (!HasSilenceDetect) list.Add("silencedetect（無音区間解析）");
        if (!Filters.Contains("trim") || !Filters.Contains("concat")) list.Add("trim / concat（カット書き出し）");
        if (!HasSceneDetect) list.Add("select / showinfo（画面変化の検出）");
        if (!HasSubtitles) list.Add("subtitles / libass（字幕の焼き込み）");
        if (!HasGifPalette) list.Add("palettegen / paletteuse（Animated GIF）");
        return list;
    }
}

public interface IFFmpegInfoService
{
    Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default);
}

/// <summary>ffmpeg -version / -filters / -encoders を実行して機能を検出する。</summary>
public sealed partial class FFmpegInfoService(IProcessRunner runner, ILogger<FFmpegInfoService>? logger = null) : IFFmpegInfoService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly ILogger _logger = logger ?? NullLogger<FFmpegInfoService>.Instance;

    public async Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default)
    {
        if (location.Ffmpeg.Path is null) throw new InvalidOperationException("ffmpeg.exe が見つかりません。");
        var ffmpeg = location.Ffmpeg.Path;
        var version = await RunAsync(ffmpeg, ["-hide_banner", "-version"], ct);
        var versionLine = FirstLine(version);
        if (!versionLine.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ffmpeg として認識できません: {versionLine}");

        var filters = ParseNameList(await RunAsync(ffmpeg, ["-hide_banner", "-filters"], ct));
        var encoders = ParseNameList(await RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct));

        string probeLine = "";
        if (location.Ffprobe.Path is not null)
        {
            try { probeLine = FirstLine(await RunAsync(location.Ffprobe.Path, ["-hide_banner", "-version"], ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ffprobe -version failed");
                probeLine = "（実行できません）";
            }
        }

        return new FFmpegCapabilities
        {
            FfmpegVersion = ParseVersion(versionLine),
            FfprobeVersion = ParseVersion(probeLine),
            MajorVersion = ParseMajor(ParseVersion(versionLine)),
            Filters = filters,
            Encoders = encoders,
        };
    }

    private async Task<string> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var r = await runner.RunAsync(new ProcessRequest(exe, args) { Timeout = Timeout }, ct);
        if (!r.Succeeded) throw new InvalidOperationException($"{Path.GetFileName(exe)} {string.Join(' ', args)} が失敗しました（終了コード {r.ExitCode}）。");
        return r.StandardOutput;
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>"ffmpeg version 9.0.1-full_build-www.gyan.dev Copyright ..." → "9.0.1-full_build-www.gyan.dev"</summary>
    public static string ParseVersion(string line)
    {
        var m = VersionPattern().Match(line);
        return m.Success ? m.Groups[1].Value : line;
    }

    /// <summary>"9.0.1-..." → 9、"N-12345-g..."（開発版）→ 0。</summary>
    public static int ParseMajor(string version)
    {
        var m = MajorPattern().Match(version);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    /// <summary>
    /// -filters / -encoders の一覧から名前を取り出す。
    /// 行形式: " T.. freezedetect  V->V  Detects..." / " V....D libx264  ..."。凡例行（"= ..."）は除外される。
    /// </summary>
    public static HashSet<string> ParseNameList(string output)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var m = NameLinePattern().Match(raw);
            if (m.Success) set.Add(m.Groups[1].Value);
        }
        return set;
    }

    [GeneratedRegex(@"version\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^n?(\d+)\.")]
    private static partial Regex MajorPattern();

    [GeneratedRegex(@"^\s*[A-Z\.\|]{2,7}\s+([A-Za-z0-9_\-]+)\s")]
    private static partial Regex NameLinePattern();
}
