using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 不要区間の解析（CutAnalysis）の依頼用動画（v1.21。設計 4.2）。元動画の時間軸のまま（カットを適用しない）、
/// 映像の下に黒い帯を足して「区間 12　00:03:25」を 1 秒ごとに焼き込む。
/// 保存先・削除は字幕依頼用動画と同じ <see cref="CaptionRequestStore"/> を使い、ファイル名の頭を <see cref="FilePrefix"/> にして区別する。
/// 帯の文字の ASS は一時フォルダー（<see cref="AppTemp"/>）に作り、終わったら消す。
/// </summary>
public sealed class AiRequestVideoService(IProcessRunner runner, CaptionRequestStore store, ILogger<AiRequestVideoService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<AiRequestVideoService>.Instance;

    public const string FilePrefix = "cut-request_";
    public const string TimecodeFileName = "timecodes.ass";

    /// <summary>生成条件の名前（指紋に含める）。生成条件を変えたら変える（準備済みの動画を再利用しないため）。</summary>
    public const string EncodeProfile = "fps10-crf28-veryfast-a96-band6-v1";

    /// <summary>元動画・区間表・生成条件の指紋。同じなら準備済みの動画をそのまま使える（カットは含めない。元動画の時間軸のため）。</summary>
    public static string Fingerprint(ProjectDocument project, IReadOnlyList<AiSegment> segments)
    {
        var sb = new StringBuilder();
        var src = project.SourceVideo.Path;
        var fi = new FileInfo(src);
        sb.Append(Path.GetFullPath(src).ToUpperInvariant()).Append('|').Append(fi.Exists ? fi.Length : 0).Append('|')
          .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('|').Append(F(project.VideoMetadata?.Duration ?? 0)).Append('|')
          .Append(project.VideoMetadata?.HasAudio == true ? "a" : "-").Append('|').Append(EncodeProfile);
        foreach (var s in segments.OrderBy(s => s.Start))
            sb.Append('|').Append(s.Id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(F(s.Start)).Append('-').Append(F(s.End));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>準備済みの動画を今のプロジェクト・区間表でそのまま使えるか（ファイルがあり、指紋が一致する）。</summary>
    public static bool IsCurrent(CaptionRequestVideo? video, ProjectDocument project, IReadOnlyList<AiSegment> segments)
        => video is not null && File.Exists(video.Path) && video.Fingerprint == Fingerprint(project, segments);

    /// <summary>
    /// 依頼用動画を作る。成功したときだけ保存場所に置き、削除してよい動画として記録する。
    /// キャンセル・失敗では途中のファイルを残さない（一時ファイル *.partial.mp4 へ書いて成功時に移す）。
    /// 失敗は段階と対象のフルパスを <see cref="PathStageException"/>（書き出しの失敗は <see cref="FFmpegException"/>）で返し、ログにも記録する。
    /// </summary>
    /// <param name="segments">プロンプトに使う区間表（<see cref="Ai.SegmentTable.ToHistory"/>）。空なら帯には時刻だけを出す。</param>
    /// <param name="projectLocation">プロジェクトフォルダー内の保存先（<see cref="CaptionRequestStore.ProjectLocation"/>）。未保存なら null。</param>
    public async Task<CaptionRequestVideo> PrepareAsync(string ffmpegPath, ProjectDocument project, IReadOnlyList<AiSegment> segments,
        string projectName, string? projectLocation, IProgress<VideoExportProgress>? progress, CancellationToken ct)
    {
        var meta = project.VideoMetadata ?? throw new InvalidOperationException("動画の情報がありません。");
        if (meta.Width <= 0 || meta.Height <= 0 || !(meta.Duration > 0)) throw new InvalidOperationException("動画の大きさ・長さが分かりません。");
        var fingerprint = Fingerprint(project, segments);

        // 1. 元動画
        var source = project.SourceVideo.Path;
        if (!File.Exists(source))
            throw Fail(new PathStageException("元動画の確認", source, "元動画が見つかりません（移動・削除された可能性があります）。元動画を指定し直してください。"));
        try { using var _ = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Fail(new PathStageException("元動画の読み取り", source, ex.Message, ex));
        }

        // 2. 保存先（プロジェクトフォルダー内）
        CaptionRequestLocation location;
        try { location = store.Resolve(projectLocation); }
        catch (PathStageException ex) { throw Fail(ex); }
        var output = store.NewPath(location, projectName, DateTime.Now, FilePrefix);
        var temp = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".partial" + Path.GetExtension(output));
        _logger.LogInformation("Cut request prepare: source={Source} output={Output} segments={Count}", source, output, segments.Count);

        // 3. 帯の文字（ASS）を一時フォルダーに作る
        var layout = FFmpegArguments.AiRequestVideoSize(meta.Width, meta.Height);
        var work = Path.Combine(AppTemp.Root, $"manualvideostudio-cutreq-{Guid.NewGuid():N}");
        var succeeded = false;
        try
        {
            try
            {
                Directory.CreateDirectory(work);
                var ass = TimecodeAssBuilder.Build(meta.Duration, segments, layout.Width, layout.Height, layout.BandHeight);
                await File.WriteAllTextAsync(Path.Combine(work, TimecodeFileName), ass, new UTF8Encoding(true), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Fail(new PathStageException("帯の文字（字幕ファイル）の作成", Path.Combine(work, TimecodeFileName), ex.Message, ex));
            }

            // 4. FFmpeg の書き出し
            var args = FFmpegArguments.AiRequestVideo(Path.GetFullPath(source), meta, TimecodeFileName, temp);
            var total = meta.Duration;
            var r = await runner.RunAsync(new ProcessRequest(ffmpegPath, args)
            {
                // 字幕ファイルは作業フォルダーからの相対名で渡す（フィルタ記法でのパスのエスケープを避ける）。
                WorkingDirectory = work,
                OnStandardOutputLine = line =>
                {
                    if (FFmpegProgressParser.ParseLine(line) is { } sec)
                        progress?.Report(new VideoExportProgress(FFmpegProgressParser.Ratio(sec, total),
                            double.IsPositiveInfinity(sec) ? total : Math.Min(sec, total), total));
                },
                MaxCapturedChars = 400_000,
            }, ct);
            if (!r.Succeeded)
            {
                var error = new FFmpegException($"依頼用動画の作成に失敗しました（終了コード {r.ExitCode}）。{Environment.NewLine}出力先: {temp}", r.ErrorTail(15));
                _logger.LogError("Cut request: FFmpeg failed: {Message} / {Detail}", error.Message, error.ErrorDetail);
                throw error;
            }
            if (!File.Exists(temp))
                throw Fail(new PathStageException("FFmpeg の出力ファイルの確認", temp, "FFmpeg は正常終了しましたが、出力ファイルが見つかりません。"));
            try { File.Move(temp, output, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Fail(new PathStageException("一時ファイルから完成名への移動", output, $"{ex.Message}（一時ファイル: {temp}）", ex));
            }

            // 5. 生成後の確認
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw Fail(new PathStageException("生成後のファイルの確認", output, "書き出しは完了しましたが、出力ファイルが見つからない、または空です。"));
            succeeded = true;
            store.Register(output);
            _logger.LogInformation("Cut request video prepared: {Path} ({Elapsed:0.0}s)", output, r.Elapsed.TotalSeconds);
            return new CaptionRequestVideo(output, fingerprint, total, DateTimeOffset.Now) { Notice = location.Notice };
        }
        finally
        {
            if (!succeeded) TryDelete(temp);
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", work); }
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }

    private PathStageException Fail(PathStageException ex)
    {
        _logger.LogError(ex, "Cut request failed at {Stage}: {Path}: {Reason}", ex.Stage, ex.FilePath, ex.Reason);
        return ex;
    }
}
