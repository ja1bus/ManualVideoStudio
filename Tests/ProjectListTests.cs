using System.IO.Compression;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>一時フォルダーに保存先ルートを作り、テストの後で消す。</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Base = Path.Combine(Path.GetTempPath(), "mvs-list-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Base, "root");
        Directory.CreateDirectory(Root);
    }

    public string Base { get; }
    public string Root { get; }

    /// <summary>録画のフォルダー（動画 1 つ）を作る。</summary>
    public string Recording(string folderName, string video = "rec_20260923_101500.mp4", int bytes = 2048)
    {
        var dir = Path.Combine(Root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, video), Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
        return dir;
    }

    /// <summary>プロジェクトファイルを保存する（ProjectService で書く）。</summary>
    public static string SaveProject(string path, string videoPath, Action<ProjectDocument>? edit = null)
    {
        var p = TestProjects.Create(60);
        p.SourceVideo = new SourceVideo { Path = videoPath, FileSize = new FileInfo(videoPath).Length };
        edit?.Invoke(p);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new ProjectService().Save(p, path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class ProjectFoldersV17Tests
{
    [Fact]
    public void DisplayNameAndStamp()
    {
        Assert.Equal("ルーター設定", ProjectFolders.DisplayName("20260923_1015_ルーター設定"));
        Assert.Equal("ルーター設定_2", ProjectFolders.DisplayName("20260923_1015_ルーター設定_2"));
        Assert.Equal("2026/09/23 10:15", ProjectFolders.DisplayName("20260923_1015"));
        Assert.Equal("手順", ProjectFolders.DisplayName("手順"));
        Assert.Equal(new DateTime(2026, 9, 23, 10, 15, 0), ProjectFolders.TryParseStamp("20260923_1015_x"));
        Assert.Null(ProjectFolders.TryParseStamp("2026-09-23"));
    }

    [Fact]
    public void DefaultOutputFolderSeparatesEditsFromTheFirstEdit()
    {
        const string video = @"C:\root\20260923_1015_a\rec.mp4";
        // 1 つ目の編集（録画のフォルダーの直下）・未保存・保存先の外は従来どおり動画のフォルダー。
        Assert.Equal(@"C:\root\20260923_1015_a", ProjectFolders.DefaultOutputFolder(video, @"C:\root\20260923_1015_a\20260923_1015_a.mvsproj.json"));
        Assert.Equal(@"C:\root\20260923_1015_a", ProjectFolders.DefaultOutputFolder(video, null));
        Assert.Equal(@"C:\root\20260923_1015_a", ProjectFolders.DefaultOutputFolder(video, @"D:\other\x.mvsproj.json"));
        Assert.Equal("rec", ProjectFolders.DefaultOutputBaseName(video, null));
        // edits\ の編集はそのフォルダーに、プロジェクト名で出す。
        var edit = @"C:\root\20260923_1015_a\edits\20260925_0930_管理者向け\20260925_0930_管理者向け.mvsproj.json";
        Assert.Equal(@"C:\root\20260923_1015_a\edits\20260925_0930_管理者向け", ProjectFolders.DefaultOutputFolder(video, edit));
        Assert.Equal("20260925_0930_管理者向け", ProjectFolders.DefaultOutputBaseName(video, edit));
        Assert.Equal(@"C:\root\20260923_1015_a\edits\20260925_0930_管理者向け\20260925_0930_管理者向け_edited.mp4",
            VideoExportService.DefaultOutputPath(video, new ExportSettings(), edit));
        Assert.Equal(@"C:\root\20260923_1015_a\edits\20260925_0930_管理者向け\20260925_0930_管理者向け_edited.gif",
            GifExportService.DefaultOutputPath(video, new GifExportSettings(), edit));
        Assert.Equal(@"C:\root\20260923_1015_a\edits\20260925_0930_管理者向け\images",
            ImageExportService.DefaultDirectory(video, new ExportSettings(), edit));
        // 固定の出力先を指定していればそちら。
        Assert.Equal(@"D:\out\20260925_0930_管理者向け_edited.mp4",
            VideoExportService.DefaultOutputPath(video, new ExportSettings { VideoOutputDirectory = @"D:\out" }, edit));
    }

    [Fact]
    public void RecordingFolderOfAndIsUnder()
    {
        using var t = new TempRoot();
        var rec = t.Recording("20260923_1015_a");
        Assert.Equal(rec, ProjectFolders.RecordingFolderOf(Path.Combine(rec, "edits", "x", "y.mvsproj.json"), t.Root));
        Assert.Equal(rec, ProjectFolders.RecordingFolderOf(rec, t.Root));
        Assert.Null(ProjectFolders.RecordingFolderOf(t.Root, t.Root));
        Assert.Null(ProjectFolders.RecordingFolderOf(Path.Combine(t.Base, "other"), t.Root));
        Assert.True(ProjectFolders.IsUnder(rec, t.Root));
        Assert.False(ProjectFolders.IsUnder(t.Root, t.Root));
        Assert.False(ProjectFolders.IsUnder(t.Root + "2", t.Root));
    }

    [Fact]
    public void FindVideoAndProjectFiles()
    {
        using var t = new TempRoot();
        var rec = t.Recording("20260923_1015_a");
        File.WriteAllText(Path.Combine(rec, "rec_20260923_101500_edited.mp4"), "x");
        Assert.Equal(Path.Combine(rec, "rec_20260923_101500.mp4"), ProjectFolders.FindVideo(rec));
        var video = Path.Combine(rec, "rec_20260923_101500.mp4");
        var first = TempRoot.SaveProject(Path.Combine(rec, "20260923_1015_a.mvsproj.json"), video);
        var second = TempRoot.SaveProject(Path.Combine(ProjectFolders.CreateEditFolder(rec, new DateTime(2026, 9, 25, 9, 30, 0), "管理者向け"), "p.mvsproj.json"), video);
        Assert.Equal([first, second], ProjectFolders.ProjectFilesIn(rec));
        Assert.EndsWith(@"edits\20260925_0930_管理者向け\p.mvsproj.json", second);
    }
}

public class ProjectIndexTests
{
    [Fact]
    public void RebuildListsRecordingsEditsAndProblems()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_ルーター設定");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        TempRoot.SaveProject(Path.Combine(a, "20260923_1015_ルーター設定.mvsproj.json"), video);
        TempRoot.SaveProject(Path.Combine(a, "edits", "20260925_0930_管理者向け", "20260925_0930_管理者向け.mvsproj.json"), video);
        var b = t.Recording("20260920_0900_経費精算");                       // 編集なし
        var c = Path.Combine(t.Root, "20260918_0800_勤怠");                  // 動画が無い（編集だけ）
        TempRoot.SaveProject(Path.Combine(c, "20260918_0800_勤怠.mvsproj.json"), video, p => p.SourceVideo.Path = @"Z:\none\rec.mp4");
        File.WriteAllText(Path.Combine(c, "20260918_0800_勤怠.mvsproj.json"), "{ broken");
        Directory.CreateDirectory(Path.Combine(t.Root, "空のフォルダー"));    // 録画ではない
        Directory.CreateDirectory(Path.Combine(t.Root, ".importing-x"));     // 作業用
        t.Recording(".importing-y");

        var store = new ProjectIndexStore(t.Root);
        var index = store.LoadOrRebuild(out var rebuilt);
        Assert.True(rebuilt);
        Assert.True(File.Exists(Path.Combine(t.Root, ProjectIndexStore.FileName)));
        Assert.Equal(["20260923_1015_ルーター設定", "20260920_0900_経費精算", "20260918_0800_勤怠"], index.Recordings.Select(r => r.Folder));
        var ra = index.Recordings[0];
        Assert.Equal("ルーター設定", ra.Name);
        Assert.Equal("rec_20260923_101500.mp4", ra.Video);
        Assert.Equal(["ルーター設定", "管理者向け"], ra.Edits.Select(e => e.Name));
        Assert.All(ra.Edits, e => Assert.Equal("", e.Problem));
        Assert.Equal(@"edits\20260925_0930_管理者向け\20260925_0930_管理者向け.mvsproj.json", ra.Edits[1].Path);
        Assert.Empty(index.Recordings[1].Edits);
        Assert.Equal(ProjectIndexStore.NoVideoProblem, index.Recordings[2].Problem);
        Assert.Equal(ProjectIndexStore.EditUnreadableProblem, index.Recordings[2].Edits[0].Problem);

        // 2 回目は管理ファイルを読むだけ（フォルダーを消しても、更新するまでは残る）。
        Directory.Delete(b, recursive: true);
        var again = store.LoadOrRebuild(out rebuilt);
        Assert.False(rebuilt);
        Assert.Equal(3, again.Recordings.Count);
        Assert.Equal(2, store.Rebuild().Recordings.Count);
    }

    [Fact]
    public void TouchUpdatesOnlyThatRecordingAndRemovesMissing()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var b = t.Recording("20260924_1015_b");
        var store = new ProjectIndexStore(t.Root);
        store.Rebuild();
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var p = TempRoot.SaveProject(Path.Combine(a, "edits", "20260925_0930_x", "20260925_0930_x.mvsproj.json"), video);
        store.Touch(p);
        var index = store.TryLoad()!;
        Assert.Equal("x", Assert.Single(index.Recordings.Single(r => r.Folder == "20260923_1015_a").Edits).Name);

        Directory.Delete(b, recursive: true);
        store.Touch(b);
        Assert.DoesNotContain(store.TryLoad()!.Recordings, r => r.Folder == "20260924_1015_b");
        store.Remove(a);
        Assert.Empty(store.TryLoad()!.Recordings);
        // 保存先の外・管理ファイル自身は何もしない。
        store.Touch(Path.Combine(t.Base, "outside.mp4"));
        store.Touch(store.IndexPath);
        Assert.Empty(store.TryLoad()!.Recordings);
    }

    [Fact]
    public void BrokenIndexIsRebuilt()
    {
        using var t = new TempRoot();
        t.Recording("20260923_1015_a");
        File.WriteAllText(Path.Combine(t.Root, ProjectIndexStore.FileName), "{ not json");
        var index = new ProjectIndexStore(t.Root).LoadOrRebuild(out var rebuilt);
        Assert.True(rebuilt);
        Assert.Single(index.Recordings);
    }

    [Fact]
    public void CheckProjectFindsVideoByRelativePath()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var p = TempRoot.SaveProject(Path.Combine(a, "edits", "e", "e.mvsproj.json"), video);
        Assert.Equal("", ProjectIndexStore.CheckProject(p));
        // 絶対パスが古くても、相対パスで見つかれば問題なし。
        File.WriteAllText(p, File.ReadAllText(p).Replace(video.Replace(@"\", @"\\"), @"Z:\\gone\\rec.mp4"));
        Assert.Equal("", ProjectIndexStore.CheckProject(p));
        File.Move(video, video + ".bak");
        Assert.Equal(ProjectIndexStore.EditVideoMissingProblem, ProjectIndexStore.CheckProject(p));
    }
}

public class ProjectCatalogTests
{
    [Fact]
    public void PrepareCopyResetsOutputsAndOptionallyRestartsFromCuts()
    {
        var p = TestProjects.Create(60);
        p.EditPhase = EditPhase.Subtitle;
        p.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
        p.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "字幕" });
        p.ShapeGroups.Add(new ShapeGroup { Start = 1, End = 2, ImageWidth = 1920, ImageHeight = 1080, Shapes = [new RectangleAnnotation { Width = 10, Height = 10 }] });
        p.CapturePoints.Add(new CapturePoint { Id = 1, Sequence = 1, OutputFilename = "001_a.png" });
        p.ExportSettings.VideoOutputDirectory = @"C:\old";
        p.ExportSettings.VideoOutputFileName = "old.mp4";
        p.ExportSettings.ImageOutputDirectory = @"C:\old\images";
        p.ExportGifSettings.OutputDirectory = @"C:\old";
        p.ExportGifSettings.OutputFileName = "old.gif";
        var defaults = new ExportSettings { VideoOutputDirectory = @"D:\fixed" };

        var copy = ProjectCatalog.PrepareCopy(p, restartFromCuts: false, defaults);
        Assert.Single(copy.CutSegments);
        Assert.Single(copy.SubtitleSegments);
        Assert.Single(copy.ShapeGroups);
        Assert.Equal(EditPhase.Subtitle, copy.EditPhase);
        Assert.Equal(@"D:\fixed", copy.ExportSettings.VideoOutputDirectory);
        Assert.Equal("", copy.ExportSettings.VideoOutputFileName);
        Assert.Equal("", copy.ExportSettings.ImageOutputDirectory);
        Assert.Equal("", copy.ExportGifSettings.OutputDirectory);
        Assert.Equal("", copy.ExportGifSettings.OutputFileName);
        Assert.Equal("", copy.CapturePoints[0].OutputFilename);
        // 元は変えない。
        Assert.Equal("001_a.png", p.CapturePoints[0].OutputFilename);
        Assert.Equal(@"C:\old", p.ExportSettings.VideoOutputDirectory);

        var restart = ProjectCatalog.PrepareCopy(p, restartFromCuts: true, defaults);
        Assert.Single(restart.CutSegments);
        Assert.Empty(restart.SubtitleSegments);
        Assert.Empty(restart.ShapeGroups);
        Assert.Equal(EditPhase.Cut, restart.EditPhase);
        Assert.Single(restart.CapturePoints);
    }

    [Fact]
    public void RenameEditInEditsFolderMovesFolderAndRewritesOutputPaths()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var dir = Path.Combine(a, "edits", "20260925_0930_旧名");
        var p = TempRoot.SaveProject(Path.Combine(dir, "20260925_0930_旧名.mvsproj.json"), video, d =>
        {
            d.ExportSettings.VideoOutputDirectory = dir;
            d.ExportSettings.ImageOutputDirectory = Path.Combine(dir, "images");
            d.ExportGifSettings.OutputDirectory = @"D:\elsewhere";
        });

        var renamed = ProjectCatalog.RenameEdit(p, "新しい名前");
        var newDir = Path.Combine(a, "edits", "20260925_0930_新しい名前");
        Assert.Equal(Path.Combine(newDir, "20260925_0930_新しい名前.mvsproj.json"), renamed);
        Assert.False(Directory.Exists(dir));
        var loaded = new ProjectService().Load(renamed);
        Assert.True(loaded.VideoFound);
        Assert.Equal(video, loaded.Project.SourceVideo.Path);
        Assert.Equal(newDir, loaded.Project.ExportSettings.VideoOutputDirectory);
        Assert.Equal(Path.Combine(newDir, "images"), loaded.Project.ExportSettings.ImageOutputDirectory);
        Assert.Equal(@"D:\elsewhere", loaded.Project.ExportGifSettings.OutputDirectory);

        // 同じ名前があれば変えない。
        Directory.CreateDirectory(Path.Combine(a, "edits", "20260925_0930_重複"));
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalog.RenameEdit(renamed, "重複"));
        Assert.Contains("同じ名前", ex.Message);
        Assert.True(File.Exists(renamed));
    }

    [Fact]
    public void RenameFirstEditRenamesOnlyTheProjectFile()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var p = TempRoot.SaveProject(Path.Combine(a, "20260923_1015_a.mvsproj.json"), video);
        var renamed = ProjectCatalog.RenameEdit(p, "初心者向け");
        Assert.Equal(Path.Combine(a, "20260923_1015_初心者向け.mvsproj.json"), renamed);
        Assert.True(File.Exists(video));
        Assert.True(new ProjectService().Load(renamed).VideoFound);
    }

    [Fact]
    public void DeletionTargetsStayInsideTheRoot()
    {
        const string rec = @"C:\root\20260923_1015_a";
        Assert.Equal(rec + @"\edits\e", ProjectCatalog.EditDeletionTarget(rec + @"\edits\e\e.mvsproj.json"));
        Assert.Equal(rec + @"\x.mvsproj.json", ProjectCatalog.EditDeletionTarget(rec + @"\x.mvsproj.json"));
        Assert.True(ProjectCatalog.IsSafeToDelete(rec, @"C:\root"));
        Assert.False(ProjectCatalog.IsSafeToDelete(@"C:\root", @"C:\root"));
        Assert.False(ProjectCatalog.IsSafeToDelete(@"C:\root\", @"C:\root"));
        Assert.False(ProjectCatalog.IsSafeToDelete(@"C:\other\x", @"C:\root"));
        Assert.False(ProjectCatalog.IsSafeToDelete(@"C:\root2\x", @"C:\root"));
    }
}

public class ProjectArchiveTests
{
    private static (TempRoot T, string Folder, string Video, string Edit2) Sample()
    {
        var t = new TempRoot();
        var a = t.Recording("20260923_1015_ルーター設定", bytes: 300_000);
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        TempRoot.SaveProject(Path.Combine(a, "20260923_1015_ルーター設定.mvsproj.json"), video, d => d.ExportSettings.VideoOutputDirectory = a);
        var dir = Path.Combine(a, "edits", "20260925_0930_管理者向け");
        var edit2 = TempRoot.SaveProject(Path.Combine(dir, "20260925_0930_管理者向け.mvsproj.json"), video, d => d.ExportSettings.ImageOutputDirectory = Path.Combine(dir, "images"));
        Directory.CreateDirectory(Path.Combine(dir, "images"));
        File.WriteAllText(Path.Combine(dir, "images", "capture-metadata.json"), "{}");
        return (t, a, video, edit2);
    }

    [Fact]
    public void DefaultFileName()
    {
        Assert.Equal("ルーター設定_20260923_1015.zip", ProjectArchive.DefaultFileName("20260923_1015_ルーター設定"));
        Assert.Equal("20260923_1015.zip", ProjectArchive.DefaultFileName("20260923_1015"));
        Assert.Equal("手順.zip", ProjectArchive.DefaultFileName("手順"));
    }

    [Fact]
    public async Task ExportVerifyAndImportToAnotherRootRewritesPaths()
    {
        var (t, folder, video, _) = Sample();
        using (t)
        {
            var zip = Path.Combine(t.Base, "backup.zip");
            double last = 0;
            await new ProjectArchive().ExportAsync(folder, zip, "1.7.0", new SyncProgress(r => last = r), CancellationToken.None);
            Assert.True(File.Exists(zip));
            Assert.False(File.Exists(zip + ".part"));
            Assert.Equal(1.0, last, 3);
            Assert.Null(ProjectArchive.Verify(zip, folder));
            using (var z = ZipFile.OpenRead(zip))
            {
                Assert.Contains(z.Entries, e => e.FullName == "20260923_1015_ルーター設定/rec_20260923_101500.mp4");
                Assert.Contains(z.Entries, e => e.FullName == "20260923_1015_ルーター設定/edits/20260925_0930_管理者向け/images/capture-metadata.json");
                var m = ProjectArchive.ReadManifest(z, zip);
                Assert.Equal(folder, m.SourceFolder);
                Assert.Equal(4, m.FileCount); // 動画・プロジェクト 2 つ・画像のメタデータ
            }

            // フォルダーの中身が変わったら確認は失敗する。
            File.WriteAllText(Path.Combine(folder, "added.txt"), "x");
            Assert.NotNull(ProjectArchive.Verify(zip, folder));
            File.Delete(Path.Combine(folder, "added.txt"));

            // 別の保存先へ取り込むと、元の場所に動画が残っていても取り込んだ動画を使う。
            var root2 = Path.Combine(t.Base, "root2");
            var imported = await new ProjectArchive().ImportAsync(zip, root2, null, CancellationToken.None);
            Assert.Equal(Path.Combine(root2, "20260923_1015_ルーター設定"), imported);
            var files = ProjectFolders.ProjectFilesIn(imported);
            Assert.Equal(2, files.Count);
            var first = new ProjectService().Load(files[0]).Project;
            Assert.Equal(Path.Combine(imported, "rec_20260923_101500.mp4"), first.SourceVideo.Path);
            Assert.Equal(imported, first.ExportSettings.VideoOutputDirectory);
            var second = new ProjectService().Load(files[1]).Project;
            Assert.Equal(Path.Combine(imported, "rec_20260923_101500.mp4"), second.SourceVideo.Path);
            Assert.Equal(Path.Combine(imported, "edits", "20260925_0930_管理者向け", "images"), second.ExportSettings.ImageOutputDirectory);
            Assert.Equal(new FileInfo(video).Length, new FileInfo(Path.Combine(imported, "rec_20260923_101500.mp4")).Length);
            Assert.Empty(Directory.GetDirectories(root2, ".importing-*"));

            // 同じ保存先へもう一度取り込むと「_2」を付ける。
            var again = await new ProjectArchive().ImportAsync(zip, root2, null, CancellationToken.None);
            Assert.Equal(Path.Combine(root2, "20260923_1015_ルーター設定_2"), again);
            Assert.Equal(Path.Combine(again, "rec_20260923_101500.mp4"), new ProjectService().Load(ProjectFolders.ProjectFilesIn(again)[0]).Project.SourceVideo.Path);
        }
    }

    [Fact]
    public async Task CanceledExportLeavesNoFile()
    {
        var (t, folder, _, _) = Sample();
        using (t)
        {
            var zip = Path.Combine(t.Base, "c.zip");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectArchive().ExportAsync(folder, zip, "1.7.0", null, cts.Token));
            Assert.False(File.Exists(zip));
            Assert.False(File.Exists(zip + ".part"));
        }
    }

    [Fact]
    public async Task ImportRejectsForeignAndUnsafeZips()
    {
        using var t = new TempRoot();
        var foreign = Path.Combine(t.Base, "foreign.zip");
        using (var z = ZipFile.Open(foreign, ZipArchiveMode.Create)) z.CreateEntry("a/b.txt");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectArchive().ImportAsync(foreign, t.Root, null, CancellationToken.None));
        Assert.Contains("エクスポートではない", ex.Message);
        Assert.Contains(foreign, ex.Message);

        var unsafeZip = Path.Combine(t.Base, "unsafe.zip");
        using (var z = ZipFile.Open(unsafeZip, ZipArchiveMode.Create))
        {
            using (var s = new StreamWriter(z.CreateEntry(ArchiveManifest.FileName).Open()))
                s.Write("""{ "format": "ManualVideoStudioExport", "schemaVersion": 1, "folder": "x" }""");
            z.CreateEntry("x/../../evil.txt");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectArchive().ImportAsync(unsafeZip, t.Root, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(t.Base, "evil.txt")));
        Assert.Empty(Directory.GetDirectories(t.Root));

        var badName = Path.Combine(t.Base, "badname.zip");
        using (var z = ZipFile.Open(badName, ZipArchiveMode.Create))
        {
            using var s = new StreamWriter(z.CreateEntry(ArchiveManifest.FileName).Open());
            s.Write("""{ "format": "ManualVideoStudioExport", "schemaVersion": 1, "folder": ".." }""");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectArchive().ImportAsync(badName, t.Root, null, CancellationToken.None));
    }

    /// <summary>Progress&lt;T&gt; は同期コンテキストに送るため、テストではその場で呼ぶ。</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public class ProjectListViewModelTests
{
    internal sealed class Settings(string root) : ISettingsService
    {
        public AppSettings Current { get; } = new() { Recording = new RecordingSettings { OutputDirectory = root } };
        public AppSettings Load() => Current;
        public void Save() { }
    }

    internal sealed class Dialogs : IDialogService
    {
        public List<string> Messages { get; } = [];
        public string? SaveFileResult { get; set; }
        public void ShowInfo(string message, string title = "情報") => Messages.Add("info:" + message);
        public void ShowWarning(string message, string title = "注意") => Messages.Add("warn:" + message);
        public void ShowError(string message, string? detail = null, string title = "エラー") => Messages.Add("error:" + message);
        public bool Confirm(string message, string title = "確認") => false;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => null;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存") => SaveFileResult;
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    internal sealed class Windows : IWindowService
    {
        public bool DangerResult { get; set; } = true;
        public List<string> DangerMessages { get; } = [];
        public string? PromptResult { get; set; }
        public Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null) => throw new NotSupportedException();
        public void ShowAiAssist() { }
        public void CloseAiAssist() => AiClosed++;
        public int AiClosed;
        public double? ChooseFrame(FrameCandidatesViewModel vm) => null;
        public bool EditCut(CutEditViewModel vm) => false;
        public void EditImage(ImageEditorViewModel vm) { }
        public string? PromptText(string title, string message, string initial, bool singleLine = false) => PromptResult;
        public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
        {
            await work(new Progress<ProgressInfo>(), CancellationToken.None);
            return true;
        }
        public bool EditSubtitle(SubtitleEditViewModel vm) => false;
        public bool ChooseGifQuality(GifExportViewModel vm) => false;
        public void ShowAbout(AboutViewModel vm) { }
        public bool EditTitle(TitleEditViewModel vm) => false;
        public bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel)
        {
            DangerMessages.Add(message);
            return DangerResult;
        }
        public Task<RecordingOutcome?> ShowRecorderAsync(AppendRecordingContext? append = null) => Task.FromResult<RecordingOutcome?>(null);
        public bool IsRecorderOpen => false;
        public void ActivateRecorder() { }
        public ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath) => null;
        public void ShowTejunshoExportDone(TejunshoExportDone result) { }
        public int GeminiOpened;
        public void ShowGeminiCaption(GeminiCaptionViewModel vm) => GeminiOpened++;
        public bool? ConfirmDeleteRequestVideo(string message, string path) => true;
    }

    /// <summary>ごみ箱の代わりに、ルートの外の trash フォルダーへ移す。</summary>
    internal sealed class Trash(string dir) : IRecycleBin
    {
        public List<string> Moved { get; } = [];
        public void MoveToRecycleBin(string path)
        {
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path)) Directory.Move(path, dest);
            else File.Move(path, dest);
            Moved.Add(path);
        }
    }

    private sealed class Shell : IShellService
    {
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) { }
        public void OpenUrl(string url) { }
    }

    private static (ProjectListViewModel Vm, Dialogs D, Windows W, Trash Trash) Create(TempRoot t)
    {
        var d = new Dialogs();
        var w = new Windows();
        var trash = new Trash(Path.Combine(t.Base, "trash"));
        var vm = new ProjectListViewModel(new Settings(t.Root), d, w, new Shell(), trash, NullLogger<ProjectListViewModel>.Instance);
        return (vm, d, w, trash);
    }

    private static (string A, string Video, string First, string Second) Sample(TempRoot t)
    {
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var first = TempRoot.SaveProject(Path.Combine(a, "20260923_1015_a.mvsproj.json"), video);
        var second = TempRoot.SaveProject(Path.Combine(a, "edits", "20260925_0930_b", "20260925_0930_b.mvsproj.json"), video);
        t.Recording("20260920_0900_c");
        return (a, video, first, second);
    }

    [Fact]
    public void RowsSearchAndActions()
    {
        using var t = new TempRoot();
        var (a, _, first, second) = Sample(t);
        var (vm, _, _, _) = Create(t);
        vm.Load();
        Assert.Equal(["a", "a", "b", "c"], vm.Rows.Select(r => r.Name));
        Assert.Equal("（編集なし）", vm.Rows[3].Note);
        vm.SearchText = "b";
        Assert.Equal(["a", "b"], vm.Rows.Select(r => r.Name));
        vm.SearchText = "";

        ProjectListAction? closed = null;
        vm.CloseRequested += x => closed = x;
        vm.SetSelection([vm.Rows[0]]);
        Assert.False(vm.DuplicateCommand.CanExecute(null)); // 録画の行は複製できない
        vm.OpenCommand.Execute(null);                        // 録画の行: 最後に更新した編集
        Assert.Equal(ProjectListActionKind.OpenProject, closed!.Kind);

        vm.SetSelection([vm.Rows[3]]);                       // 編集の無い録画: 動画を開く
        vm.OpenCommand.Execute(null);
        Assert.Equal(ProjectListActionKind.OpenVideo, closed!.Kind);

        vm.SetSelection([vm.Rows[2]]);
        vm.DuplicateFromCutsCommand.Execute(null);
        Assert.Equal(new ProjectListAction(ProjectListActionKind.DuplicateFromCuts, second), closed);
        vm.NewEditCommand.Execute(null);
        Assert.Equal(new ProjectListAction(ProjectListActionKind.NewEdit, a), closed);
    }

    [Fact]
    public void DeleteMovesToTrashAndProtectsTheOpenProject()
    {
        using var t = new TempRoot();
        var (a, video, first, second) = Sample(t);
        var (vm, d, w, trash) = Create(t);
        vm.OpenProjectPath = second;
        vm.OpenVideoPath = video;
        vm.Load();

        // 開いている編集・その録画は消せない。
        vm.SetSelection([vm.Rows[2]]);
        vm.DeleteCommand.Execute(null);
        vm.SetSelection([vm.Rows[0]]);
        vm.DeleteCommand.Execute(null);
        Assert.Empty(trash.Moved);
        Assert.Equal(2, d.Messages.Count(m => m.StartsWith("warn:") && m.Contains("開いている")));
        Assert.Empty(w.DangerMessages);

        // 1 つ目の編集（録画のフォルダーの直下）はプロジェクトファイルだけ、録画の行はフォルダーごと。
        vm.OpenProjectPath = null;
        vm.OpenVideoPath = null;
        vm.SetSelection([vm.Rows[1], vm.Rows[3]]);
        w.DangerResult = false;
        vm.DeleteCommand.Execute(null);
        Assert.Empty(trash.Moved);
        w.DangerResult = true;
        vm.DeleteCommand.Execute(null);
        Assert.Equal([Path.Combine(t.Root, "20260920_0900_c"), first], trash.Moved.Order());
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(second));
        Assert.Equal(["a", "b"], vm.Rows.Select(r => r.Name));
        Assert.Contains("元の動画と、録画のフォルダーにある書き出したファイルは残ります", w.DangerMessages.Last());

        // 録画と、その中の編集を一緒に選んだら録画のフォルダーだけ。
        vm.SetSelection(vm.Rows.ToList());
        vm.DeleteCommand.Execute(null);
        Assert.Equal(a, trash.Moved.Last());
        Assert.Equal(3, trash.Moved.Count);
        Assert.Empty(vm.Rows);
        Assert.Empty(new ProjectIndexStore(t.Root).TryLoad()!.Recordings);
    }

    [Fact]
    public void RenameIsBlockedForTheOpenEdit()
    {
        using var t = new TempRoot();
        var (_, _, _, second) = Sample(t);
        var (vm, d, w, _) = Create(t);
        vm.OpenProjectPath = second;
        vm.Load();
        w.PromptResult = "新しい名前";
        vm.SetSelection([vm.Rows[2]]);
        vm.RenameCommand.Execute(null);
        Assert.True(File.Exists(second));
        Assert.Contains(d.Messages, m => m.Contains("名前は変えられません"));

        vm.OpenProjectPath = null;
        vm.RenameCommand.Execute(null);
        Assert.False(File.Exists(second));
        Assert.Contains(vm.Rows, r => r.Name == "新しい名前");
    }

    [Fact]
    public async Task ExportAndDeleteRemovesOnlyAfterVerification()
    {
        using var t = new TempRoot();
        var (a, _, _, _) = Sample(t);
        var (vm, d, w, trash) = Create(t);
        vm.Load();
        var zip = Path.Combine(t.Base, "out.zip");
        d.SaveFileResult = zip;
        vm.SetSelection([vm.Rows[2]]); // 編集の行を選んでも録画ごと
        await vm.ExportAndDeleteCommand.ExecuteAsync(null);
        Assert.True(File.Exists(zip));
        Assert.Equal([a], trash.Moved);
        Assert.Contains(a, w.DangerMessages.Single());
        Assert.Equal(["c"], vm.Rows.Select(r => r.Name));
    }
}

public class MainViewModelEditsTests
{
    private sealed class Unused : IMediaMetadataService, IFrameExtractionService, IVideoExportService, IGifExportService, ILocalAnalysisService, IShellService, IFFmpegInfoService
    {
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExtractAsync(string f, string v, double t, string o, ImageFormat fmt, int q, int w, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetThumbnailAsync(string f, string v, double t, int w, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetFullFrameAsync(string f, string v, double t, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoExportResult> ExportAsync(VideoExportRequest r, IProgress<VideoExportProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<GifExportResult> ExportAsync(GifExportRequest r, IProgress<VideoExportProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<AnalysisResult> AnalyzeAsync(string f, string v, VideoMetadata m, AnalysisSettings s, IProgress<double>? p, CancellationToken ct) => throw new NotSupportedException();
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) { }
        public void OpenUrl(string url) { }
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DuplicateFromCutsCreatesEditFolderAndOpensIt()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var first = TempRoot.SaveProject(Path.Combine(a, "20260923_1015_a.mvsproj.json"), video, p =>
        {
            p.EditPhase = EditPhase.Subtitle;
            p.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
            p.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "字幕" });
        });
        var settings = new ProjectListViewModelTests.Settings(t.Root);
        var dialogs = new ProjectListViewModelTests.Dialogs();
        var windows = new ProjectListViewModelTests.Windows { PromptResult = "短縮版" };
        var unused = new Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var vm = new MainViewModel(settings, new ProjectService(), unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, windows, unused, ffmpeg, NullLogger<MainViewModel>.Instance);

        await vm.OpenProjectFileAsync(first);
        Assert.Equal("20260923_1015_a.mvsproj.json - Manual Video Studio", vm.WindowTitle);
        await vm.DuplicateEditAsync(first, restartFromCuts: true);

        var path = vm.ProjectPath!;
        Assert.StartsWith(Path.Combine(a, "edits") + @"\", path);
        Assert.EndsWith("_短縮版.mvsproj.json", path);
        Assert.True(vm.IsCutPhase);
        Assert.Single(vm.CutItems);
        Assert.Empty(vm.Project!.SubtitleSegments);
        Assert.False(vm.IsDirty);
        Assert.Equal("a / 短縮版 - Manual Video Studio", vm.WindowTitle);
        Assert.True(vm.NewEditForCurrentCommand.CanExecute(null));
        // 元の編集は変わらない。一覧の管理ファイルにも載る。
        Assert.Single(new ProjectService().Load(first).Project.SubtitleSegments);
        var rec = Assert.Single(new ProjectIndexStore(t.Root).TryLoad()!.Recordings);
        Assert.Equal(["a", "短縮版"], rec.Edits.Select(e => e.Name));
        Assert.Empty(dialogs.Messages);
    }
}
