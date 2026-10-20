using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Join;

/// <summary>連結する動画 1 本（再生順）。DisplayName は結合元の記録（join-sources.json）に残す表示名。</summary>
public sealed record JoinInput(string Path, string DisplayName);

/// <summary>連結の依頼。Root は保存先のルート（作業フォルダーも新しいプロジェクトフォルダーもこの直下に作る）。</summary>
public sealed record JoinRequest(string FfmpegPath, string FfprobePath, string Root, IReadOnlyList<JoinInput> Inputs, string? Name, DateTime Time);

/// <summary>連結の結果。SourcesSaved が false なら join-sources.json だけ書けなかった（MP4 は使える）。</summary>
public sealed record JoinResult(string Folder, string VideoPath, VideoMetadata Metadata, bool SourcesSaved);

public interface IRecordingJoinService
{
    Task<JoinResult> JoinAsync(JoinRequest request, CancellationToken ct = default);
    /// <summary>連結・追加録画の条件確認のために、動画のストリーム情報を取得する。</summary>
    Task<JoinStreamInfo> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default);
}

/// <summary>
/// 録画の連結（v1.18）。元の動画は変更・削除しない。順序は Inputs のとおり。
/// 条件が違う動画は理由を示して連結しない（解像度などの自動変換はしない）。
/// 手順: 条件確認 → 空き容量の確認 → 保存先ルート内の隠し作業フォルダーへ連結 → 出力の確認 → 新しいフォルダーへ移動 → join-sources.json。
/// 失敗・キャンセルでは作業用の未完成ファイルだけを片付ける。
/// </summary>
public sealed class RecordingJoinService(IProcessRunner runner, IMediaMetadataService metadata, ILogger<RecordingJoinService>? logger = null) : IRecordingJoinService
{
    /// <summary>隠し作業フォルダーの名前の接頭辞（先頭が「.」のフォルダーは一覧に出ない）。</summary>
    public const string WorkFolderPrefix = ".join-";
    /// <summary>作業フォルダーに置く連結の一覧ファイル（拡張子なし）。</summary>
    public const string ListFileName = "inputs";
    /// <summary>空き容量に持たせる余裕（出力は入力の合計とほぼ同じ）。</summary>
    private const double FreeSpaceMargin = 1.1;

    private readonly ILogger _logger = logger ?? NullLogger<RecordingJoinService>.Instance;

    public Task<JoinStreamInfo> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
        => JoinProbe.ProbeAsync(runner, ffprobePath, videoPath, ct);

    public async Task<JoinResult> JoinAsync(JoinRequest r, CancellationToken ct = default)
    {
        if (r.Inputs.Count < 2) throw new JoinException("連結する動画が 2 本ありません。");
        foreach (var input in r.Inputs)
            if (!File.Exists(input.Path))
                throw new JoinException($"動画が見つかりません: {input.DisplayName}", input.Path);

        // 1. 条件の確認（すべてを先頭の動画と比べ、違う項目を全部示す）。
        var infos = new List<JoinStreamInfo>();
        foreach (var input in r.Inputs) infos.Add(await JoinProbe.ProbeAsync(runner, r.FfprobePath, input.Path, ct).ConfigureAwait(false));
        var problems = new List<string>();
        for (int i = 1; i < infos.Count; i++)
            foreach (var diff in JoinCompatibility.Compare(infos[0], infos[i]))
                problems.Add($"{System.IO.Path.GetFileName(r.Inputs[i].Path)}: {diff}");
        if (problems.Count > 0)
            throw new JoinException("動画の条件が違うため連結できません（解像度などの自動変換はしません）。", null, problems);

        // 2. 空き容量（出力は入力の合計とほぼ同じ大きさ）。
        long needed = (long)(r.Inputs.Sum(i => new FileInfo(i.Path).Length) * FreeSpaceMargin);
        Directory.CreateDirectory(r.Root);
        long free = FreeSpace(r.Root);
        if (free >= 0 && free < needed)
            throw new JoinException($"保存先の空き容量が足りないため連結できません（必要の目安 {FormatSize(needed)}、空き {FormatSize(free)}）。");

        // 3. 隠し作業フォルダーへ連結する。
        var work = System.IO.Path.Combine(r.Root, WorkFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var list = System.IO.Path.Combine(work, ListFileName);
            await File.WriteAllTextAsync(list, FFmpegArguments.ConcatListForPaths(r.Inputs.Select(i => i.Path)), new UTF8Encoding(false), ct).ConfigureAwait(false);
            var temp = System.IO.Path.Combine(work, "output.mp4");
            var run = await runner.RunAsync(new ProcessRequest(r.FfmpegPath, FFmpegArguments.ConcatVideos(list, temp))
            {
                WorkingDirectory = work,
                Timeout = TimeSpan.FromMinutes(60),
            }, ct).ConfigureAwait(false);
            if (!run.Succeeded || !File.Exists(temp))
                throw new JoinException($"動画を連結できませんでした（終了コード {run.ExitCode}）。元の動画は変更していません。", run.ErrorTail(10));

            // 4. 完成した動画の確認（映像があり、長さが入力の合計に近い）。
            VideoMetadata meta;
            try
            {
                meta = await metadata.ProbeAsync(r.FfprobePath, temp, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new JoinException("連結した動画を読めませんでした。元の動画は変更していません。", ex.Message);
            }
            double expected = infos.Sum(i => i.Duration);
            if (!meta.HasVideo || meta.Duration <= 0 || Math.Abs(meta.Duration - expected) > Tolerance(infos.Count))
                throw new JoinException($"連結した動画の長さが想定と違うため中止しました（想定 {expected:0.0} 秒、結果 {meta.Duration:0.0} 秒）。元の動画は変更していません。");

            // 5. 新しいプロジェクトのフォルダーへ移す。
            var folder = ProjectFolders.CreateFolder(r.Root, r.Time, r.Name);
            var fileName = $"join_{r.Time:yyyyMMdd_HHmmss}.mp4";
            var output = System.IO.Path.Combine(folder, fileName);
            try
            {
                File.Move(temp, output);
            }
            catch
            {
                TryDeleteEmpty(folder);
                throw;
            }

            // 6. 結合元の記録（失っても結合 MP4 と編集は使える。書けなくても連結は成功とする）。
            var sources = BuildSources(r, infos, fileName);
            bool saved = true;
            try
            {
                sources.Save(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                saved = false;
                _logger.LogWarning(ex, "Failed to write {File}", JoinSourcesFile.FileName);
            }
            _logger.LogInformation("Joined {Count} recordings into {Path} ({Duration:0.000}s)", r.Inputs.Count, output, meta.Duration);
            return new JoinResult(folder, output, meta, saved);
        }
        finally
        {
            TryDeleteDir(work);
        }
    }

    /// <summary>結合済みの動画が入力に含まれていれば、その結合元を展開して元素材の順序を引き継ぐ。</summary>
    private static JoinSourcesFile BuildSources(JoinRequest r, IReadOnlyList<JoinStreamInfo> infos, string outputFileName)
    {
        var file = new JoinSourcesFile { CreatedAt = DateTimeOffset.Now, OutputFileName = outputFileName };
        for (int i = 0; i < r.Inputs.Count; i++)
        {
            var input = r.Inputs[i];
            if (JoinSourcesFile.TryReadFor(input.Path) is { } previous)
                file.Sources.AddRange(previous.Sources);
            else
                file.Sources.Add(new JoinSourceEntry
                {
                    DisplayName = input.DisplayName,
                    FileName = System.IO.Path.GetFileName(input.Path),
                    DurationSeconds = Math.Round(infos[i].Duration, 3),
                });
        }
        return file;
    }

    /// <summary>出力と入力の合計の長さの許容差（秒）。継ぎ目ごとの音声の端数を見込む。</summary>
    internal static double Tolerance(int count) => 0.5 + 0.1 * count;

    private static long FreeSpace(string folder)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    internal static string FormatSize(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private static void TryDeleteEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Dir}", dir); }
    }

    /// <summary>前回の強制終了などで残った隠し作業フォルダーを片付ける（結合の開始前に呼ぶ。1 日以上前のものだけ）。</summary>
    public static void CleanStaleWorkFolders(string root, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root, WorkFolderPrefix + "*"))
            {
                if (DateTime.Now - Directory.GetLastWriteTime(dir) < TimeSpan.FromDays(1)) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger?.LogWarning(ex, "Failed to delete stale {Dir}", dir); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger?.LogWarning(ex, "Stale work folder scan failed"); }
    }
}
