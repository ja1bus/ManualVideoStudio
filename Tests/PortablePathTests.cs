using System.Text.Json;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>
/// v1.10: 別の PC（ユーザー名だけ違う同じ場所）で手順書を作るための「~/…」のパスと、
/// カットの確定で未保存の編集を保存するときの保存先。
/// </summary>
public sealed class PortablePathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-portable-" + Guid.NewGuid().ToString("N"));

    public PortablePathTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void PathUnderHomeBecomesTildeWithSlashes()
    {
        Assert.Equal("~/Videos/ManualVideoStudio/20260926_1200_初期設定/tejunsho",
            PortablePath.FromHome(@"C:\Users\taro\Videos\ManualVideoStudio\20260926_1200_初期設定\tejunsho", @"C:\Users\taro"));
        Assert.Equal("~/Videos", PortablePath.FromHome(@"c:\users\TARO\Videos", @"C:\Users\taro"));   // 大文字・小文字は区別しない
        Assert.Equal("~", PortablePath.FromHome(@"C:\Users\taro\", @"C:\Users\taro"));
    }

    [Fact]
    public void PathOutsideHomeStaysFull()
    {
        Assert.Equal(@"D:\work\tejunsho", PortablePath.FromHome(@"D:\work\tejunsho", @"C:\Users\taro"));
        Assert.Equal(@"C:\Users\taro2\Videos", PortablePath.FromHome(@"C:\Users\taro2\Videos", @"C:\Users\taro"));   // 名前の前方一致は中ではない
        Assert.Equal(@"C:\Users\Public\Videos", PortablePath.FromHome(@"C:\Users\Public\Videos", @"C:\Users\taro"));
        Assert.Equal("", PortablePath.FromHome("", @"C:\Users\taro"));
    }

    [Fact]
    public void DefaultRequestUsesHomeRelativeDirectoryAndProjectName()
    {
        var dir = Path.Combine(Home, "Videos", "ManualVideoStudio", "20260926_1200_初期設定", "tejunsho");
        Assert.Equal("~/Videos/ManualVideoStudio/20260926_1200_初期設定/tejunsho の ManualVideoStudio の出力（プロジェクト「初期設定」）から tejunsho-gui-docx で手順書を作って",
            TejunshoExport.RequestText(dir, "初期設定"));
        // {{OUTPUT_DIR}} は今までどおりフルパス（利用者が編集した依頼文はそのまま動く）。
        var r = TejunshoExport.RenderRequest("{{OUTPUT_DIR}} | {{OUTPUT_DIR_HOME}}", dir, "初期設定", 3);
        Assert.Equal(dir + " | ~/Videos/ManualVideoStudio/20260926_1200_初期設定/tejunsho", r.Text);
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void MetadataSourcePathDoesNotKeepUserName()
    {
        var video = Path.Combine(Home, "Videos", "ManualVideoStudio", "20260926_1200_初期設定", "rec_20260926_120000.mp4");
        var points = new List<CapturePoint> { new() { Id = 1, Sequence = 1, Timestamp = 1, SelectedFrameTime = 1, Caption = "設定を開く" } };
        var plan = ImageExportService.Plan(points, Path.Combine(_dir, "images"), ImageFormat.Png);
        using var doc = JsonDocument.Parse(ImageExportService.BuildMetadataJson(video, plan, null));
        Assert.Equal("~/Videos/ManualVideoStudio/20260926_1200_初期設定/rec_20260926_120000.mp4", doc.RootElement.GetProperty("sourcePath").GetString());
        Assert.Equal("rec_20260926_120000.mp4", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("001_設定を開く.png", doc.RootElement.GetProperty("captures")[0].GetProperty("path").GetString());   // 画像は相対パス
    }

    [Fact]
    public void FirstSavePathIsFolderNameInProjectFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "20260926_1200_初期設定")).FullName;
        var video = Path.Combine(folder, "rec_20260926_120000.mp4");
        Assert.Equal(Path.Combine(folder, "20260926_1200_初期設定" + ProjectService.Extension), ProjectFolders.FirstSavePath(video, _dir));
    }

    [Fact]
    public void FirstSavePathIsNullWhenAskingIsNeeded()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "20260926_1200_初期設定")).FullName;
        var video = Path.Combine(folder, "rec_20260926_120000.mp4");
        // 動画がプロジェクトフォルダー（root の直下のフォルダー）に無い。
        Assert.Null(ProjectFolders.FirstSavePath(Path.Combine(_dir, "rec.mp4"), _dir));
        Assert.Null(ProjectFolders.FirstSavePath(video, Path.Combine(_dir, "other-root")));
        Assert.Null(ProjectFolders.FirstSavePath("", _dir));
        // 同じ録画の編集が edits にある。
        var edit = Directory.CreateDirectory(Path.Combine(folder, ProjectFolders.EditsFolderName, "20260926_1300_別案")).FullName;
        File.WriteAllText(Path.Combine(edit, "20260926_1300_別案" + ProjectService.Extension), "{}");
        Assert.Null(ProjectFolders.FirstSavePath(video, _dir));
    }
}
