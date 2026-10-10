using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Persistence;

/// <summary>エクスポートした zip の先頭に入れる目印（v1.7）。インポートではこれが無い zip を受け付けない。</summary>
public sealed class ArchiveManifest
{
    public const string FileName = "manualvideostudio-export.json";
    public const string FormatName = "ManualVideoStudioExport";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
    /// <summary>録画のフォルダー名（zip の中の最上位フォルダー）。</summary>
    public string Folder { get; set; } = "";
    /// <summary>エクスポートしたときのフォルダーのフルパス（インポート後にプロジェクトファイルのパスを書き換えるため）。</summary>
    public string SourceFolder { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>
/// 録画のフォルダー（元動画とすべての編集）を zip にまとめる・zip から取り込む（v1.7。バックアップ用）。
/// 動画・画像はもともと圧縮されているため、無圧縮で格納して時間を短くする。4 GB を超えるファイルも扱える（ZIP64）。
/// </summary>
public sealed class ProjectArchive(ILogger? logger = null)
{
    private static readonly HashSet<string> StoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv", ".gif", ".png", ".jpg", ".jpeg", ".zip", ".7z",
    };

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>既定の zip のファイル名（「名前_yyyyMMdd_HHmm.zip」。名前が無ければフォルダー名）。</summary>
    public static string DefaultFileName(string recordingFolderName)
    {
        if (ProjectFolders.TryParseStamp(recordingFolderName) is null) return recordingFolderName + ".zip";
        var stamp = recordingFolderName[..13];
        var rest = recordingFolderName.Length > 14 ? recordingFolderName[14..] : "";
        return (rest.Length == 0 ? stamp : $"{rest}_{stamp}") + ".zip";
    }

    /// <summary>
    /// 録画のフォルダーを zip にする。途中のファイルは「.part」で作り、完了してから名前を変える（キャンセル・失敗なら消す）。
    /// progress は 0～1。
    /// </summary>
    public async Task ExportAsync(string recordingFolder, string zipPath, string appVersion, IProgress<double>? progress, CancellationToken ct)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder));
        var folderName = Path.GetFileName(folder);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        long total = files.Sum(f => f.Length);
        var partial = zipPath + ".part";
        try
        {
            await Task.Run(async () =>
            {
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var manifest = new ArchiveManifest
                    {
                        AppVersion = appVersion,
                        ExportedAt = DateTimeOffset.Now,
                        Folder = folderName,
                        SourceFolder = folder,
                        FileCount = files.Count,
                        TotalBytes = total,
                    };
                    var m = zip.CreateEntry(ArchiveManifest.FileName, CompressionLevel.Optimal);
                    using (var ms = m.Open()) await JsonSerializer.SerializeAsync(ms, manifest, JsonDefaults.Options, ct);

                    long done = 0;
                    var buffer = new byte[1 << 20];
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entryName = folderName + "/" + Path.GetRelativePath(folder, f.FullName).Replace('\\', '/');
                        var level = StoredExtensions.Contains(f.Extension) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        var entry = zip.CreateEntry(entryName, level);
                        entry.LastWriteTime = f.LastWriteTime;
                        using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var dst = entry.Open();
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                            done += n;
                            if (total > 0) progress?.Report((double)done / total);
                        }
                    }
                }
                File.Move(partial, zipPath, overwrite: true);
            }, ct);
            _logger.LogInformation("Exported {Folder} -> {Zip} ({Count} files, {Bytes} bytes)", folder, zipPath, files.Count, total);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// 作った zip を読み直し、フォルダーの全ファイルが同じ大きさで入っているか確かめる。
    /// 問題があれば理由（利用者向けの文）、無ければ null。
    /// </summary>
    public static string? Verify(string zipPath, string recordingFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder));
        var folderName = Path.GetFileName(folder);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(ArchiveManifest.FileName) is null) return "zip に目印のファイルがありません。";
            var entries = zip.Entries.Where(e => e.FullName != ArchiveManifest.FileName && !e.FullName.EndsWith('/'))
                .ToDictionary(e => e.FullName, e => e.Length, StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
            if (entries.Count != files.Count) return $"zip のファイル数（{entries.Count}）がフォルダー（{files.Count}）と違います。";
            foreach (var f in files)
            {
                var name = folderName + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/');
                if (!entries.TryGetValue(name, out var length)) return $"zip に入っていないファイルがあります。{Environment.NewLine}{f}";
                if (length != new FileInfo(f).Length) return $"zip の中の大きさが違うファイルがあります。{Environment.NewLine}{f}";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"zip を読み直せませんでした: {ex.Message}";
        }
    }

    /// <summary>zip の目印を読む。Manual Video Studio のエクスポートでなければ例外（InvalidOperationException、利用者向けの文）。</summary>
    public static ArchiveManifest ReadManifest(ZipArchive zip, string zipPath)
    {
        var entry = zip.GetEntry(ArchiveManifest.FileName)
            ?? throw new InvalidOperationException($"Manual Video Studio のエクスポートではない zip です（目印のファイル {ArchiveManifest.FileName} がありません）。{Environment.NewLine}{zipPath}");
        ArchiveManifest? manifest;
        try
        {
            using var s = entry.Open();
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(s, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"zip の目印のファイルを読めません。{Environment.NewLine}{zipPath}", ex);
        }
        if (manifest is null || manifest.Format != ArchiveManifest.FormatName)
            throw new InvalidOperationException($"Manual Video Studio のエクスポートではない zip です。{Environment.NewLine}{zipPath}");
        if (manifest.SchemaVersion > ArchiveManifest.CurrentSchemaVersion)
            throw new InvalidOperationException($"新しいバージョンのアプリでエクスポートした zip です（形式 v{manifest.SchemaVersion}）。{Environment.NewLine}{zipPath}");
        var name = manifest.Folder;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"zip の中のフォルダー名が正しくありません（{name}）。{Environment.NewLine}{zipPath}");
        return manifest;
    }

    /// <summary>
    /// zip を保存先ルートに取り込む。同じ名前のフォルダーがあれば「_2」「_3」… を付ける。
    /// 作業用のフォルダー（.importing-…）に展開してから名前を変え、失敗・キャンセルなら作業用のフォルダーを消す。
    /// 取り込んだ後、プロジェクトファイルの元動画・書き出し先のパスを取り込んだ場所に書き換える。取り込んだフォルダーのパスを返す。
    /// </summary>
    public async Task<string> ImportAsync(string zipPath, string root, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var work = Path.Combine(root, $".importing-{Guid.NewGuid():N}");
        try
        {
            return await Task.Run(async () =>
            {
                ArchiveManifest manifest;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    manifest = ReadManifest(zip, zipPath);
                    var prefix = manifest.Folder + "/";
                    var entries = zip.Entries.Where(e => e.FullName != ArchiveManifest.FileName).ToList();
                    foreach (var e in entries)
                    {
                        if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException($"zip に録画のフォルダーの外のファイルがあります（{e.FullName}）。{Environment.NewLine}{zipPath}");
                    }
                    long total = entries.Sum(e => e.Length), done = 0;
                    Directory.CreateDirectory(work);
                    var buffer = new byte[1 << 20];
                    foreach (var e in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = e.FullName[prefix.Length..];
                        if (rel.Length == 0) continue;
                        var dest = Path.GetFullPath(Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar)));
                        // zip の中の「..」などで作業用のフォルダーの外に書かない。
                        if (!ProjectFolders.IsUnder(dest, work))
                            throw new InvalidOperationException($"zip に正しくないパスがあります（{e.FullName}）。{Environment.NewLine}{zipPath}");
                        if (e.FullName.EndsWith('/'))
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using (var src = e.Open())
                        using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            int n;
                            while ((n = await src.ReadAsync(buffer, ct)) > 0)
                            {
                                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                                done += n;
                                if (total > 0) progress?.Report((double)done / total);
                            }
                        }
                        File.SetLastWriteTime(dest, e.LastWriteTime.LocalDateTime);
                    }
                }
                var target = Path.Combine(root, manifest.Folder);
                for (int i = 2; Directory.Exists(target) || File.Exists(target); i++) target = Path.Combine(root, $"{manifest.Folder}_{i}");
                Directory.Move(work, target);
                foreach (var p in ProjectFolders.ProjectFilesIn(target))
                {
                    try { ProjectCatalog.Relocate(p, manifest.SourceFolder, target); }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        // 読めないプロジェクトファイルはそのまま残す（一覧に注意として出る）。
                        _logger.LogWarning(ex, "Relocate failed: {Path}", p);
                    }
                }
                _logger.LogInformation("Imported {Zip} -> {Folder}", zipPath, target);
                return target;
            }, ct);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }

    private void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to delete {Path}", path); }
    }
}
