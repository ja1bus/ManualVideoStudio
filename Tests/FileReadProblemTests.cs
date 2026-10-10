using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>読めないファイルの案内（v1.4.2。暗号化・破損）。</summary>
public class FileReadProblemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-read-" + Guid.NewGuid().ToString("N"));

    public FileReadProblemTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] Encrypted = "ENCRYPTED-HEADER-0123456789"u8.ToArray();

    [Theory]
    [InlineData("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A })]
    [InlineData("a.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("a.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData("a.mp4", new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D })]
    [InlineData("a.mkv", new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01 })]
    [InlineData("a.json", new byte[] { 0xEF, 0xBB, 0xBF, 0x0D, 0x0A, 0x20, 0x7B, 0x7D })]
    public void MatchingSignatureIsMatch(string name, byte[] head)
        => Assert.Equal(FileReadProblem.SignatureCheck.Match, FileReadProblem.Check(Write(name, head)));

    [Theory]
    [InlineData("a.png")]
    [InlineData("a.jpg")]
    [InlineData("a.gif")]
    [InlineData("a.mp4")]
    [InlineData("a.json")]
    public void OtherContentIsMismatch(string name)
        => Assert.Equal(FileReadProblem.SignatureCheck.Mismatch, FileReadProblem.Check(Write(name, Encrypted)));

    [Fact]
    public void UnknownExtensionAndMissingFileAreUnknown()
    {
        Assert.Equal(FileReadProblem.SignatureCheck.Unknown, FileReadProblem.Check(Write("a.ass", Encrypted)));
        Assert.Equal(FileReadProblem.SignatureCheck.Unknown, FileReadProblem.Check(Path.Combine(_dir, "none.png")));
    }

    [Fact]
    public void LockedFileIsUnreadable()
    {
        var path = Write("locked.png", Encrypted);
        using var lockIt = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(FileReadProblem.SignatureCheck.Unreadable, FileReadProblem.Check(path));
    }

    [Fact]
    public void MessageHasHeadlineHintAndFullPath()
    {
        var path = Write("画像 001.png", Encrypted);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        var m = FileReadProblem.Message("画像を読み込めませんでした。", relative);
        var lines = m.Split(Environment.NewLine);
        Assert.Equal("画像を読み込めませんでした。", lines[0]);
        Assert.Contains("PNG の形式になっていません", m);
        Assert.Contains("暗号化されている場合は解除してから", m);
        Assert.Equal("ファイル: " + path, lines[^1]); // 相対パスで渡してもフルパスを表示する
    }

    [Fact]
    public void BrokenProjectShowsHintAndFullPath()
    {
        var path = Write("p.mvsproj.json", Encrypted);
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectService().Load(path));
        Assert.StartsWith("プロジェクトを読み込めませんでした。", ex.Message);
        Assert.Contains("JSON の形式になっていません", ex.Message);
        Assert.EndsWith("ファイル: " + path, ex.Message);
    }

    [Fact]
    public void BrokenSettingsAndTemplatesAreReportedAfterStartup()
    {
        FileLoadProblems.TakeAll();
        var settings = Write("settings.json", Encrypted);
        var templates = Write("prompt-templates.json", Encrypted);
        new SettingsService(settings).Load();
        _ = new PromptTemplateService(templates);
        var messages = FileLoadProblems.TakeAll();
        Assert.Contains(messages, m => m.StartsWith("設定を読み込めなかったため") && m.EndsWith("ファイル: " + settings));
        Assert.Contains(messages, m => m.StartsWith("プロンプトテンプレートを読み込めなかったため") && m.EndsWith("ファイル: " + templates));
        Assert.Empty(FileLoadProblems.TakeAll());
        Assert.True(File.Exists(settings + ".bad"));
    }
}
