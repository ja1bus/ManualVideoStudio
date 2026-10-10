using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Export;

/// <summary>整理の結果。</summary>
public sealed record FrameCacheCleanupResult(int DeletedFolders, long DeletedBytes, long RemainingBytes, int FailedFolders);

/// <summary>
/// サムネイル・元画像のキャッシュ（cache\&lt;動画キー&gt;\）の自動整理（v1.11.1）。
/// 削除は動画キーのフォルダー単位。最終アクセス（フォルダーの更新日時。<see cref="FrameExtractionService"/> が使うたびに更新する）が
/// <see cref="MaxAge"/> より古いものを消し（A）、なお合計が <see cref="MaxBytes"/> を超えていれば古い順に消す（B）。
/// 動画キー以外の名前のフォルダー・ルート直下のファイルには触れない。失敗しても止めず、次の起動でやり直す。
/// </summary>
public static partial class FrameCacheCleaner
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);
    public const long DefaultMaxBytes = 500L * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex VideoKeyPattern();

    public static FrameCacheCleanupResult Cleanup(string cacheRoot, TimeSpan maxAge, long maxBytes, DateTime? nowUtc = null, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!Directory.Exists(cacheRoot)) return new(0, 0, 0, 0);
        var now = nowUtc ?? DateTime.UtcNow;

        var folders = new List<(DirectoryInfo Dir, DateTime Access, long Size)>();
        try
        {
            foreach (var d in new DirectoryInfo(cacheRoot).EnumerateDirectories())
            {
                if (!VideoKeyPattern().IsMatch(d.Name)) continue;
                folders.Add((d, LastAccessUtc(d), SizeOf(d)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cache cleanup could not list {Path}", cacheRoot);
            return new(0, 0, 0, 0);
        }

        int deleted = 0, failed = 0;
        long deletedBytes = 0;
        var kept = new List<(DirectoryInfo Dir, DateTime Access, long Size)>();

        // A: 最終アクセスが古いもの。
        foreach (var f in folders)
        {
            if (now - f.Access > maxAge && TryDelete(f.Dir, logger)) { deleted++; deletedBytes += f.Size; }
            else
            {
                if (now - f.Access > maxAge) failed++;
                kept.Add(f);
            }
        }

        // B: 合計が上限を超えていれば、古い順に。
        var total = kept.Sum(f => f.Size);
        foreach (var f in kept.OrderBy(f => f.Access).ToList())
        {
            if (total <= maxBytes) break;
            if (TryDelete(f.Dir, logger)) { deleted++; deletedBytes += f.Size; total -= f.Size; }
            else failed++;
        }

        if (deleted > 0 || failed > 0)
            logger.LogInformation("Cache cleanup: deleted {Deleted} folders ({Bytes} bytes), failed {Failed}, remaining {Remaining} bytes", deleted, deletedBytes, failed, total);
        return new(deleted, deletedBytes, total, failed);
    }

    /// <summary>フォルダーの更新日時と中のファイルの更新日時のうち新しいほう。</summary>
    private static DateTime LastAccessUtc(DirectoryInfo dir)
    {
        var latest = dir.LastWriteTimeUtc;
        try
        {
            foreach (var f in dir.EnumerateFiles())
                if (f.LastWriteTimeUtc > latest) latest = f.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return latest;
    }

    private static long SizeOf(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool TryDelete(DirectoryInfo dir, ILogger logger)
    {
        try { dir.Delete(recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cache cleanup could not delete {Path}", dir.FullName);
            return false;
        }
    }
}
