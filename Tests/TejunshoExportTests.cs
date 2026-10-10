using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>手順書用の書き出し（v1.8）: 画像メタデータ JSON の形式 2・字幕の候補・meta.json・出力先・線幅の既定。</summary>
public sealed class TejunshoExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-tejunsho-" + Guid.NewGuid().ToString("N"));

    public TejunshoExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static List<CapturePoint> Points() =>
    [
        new() { Id = 1, Sequence = 1, Timestamp = 7.12, SelectedFrameTime = 7.32, Caption = "設定画面を開く", CaptionSource = CaptionSource.Manual },
        new() { Id = 5, Sequence = 2, Timestamp = 3, SelectedFrameTime = 3, Caption = "カットされる位置" },
    ];

    private static IReadOnlyList<TimeRange> Keep() => EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 0, End = 5 }]);

    private sealed class FakeFrames : IFrameExtractionService
    {
        public Task ExtractAsync(string ffmpegPath, string videoPath, double time, string outputPath, ImageFormat format, int jpegQuality, int maxWidth, CancellationToken ct)
        {
            File.WriteAllText(outputPath, "img");
            return Task.CompletedTask;
        }
        public Task<string> GetThumbnailAsync(string ffmpegPath, string videoPath, double time, int width, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetFullFrameAsync(string ffmpegPath, string videoPath, double time, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void MetadataFormat2WritesAllNewKeysForNormalExport()
    {
        var plan = ImageExportService.Plan(Points(), Path.Combine(_dir, "images"), ImageFormat.Png);
        var json = ImageExportService.BuildMetadataJson(@"C:\v\manual.mp4", plan, Keep());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("formatVersion").GetInt32());
        Assert.StartsWith("ManualVideoStudio ", root.GetProperty("generator").GetString());
        Assert.Equal("images", root.GetProperty("layout").GetString());
        Assert.Equal("", root.GetProperty("project").GetString());
        Assert.Equal("manual.mp4", root.GetProperty("source").GetString());   // 形式 1 のキーはそのまま
        var c = root.GetProperty("captures")[0];
        Assert.Equal("001_設定画面を開く.png", c.GetProperty("file").GetString());
        Assert.Equal("001_設定画面を開く.png", c.GetProperty("path").GetString());   // 通常の書き出しは file と同じ
        Assert.Equal("設定画面を開く", c.GetProperty("caption").GetString());
        // 段階 1 では空でも省略せずに書く。
        Assert.Equal(JsonValueKind.Null, c.GetProperty("stepText").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("note").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("chapter").ValueKind);
        Assert.False(c.GetProperty("chapterStart").GetBoolean());
        Assert.Equal(0, c.GetProperty("subtitles").GetArrayLength());
    }

    [Fact]
    public void TejunshoLayoutUsesRelativeImagePathAndSubtitles()
    {
        var images = TejunshoExport.ImagesDirectory(_dir);
        var plan = ImageExportService.Plan(Points(), images, ImageFormat.Png);
        List<SubtitleSegment> subs =
        [
            new() { Start = 2.0, End = 3.0, Text = "設定画面を\r\n開きます" },
            new() { Start = 1.0, End = 2.5, Text = "先に始まる字幕" },
            new() { Start = 2.32, End = 4, Text = "   " },          // 空白だけは入れない
            new() { Start = 3.0, End = 5, Text = "後の字幕" },
        ];
        var json = ImageExportService.BuildMetadataJson(@"C:\v\manual.mp4", plan, Keep(),
            new ImageMetadataOptions(ImageMetadataLayout.Tejunsho, "ルーター設定", _dir, subs));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tejunsho", doc.RootElement.GetProperty("layout").GetString());
        Assert.Equal("ルーター設定", doc.RootElement.GetProperty("project").GetString());
        var first = doc.RootElement.GetProperty("captures")[0];
        Assert.Equal("images/001_設定画面を開く.png", first.GetProperty("path").GetString());
        Assert.Equal(2.32, first.GetProperty("editedVideoTime").GetDouble(), 3);
        Assert.Equal(["先に始まる字幕", "設定画面を\n開きます"], first.GetProperty("subtitles").EnumerateArray().Select(e => e.GetString()));
        // カットされる位置の画像には字幕を入れない。
        var cut = doc.RootElement.GetProperty("captures")[1];
        Assert.Equal(JsonValueKind.Null, cut.GetProperty("editedVideoTime").ValueKind);
        Assert.Equal(0, cut.GetProperty("subtitles").GetArrayLength());
    }

    [Fact]
    public void SubtitlesAtUsesHalfOpenInterval()
    {
        List<SubtitleSegment> subs = [new() { Start = 1, End = 2, Text = "a" }, new() { Start = 2, End = 3, Text = "b" }];
        Assert.Equal(["a"], ImageExportService.SubtitlesAt(1, subs));
        Assert.Equal(["b"], ImageExportService.SubtitlesAt(2, subs));     // 終了の時刻は含めない
        Assert.Empty(ImageExportService.SubtitlesAt(3, subs));
        Assert.Empty(ImageExportService.SubtitlesAt(0.999, subs));
        Assert.Empty(ImageExportService.SubtitlesAt(null, subs));
    }

    [Fact]
    public async Task ExportWritesMetadataToTejunshoFolderAndImagesBelow()
    {
        var plan = ImageExportService.Plan(Points(), TejunshoExport.ImagesDirectory(_dir), ImageFormat.Png);
        var result = await new ImageExportService(new FakeFrames()).ExportAsync("ffmpeg.exe", "v.mp4", plan, true, 2, true, Keep(), null, default,
            new ImageMetadataOptions(ImageMetadataLayout.Tejunsho, "p", _dir));
        Assert.Equal(Path.Combine(_dir, "capture-metadata.json"), result.MetadataPath);
        Assert.True(File.Exists(Path.Combine(_dir, "images", "001_設定画面を開く.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "images", "capture-metadata.json")));
    }

    [Fact]
    public void DefaultDirectoryAndProjectName()
    {
        var s = new ExportSettings();
        Assert.Equal(@"C:\v\tejunsho", TejunshoExport.DefaultDirectory(@"C:\v\manual.mp4", s));
        // 2 つ目以降の編集は編集のフォルダー。
        var rec = @"C:\root\20260926_1200_ルーター設定";
        var edit = Path.Combine(rec, "edits", "20260926_1300_短縮版", "20260926_1300_短縮版.mvsproj.json");
        Assert.Equal(Path.Combine(rec, "edits", "20260926_1300_短縮版", "tejunsho"), TejunshoExport.DefaultDirectory(Path.Combine(rec, "a.mp4"), s, edit));
        s.TejunshoOutputDirectory = @"D:\out";
        Assert.Equal(@"D:\out", TejunshoExport.DefaultDirectory(@"C:\v\manual.mp4", s));

        Assert.Equal("ルーター設定", TejunshoExport.ProjectName(Path.Combine(rec, "a.mp4"), Path.Combine(rec, "x.mvsproj.json")));
        Assert.Equal("短縮版", TejunshoExport.ProjectName(Path.Combine(rec, "a.mp4"), edit));
        Assert.Equal("manual", TejunshoExport.ProjectName(@"C:\v\manual.mp4", null));   // 録画のフォルダーの外（旧形式）
    }

    [Fact]
    public void MetaJsonDraftAndNoOverwrite()
    {
        var json = TejunshoExport.BuildMetaJson("ルーター設定", new DateTime(2026, 9, 6));
        using (var doc = JsonDocument.Parse(json))
        {
            var r = doc.RootElement;
            Assert.Equal(["title", "subtitle", "version", "date", "org", "revisions"], r.EnumerateObject().Select(p => p.Name));
            Assert.Equal("ルーター設定", r.GetProperty("title").GetString());
            Assert.Equal("第1.0版", r.GetProperty("version").GetString());
            Assert.Equal("2026年9月6日", r.GetProperty("date").GetString());
            Assert.Equal(["1.0", "2026/09/06", "", "初版作成"], r.GetProperty("revisions")[0].EnumerateArray().Select(e => e.GetString()));
        }

        var dir = Path.Combine(_dir, "t");
        Assert.True(TejunshoExport.WriteMetaIfMissing(dir, "A", DateTime.Now));
        var path = Path.Combine(dir, "meta.json");
        File.WriteAllText(path, "{\"title\":\"直した題名\"}");
        Assert.False(TejunshoExport.WriteMetaIfMissing(dir, "B", DateTime.Now));
        Assert.Equal("{\"title\":\"直した題名\"}", File.ReadAllText(path));
        Assert.DoesNotContain((byte)0xEF, File.ReadAllBytes(path).Take(1));   // BOM なし（最初に書いたときも同じ関数）
    }

    [Fact]
    public void RequestTextContainsFullPathAndSkillName()
        => Assert.Equal(@"C:\p\tejunsho の ManualVideoStudio の出力（プロジェクト「ルーター設定」）から tejunsho-gui-docx で手順書を作って",
            TejunshoExport.RequestText(@"C:\p\tejunsho", "ルーター設定"));

    [Fact]
    public void CopyResetsAndRelocateMovesTejunshoDirectory()
    {
        var p = TestProjects.Create();
        p.ExportSettings.TejunshoOutputDirectory = @"C:\old\tejunsho";
        Assert.Equal("", ProjectCatalog.PrepareCopy(p, false, new ExportSettings()).ExportSettings.TejunshoOutputDirectory);

        var oldFolder = Path.Combine(_dir, "old");
        var newFolder = Path.Combine(_dir, "new");
        Directory.CreateDirectory(newFolder);
        p.ExportSettings.TejunshoOutputDirectory = Path.Combine(oldFolder, "tejunsho");
        var file = Path.Combine(newFolder, "x.mvsproj.json");
        new ProjectService().Save(p, file);
        Assert.True(ProjectCatalog.Relocate(file, oldFolder, newFolder));
        var loaded = new ProjectService().Load(file).Project;
        Assert.Equal(Path.Combine(newFolder, "tejunsho"), loaded.ExportSettings.TejunshoOutputDirectory);
    }

    [Fact]
    public void ProjectWithoutTejunshoKeyLoadsAsEmpty()
    {
        var file = Path.Combine(_dir, "old.mvsproj.json");
        new ProjectService().Save(TestProjects.Create(), file);
        var text = File.ReadAllText(file).Replace("\"tejunshoOutputDirectory\": \"\",", "").Replace(",\n    \"tejunshoOutputDirectory\": \"\"", "")
            .Replace(",\r\n    \"tejunshoOutputDirectory\": \"\"", "");
        Assert.DoesNotContain("tejunshoOutputDirectory", text);
        File.WriteAllText(file, text);
        var loaded = new ProjectService().Load(file).Project;
        Assert.Equal(ProjectDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("", loaded.ExportSettings.TejunshoOutputDirectory);
    }

    private sealed class Dialogs : IDialogService
    {
        public void ShowInfo(string message, string title = "情報") { }
        public void ShowWarning(string message, string title = "注意") { }
        public void ShowError(string message, string? detail = null, string title = "エラー") { }
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "") => null;
    }

    private static BitmapSource Frame()
    {
        var b = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 180 * 4], 320 * 4);
        b.Freeze();
        return b;
    }

    [Fact]
    public Task ImageModeDefaultsToThickStrokeAndVideoModeToMedium() => StaThread.RunAsync(() =>
    {
        var image = new ImageEditorViewModel(new ImageEditorRequest(new CapturePoint { Id = 1, Sequence = 1 }, Frame(), null, ImageFormat.Png, 2,
            (_, _, _) => EditResult.Ok()), new Dialogs(), null!, NullLogger.Instance);
        Assert.Equal(StrokeWidthLevel.Thick, image.StrokeLevel);
        Assert.Equal(AnnotationStyle.ThickStroke, image.StrokeWidthValue);

        var video = new ImageEditorViewModel(new VideoShapeEditRequest("t", Frame(), [], 320, 180, 1, 4, 30, "背景", (_, _, _, _, _) => EditResult.Ok()),
            new Dialogs(), null!, NullLogger.Instance);
        Assert.Equal(StrokeWidthLevel.Medium, video.StrokeLevel);
    });
}
