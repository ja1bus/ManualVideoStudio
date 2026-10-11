using System.Text;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

public sealed record GifExportRequest(
    string FfmpegPath,
    int FfmpegMajorVersion,
    string InputPath,
    VideoMetadata Metadata,
    IReadOnlyList<CutSegment> CutSegments,
    IReadOnlyList<SubtitleSegment> Subtitles,
    SubtitleStyle SubtitleStyle,
    GifQuality Quality,
    string OutputPath,
    double TransitionSeconds = 0,
    ISubtitleTextMeasurer? TextMeasurer = null,
    VideoShapeLayers? Shapes = null,
    VideoTitleImage? Title = null);

public sealed record GifExportResult(string OutputPath, double OutputDuration, long FileSize, TimeSpan Elapsed);

public interface IGifExportService
{
    Task<GifExportResult> ExportAsync(GifExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct);
}

/// <summary>
/// 編集結果（カット・図形・字幕）を Animated GIF で書き出す。MP4 と同じカット・図形・字幕データを使い、
/// カット → 図形・字幕の焼き込み → GIF 用の FPS・縮小 → パレット生成（1 回目）→ パレット適用（2 回目）の順で処理する。
/// 音声は使わない（音声付きの動画でもエラーにしない）。一時ファイル経由で、成功時だけ出力先へ移動する。
/// </summary>
public sealed class GifExportService(IProcessRunner runner, ILogger<GifExportService>? logger = null) : IGifExportService
{
    private readonly ILogger _logger = logger ?? NullLogger<GifExportService>.Instance;

    public async Task<GifExportResult> ExportAsync(GifExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var input = Path.GetFullPath(request.InputPath);
        var output = Path.GetFullPath(request.OutputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("出力先が元動画と同じです。別のファイル名を指定してください。");
        if (!request.Metadata.HasVideo || request.Metadata.Width <= 0 || request.Metadata.Height <= 0)
            throw new InvalidOperationException("映像の大きさが分からないため GIF を作成できません。");

        var keep = EditTimelineCalculator.ComputeKeepRanges(request.Metadata.Duration, request.CutSegments);
        if (keep.Count == 0) throw new InvalidOperationException("すべての区間が削除されるため出力できません。");
        // 進捗・結果の長さはタイトルを含む完成動画の長さ（本編 + タイトルの秒数）。
        var total = EditTimelineCalculator.OutputDuration(keep) + (request.Title?.Seconds ?? 0);
        var preset = GifPreset.Of(request.Quality);

        var dir = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileNameWithoutExtension(output) + ".partial.gif");
        // 図形・字幕は元の大きさの映像に焼き込み、その後で GIF 用に縮小する（MP4 と同じ見た目の比率）。
        using var work = await ExportWorkspace.CreateAsync(request.Subtitles, request.SubtitleStyle,
            request.Metadata.Width, request.Metadata.Height, request.TextMeasurer, ct, request.Shapes,
            request.Title, preset.Fps);
        var palette = Path.Combine(AppTemp.Root, $"manualvideostudio-palette-{Guid.NewGuid():N}");
        var scripts = new List<string>();
        _logger.LogInformation("GIF export start: {Input} -> {Output}, quality={Quality}, ranges={Count}, subtitles={Subs}",
            input, output, preset.Quality, keep.Count, request.Subtitles.Count);
        var succeeded = false;
        try
        {
            var pass1 = FFmpegArguments.GifPalette(input, keep, request.Metadata, work.SubtitleFile, preset, palette, request.FfmpegMajorVersion, request.TransitionSeconds, work.Shapes, work.Title);
            var r1 = await RunAsync(request, pass1, work.Directory, scripts, total, 0.0, 0.4, progress, ct);
            if (!r1.Succeeded || !File.Exists(palette))
                throw new FFmpegException($"GIF のパレット作成に失敗しました（終了コード {r1.ExitCode}）。", r1.ErrorTail(15));

            var pass2 = FFmpegArguments.GifEncode(input, keep, request.Metadata, work.SubtitleFile, preset, palette, temp, request.FfmpegMajorVersion, request.TransitionSeconds, work.Shapes, work.Title);
            var r2 = await RunAsync(request, pass2, work.Directory, scripts, total, 0.4, 0.6, progress, ct);
            if (!r2.Succeeded || !File.Exists(temp))
                throw new FFmpegException($"GIF の書き出しに失敗しました（終了コード {r2.ExitCode}）。", r2.ErrorTail(15));

            File.Move(temp, output, overwrite: true);
            succeeded = true;
            var size = new FileInfo(output).Length;
            _logger.LogInformation("GIF export done: {Output} ({Size} bytes)", output, size);
            return new GifExportResult(output, total, size, r1.Elapsed + r2.Elapsed);
        }
        finally
        {
            if (!succeeded) TryDelete(temp);
            TryDelete(palette);
            foreach (var s in scripts) TryDelete(s);
        }
    }

    private async Task<ProcessResult> RunAsync(GifExportRequest request, ExportCommand command, string? workingDirectory, List<string> scripts,
        double total, double baseRatio, double share, IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var args = command.Arguments;
        if (command.FilterScript is not null)
        {
            var path = FFmpegArguments.NewFilterScriptPath();
            scripts.Add(path);
            await File.WriteAllTextAsync(path, command.FilterScript, new UTF8Encoding(false), ct);
            args = command.WithScriptPath(path);
        }
        return await runner.RunAsync(new ProcessRequest(request.FfmpegPath, args)
        {
            WorkingDirectory = workingDirectory,
            OnStandardOutputLine = line =>
            {
                if (FFmpegProgressParser.ParseLine(line) is { } sec)
                {
                    var ratio = FFmpegProgressParser.Ratio(sec, total);
                    progress?.Report(new VideoExportProgress(baseRatio + share * ratio,
                        double.IsPositiveInfinity(sec) ? total : Math.Min(sec, total), total));
                }
            },
            MaxCapturedChars = 400_000,
        }, ct);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }

    /// <summary>既定の出力パス（元ファイル名_edited.gif。編集のフォルダーのプロジェクトは、そのフォルダーに「プロジェクト名_edited.gif」。v1.7）。</summary>
    public static string DefaultOutputPath(string inputPath, GifExportSettings settings, string? projectPath = null)
    {
        var dir = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? Persistence.ProjectFolders.DefaultOutputFolder(inputPath, projectPath) : settings.OutputDirectory;
        var name = string.IsNullOrWhiteSpace(settings.OutputFileName)
            ? Persistence.ProjectFolders.DefaultOutputBaseName(inputPath, projectPath) + "_edited.gif"
            : settings.OutputFileName;
        return Path.Combine(dir, name);
    }
}
