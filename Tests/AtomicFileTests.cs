using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>一時ファイル経由の保存（v1.8.1）: 使用中のときのやり直し・一時ファイルを残さない・古い一時ファイルの片付け。</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string[] Temps(string name) => Directory.GetFiles(_dir, AtomicFile.TempPrefix(name) + "*");

    /// <summary>置き換え先を削除共有なしで開き、ほかのプロセス（ウイルス対策ソフト等）が使用中の状態を作る。</summary>
    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task RetriesWhileTargetIsInUseThenSucceeds()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        File.WriteAllText(path, "old");
        var locked = Lock(path);
        var release = Task.Run(async () => { await Task.Delay(250); locked.Dispose(); });
        AtomicFile.WriteAllText(path, "new", [100, 200, 300, 500, 900]);
        await release;
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Temps("p.mvsproj.json"));
    }

    [Fact]
    public void GivesUpWithInUseErrorAndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        File.WriteAllText(path, "old");
        using (Lock(path))
        {
            var ex = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new", [10, 10]));
            Assert.True(AtomicFile.IsInUse(ex), ex.ToString());   // 後始末の失敗ではなく、置き換えの失敗が伝わる
        }
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Temps("p.mvsproj.json"));
    }

    [Fact]
    public void CleansUpOldTempFilesOfTheSameFileOnly()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        var old = Path.Combine(_dir, ".p.mvsproj.json.0123456789abcdef.tmp");
        var fresh = Path.Combine(_dir, ".p.mvsproj.json.fedcba9876543210.tmp");
        var other = Path.Combine(_dir, ".q.mvsproj.json.0123456789abcdef.tmp");
        foreach (var f in new[] { old, fresh, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(-1));

        AtomicFile.WriteAllText(path, "new");
        Assert.False(File.Exists(old));      // 前回までの失敗の残り
        Assert.True(File.Exists(fresh));     // 書き込み中かもしれない新しいものは残す
        Assert.True(File.Exists(other));     // 別のファイルの一時ファイルには触れない
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void ClassifiesInUseErrors()
    {
        Assert.True(AtomicFile.IsInUse(new IOException("x", unchecked((int)0x80070020))));   // 共有違反
        Assert.True(AtomicFile.IsInUse(new IOException("x", unchecked((int)0x80070498))));   // ERROR_UNABLE_TO_MOVE_REPLACEMENT
        Assert.True(AtomicFile.IsInUse(new UnauthorizedAccessException()));
        Assert.False(AtomicFile.IsInUse(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(AtomicFile.IsInUse(new FileNotFoundException()));
        Assert.False(AtomicFile.IsInUse(new InvalidOperationException()));
    }

    [Fact]
    public void ProjectSaveInUseShowsAdviceAndFullPath()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        var service = new ProjectService();
        service.Save(TestProjects.Create(), path);
        using (Lock(path))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => service.Save(TestProjects.Create(), path));
            Assert.Contains("ほかのアプリ（ウイルス対策ソフトなど）がファイルを使用中です", ex.Message);
            Assert.Contains("ファイル: " + path, ex.Message);
        }
        Assert.Empty(Temps("p.mvsproj.json"));
    }
}
