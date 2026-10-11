using System.Text;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

public sealed record VideoExportRequest(
    string FfmpegPath,
    int FfmpegMajorVersion,
    string InputPath,
    VideoMetadata Metadata,
    IReadOnlyList<CutSegment> CutSegments,
    string OutputPath,
    VideoEncodeOptions Options,
    IReadOnlyList<SubtitleSegment>? Subtitles = null,
    SubtitleStyle? SubtitleStyle = null,
    double TransitionSeconds = 0,
    ISubtitleTextMeasurer? TextMeasurer = null,
    VideoShapeLayers? Shapes = null,
    VideoTitleImage? Title = null);

public sealed record VideoExportProgress(double Ratio, double ProcessedSeconds, double TotalSeconds);

public sealed record VideoExportResult(string OutputPath, double OutputDuration, TimeSpan Elapsed);

public interface IVideoExportService
{
    Task<VideoExportResult> ExportAsync(VideoExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct);
}

/// <summary>
/// 確定カット区間を適用した動画を書き出す。図形・字幕があればカット後の映像へ重ねる（どちらも無ければ従来と同じ出力）。元動画は変更しない。
/// 一時ファイル（*.partial.*）へ出力し、成功時のみ出力先へ移動する。キャンセル・失敗時は一時ファイルを削除する。
/// </summary>
public sealed class VideoExportService(IProcessRunner runner, ILogger<VideoExportService>? logger = null) : IVideoExportService
{
    private readonly ILogger _logger = logger ?? NullLogger<VideoExportService>.Instance;

    public async Task<VideoExportResult> ExportAsync(VideoExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var input = Path.GetFullPath(request.InputPath);
        var output = Path.GetFullPath(request.OutputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("出力先が元動画と同じです。別のファイル名を指定してください。");

        var keep = EditTimelineCalculator.ComputeKeepRanges(request.Metadata.Duration, request.CutSegments);
        if (keep.Count == 0) throw new InvalidOperationException("すべての区間が削除されるため出力できません。");
        // 進捗・結果の長さはタイトルを含む完成動画の長さ（本編 + タイトルの秒数）。
        var total = EditTimelineCalculator.OutputDuration(keep) + (request.Title?.Seconds ?? 0);

        var dir = Path.GetDirectoryName(output)!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PathStageException("保存先フォルダーの作成", dir, ex.Message, ex);
        }
        var temp = Path.Combine(dir, Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));
        using var work = await ExportWorkspace.CreateAsync(request.Subtitles, request.SubtitleStyle,
            EvenSize(request.Metadata.Width), EvenSize(request.Metadata.Height), request.TextMeasurer, ct, request.Shapes,
            request.Title, request.Metadata.Fps);
        var command = FFmpegArguments.Export(input, keep, request.Metadata, temp, request.Options, request.FfmpegMajorVersion, work.SubtitleFile,
            request.TransitionSeconds, work.Shapes, work.Title);
        string? scriptPath = null;
        var args = command.Arguments;
        if (command.FilterScript is not null)
        {
            scriptPath = FFmpegArguments.NewFilterScriptPath();
            await File.WriteAllTextAsync(scriptPath, command.FilterScript, new UTF8Encoding(false), ct);
            args = command.WithScriptPath(scriptPath);
        }

        _logger.LogInformation("Export start: {Input} -> {Output}, ranges={Count}, duration={Duration:0.000}", input, output, keep.Count, total);
        var succeeded = false;
        try
        {
            var r = await runner.RunAsync(new ProcessRequest(request.FfmpegPath, args)
            {
                // 字幕ファイル・図形の画像は作業フォルダーからの相対名で渡す（フィルタ記法でのパスのエスケープを避ける）。
                WorkingDirectory = work.Directory,
                OnStandardOutputLine = line =>
                {
                    if (FFmpegProgressParser.ParseLine(line) is { } sec)
                        progress?.Report(new VideoExportProgress(FFmpegProgressParser.Ratio(sec, total),
                            double.IsPositiveInfinity(sec) ? total : Math.Min(sec, total), total));
                },
                MaxCapturedChars = 400_000,
            }, ct);
            if (!r.Succeeded)
                throw new FFmpegException($"動画の書き出しに失敗しました（終了コード {r.ExitCode}）。{Environment.NewLine}出力先: {temp}", r.ErrorTail(15));
            if (!File.Exists(temp))
                throw new PathStageException("FFmpeg の出力ファイルの確認", temp, "FFmpeg は正常終了しましたが、出力ファイルが見つかりません（保存先が外部のプロセスから別の場所に見えている可能性があります）。");

            try { File.Move(temp, output, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PathStageException("一時ファイルから完成名への移動", output, $"{ex.Message}（一時ファイル: {temp}）", ex);
            }
            succeeded = true;
            _logger.LogInformation("Export done: {Output} ({Elapsed:0.0}s)", output, r.Elapsed.TotalSeconds);
            return new VideoExportResult(output, total, r.Elapsed);
        }
        finally
        {
            if (!succeeded) TryDelete(temp);
            if (scriptPath is not null) TryDelete(scriptPath);
        }
    }

    /// <summary>yuv420p 用に偶数へ切り下げた大きさ（書き出しで右端・下端 1px を切り落とすため、字幕の座標もそれに合わせる）。</summary>
    private static int EvenSize(int v) => v & ~1;

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }

    /// <summary>既定の出力パス（元ファイル名_edited.mp4。編集のフォルダーのプロジェクトは、そのフォルダーに「プロジェクト名_edited.mp4」。v1.7）。</summary>
    public static string DefaultOutputPath(string inputPath, ExportSettings settings, string? projectPath = null)
    {
        var dir = string.IsNullOrWhiteSpace(settings.VideoOutputDirectory) ? Persistence.ProjectFolders.DefaultOutputFolder(inputPath, projectPath) : settings.VideoOutputDirectory;
        var name = string.IsNullOrWhiteSpace(settings.VideoOutputFileName)
            ? Persistence.ProjectFolders.DefaultOutputBaseName(inputPath, projectPath) + "_edited.mp4"
            : settings.VideoOutputFileName;
        return Path.Combine(dir, name);
    }
}
