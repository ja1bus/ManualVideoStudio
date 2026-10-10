using ManualVideoStudio.Core.Export;

namespace ManualVideoStudio.Tests;

/// <summary>キャッシュの自動整理（v1.11.1）: 30 日超の削除（A）・合計上限超の古い順削除（B）・対象外に触れない。</summary>
public sealed class FrameCacheCleanerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mvs-cache-" + Guid.NewGuid().ToString("N"));

    public FrameCacheCleanerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Make(string key, int bytes, double daysAgo)
    {
        var dir = Path.Combine(_root, key);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "t1000_w240");
        File.WriteAllBytes(file, new byte[bytes]);
        var t = Now.AddDays(-daysAgo);
        File.SetLastWriteTimeUtc(file, t);
        Directory.SetLastWriteTimeUtc(dir, t);
        return dir;
    }

    [Fact]
    public void DeletesFoldersNotUsedForMoreThanMaxAge()
    {
        var old = Make("aaaaaaaaaaaaaaaa", 100, 31);
        var recent = Make("bbbbbbbbbbbbbbbb", 100, 29);
        var r = FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 1_000_000, Now);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.Equal(1, r.DeletedFolders);
        Assert.Equal(100, r.DeletedBytes);
        Assert.Equal(100, r.RemainingBytes);
    }

    [Fact]
    public void WhenOverLimitDeletesOldestFirstUntilUnderLimit()
    {
        var oldest = Make("1111111111111111", 400, 10);
        var middle = Make("2222222222222222", 400, 5);
        var newest = Make("3333333333333333", 400, 1);
        var r = FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 900, Now);
        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
        Assert.Equal(800, r.RemainingBytes);
    }

    [Fact]
    public void KeepsEverythingWhenWithinAgeAndLimit()
    {
        Make("1111111111111111", 100, 1);
        Make("2222222222222222", 100, 2);
        var r = FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 1000, Now);
        Assert.Equal(0, r.DeletedFolders);
        Assert.Equal(200, r.RemainingBytes);
    }

    [Fact]
    public void RecentFileInsideKeepsFolderAlive()
    {
        var dir = Make("aaaaaaaaaaaaaaaa", 100, 90);
        var f = Path.Combine(dir, "f2000");
        File.WriteAllBytes(f, new byte[10]);
        File.SetLastWriteTimeUtc(f, Now.AddDays(-1));
        FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 1_000_000, Now);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void LeavesOtherFoldersAndRootFilesAlone()
    {
        var other = Path.Combine(_root, "my-notes");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x"), "x");
        Directory.SetLastWriteTimeUtc(other, Now.AddDays(-400));
        var rootFile = Path.Combine(_root, "readme");
        File.WriteAllText(rootFile, "x");
        File.SetLastWriteTimeUtc(rootFile, Now.AddDays(-400));
        FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 0, Now);
        Assert.True(Directory.Exists(other));
        Assert.True(File.Exists(rootFile));
    }

    [Fact]
    public void InUseFolderIsKeptAndReportedAsFailed()
    {
        var dir = Make("aaaaaaaaaaaaaaaa", 100, 60);
        using var locked = new FileStream(Path.Combine(dir, "t1000_w240"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var r = FrameCacheCleaner.Cleanup(_root, TimeSpan.FromDays(30), 1_000_000, Now);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(1, r.FailedFolders);
    }

    [Fact]
    public void MissingRootIsNotAnError()
    {
        var r = FrameCacheCleaner.Cleanup(Path.Combine(_root, "none"), TimeSpan.FromDays(30), 100, Now);
        Assert.Equal(0, r.DeletedFolders);
    }
}
