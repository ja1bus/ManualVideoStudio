using System.Text.Json;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-test-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string CreateVideo(string name = "操作 動画.mp4", int size = 1234)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
        return p;
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var video = CreateVideo();
        var project = TestProjects.Create();
        project.SourceVideo.Path = video;
        project.SourceVideo.FileSize = 1234;
        var ed = new ProjectEditor(project);
        ed.AddCut(10, 20, SegmentAction.Shorten, 1.5);
        ed.AddCapture(7.12);
        ed.SetCaption(1, "Windowsの設定画面を開く");
        ed.AddCandidates([new CutCandidate { Start = 30, End = 40, Reason = "静止" }], "x");
        ed.UpsertAiHistory(new AiHistoryEntry { Purpose = AiPurpose.CaptureCaption, Prompt = "p", Response = "r" });

        var path = ProjectService.NormalizePath(Path.Combine(_dir, "test"));
        Assert.EndsWith(".mvsproj.json", path);
        var svc = new ProjectService();
        svc.Save(project, path);

        var text = File.ReadAllText(path);
        Assert.Contains("Windowsの設定画面を開く", text);       // 日本語をエスケープしない
        Assert.Contains("\"action\": \"shorten\"", text);        // enum は文字列
        Assert.Contains("\"relativePath\": \"操作 動画.mp4\"", text);

        var loaded = svc.Load(path);
        Assert.True(loaded.VideoFound);
        Assert.Null(loaded.Warning);
        var p = loaded.Project;
        Assert.Equal(SegmentAction.Shorten, p.CutSegments[0].Action);
        Assert.Equal(1.5, p.CutSegments[0].KeepSeconds);
        Assert.Equal("Windowsの設定画面を開く", p.CapturePoints[0].Caption);
        Assert.Equal(CaptionSource.Manual, p.CapturePoints[0].CaptionSource);
        Assert.Equal(2, p.NextCaptureId);
        Assert.Single(p.CutCandidates);
        Assert.Single(p.AiHistory);
        Assert.Equal(60, p.VideoMetadata!.Duration);
    }

    [Fact]
    public void VideoResolvedByRelativePathAfterMove()
    {
        var video = CreateVideo(Path.Combine("a", "v.mp4"));
        var project = TestProjects.Create();
        project.SourceVideo.Path = video;
        var path = Path.Combine(_dir, "a", "p.mvsproj.json");
        new ProjectService().Save(project, path);

        // フォルダごと移動
        var moved = Path.Combine(_dir, "b");
        Directory.Move(Path.Combine(_dir, "a"), moved);
        var loaded = new ProjectService().Load(Path.Combine(moved, "p.mvsproj.json"));
        Assert.True(loaded.VideoFound);
        Assert.Equal(Path.Combine(moved, "v.mp4"), loaded.Project.SourceVideo.Path);
    }

    [Fact]
    public void MissingVideoIsReportedAndCanBeRelinked()
    {
        var video = CreateVideo("v.mp4", 100);
        var project = TestProjects.Create();
        project.SourceVideo.Path = video;
        project.SourceVideo.FileSize = 100;
        var path = Path.Combine(_dir, "p.mvsproj.json");
        new ProjectService().Save(project, path);
        File.Delete(video);

        var loaded = new ProjectService().Load(path);
        Assert.False(loaded.VideoFound);

        var other = CreateVideo("other.mp4", 200);
        var warning = ProjectService.Relink(loaded.Project, other);
        Assert.NotNull(warning);
        Assert.Equal(other, loaded.Project.SourceVideo.Path);
        Assert.Equal(200, loaded.Project.SourceVideo.FileSize);
    }

    [Fact]
    public void NewerSchemaIsRejected()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        File.WriteAllText(path, "{\"schemaVersion\": 99}");
        Assert.Throws<InvalidOperationException>(() => new ProjectService().Load(path));
    }

    [Fact]
    public void BrokenProjectFileGivesFriendlyError()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        File.WriteAllText(path, "{ not json");
        var ex = Assert.Throws<InvalidOperationException>(() => new ProjectService().Load(path));
        Assert.Contains("読み込めませんでした", ex.Message);
    }

    [Fact]
    public void NextCaptureIdIsRepairedOnLoad()
    {
        var path = Path.Combine(_dir, "p.mvsproj.json");
        File.WriteAllText(path, """{"schemaVersion":1,"capturePoints":[{"id":7,"sequence":1,"timestamp":1,"selectedFrameTime":1}],"nextCaptureId":1}""");
        Assert.Equal(8, new ProjectService().Load(path).Project.NextCaptureId);
    }

    [Fact]
    public void UiModeIsSimpleOnlyOnFirstRun()
    {
        var path = Path.Combine(_dir, "settings-mode.json");
        // 初めての起動（設定ファイルが無い）は簡易モード。
        var s = new SettingsService(path);
        Assert.Equal(UiModes.Simple, s.Load().UiMode);
        Assert.True(s.Current.IsSimpleMode);
        s.Current.UiMode = UiModes.Full;
        s.Save();
        Assert.Contains("\"uiMode\": \"full\"", File.ReadAllText(path));
        Assert.Equal(UiModes.Full, new SettingsService(path).Load().UiMode);

        s.Current.UiMode = UiModes.Simple;
        s.Save();
        Assert.Equal(UiModes.Simple, new SettingsService(path).Load().UiMode);

        // v1.10 以前の設定（uiMode が無い）・不明な値・読めない設定は、機能を急に隠さないよう全機能モード。
        File.WriteAllText(path, """{"schemaVersion":1,"language":"日本語"}""");
        Assert.Equal(UiModes.Full, new SettingsService(path).Load().UiMode);
        File.WriteAllText(path, """{"schemaVersion":1,"uiMode":"expert"}""");
        Assert.Equal(UiModes.Full, new SettingsService(path).Load().UiMode);
        File.WriteAllText(path, "garbage");
        Assert.Equal(UiModes.Full, new SettingsService(path).Load().UiMode);
    }

    [Fact]
    public void SettingsRoundTripAndBrokenFile()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = new SettingsService(path);
        s.Load();
        s.Current.FfmpegPath = @"C:\ff\ffmpeg.exe";
        s.Current.Analysis = AnalysisSettings.FromPreset(AnalysisPreset.Aggressive);
        s.Current.DefaultExport.ImageFormat = ImageFormat.Jpeg;
        s.Current.FrameCandidates.StepSeconds = 0.1;
        s.Save();

        var s2 = new SettingsService(path);
        var loaded = s2.Load();
        Assert.Equal(@"C:\ff\ffmpeg.exe", loaded.FfmpegPath);
        Assert.Equal(AnalysisPreset.Aggressive, loaded.Analysis.Preset);
        Assert.True(loaded.Analysis.IncludeSilenceOnly);
        Assert.Equal(ImageFormat.Jpeg, loaded.DefaultExport.ImageFormat);
        Assert.Equal(0.1, loaded.FrameCandidates.StepSeconds);

        File.WriteAllText(path, "garbage");
        Assert.Equal("", new SettingsService(path).Load().FfmpegPath);
    }

    [Fact]
    public void FrameCandidateTimes()
    {
        var f = new FrameCandidateSettings { StepSeconds = 0.2, CountEachSide = 1 };
        Assert.Equal([12.2, 12.4, 12.6], f.CandidateTimes(12.4, 60));
        Assert.Equal([0, 0.2], f.CandidateTimes(0, 60));
        Assert.Equal([59.8, 60], f.CandidateTimes(60, 60));
    }

    [Fact]
    public void ImageExportPlanAndMetadata()
    {
        var outDir = Path.Combine(_dir, "images");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "001_Windowsの設定画面を開く.png"), "x");
        File.WriteAllText(Path.Combine(outDir, "003_古いキャプション.png"), "x");
        File.WriteAllText(Path.Combine(outDir, "memo.txt"), "x");

        var points = new List<CapturePoint>
        {
            new() { Id = 1, Sequence = 1, Timestamp = 7.12, SelectedFrameTime = 7.32, Caption = "Windowsの設定画面を開く", CaptionSource = CaptionSource.Ai },
            new() { Id = 4, Sequence = 2, Timestamp = 12.4, SelectedFrameTime = 12.4, Caption = "ネットワーク: Wi-Fi?" },
        };
        var plan = ImageExportService.Plan(points, outDir, ImageFormat.Png);
        Assert.Equal(["001_Windowsの設定画面を開く.png", "002_ネットワーク： Wi-Fi？.png"], plan.Items.Select(i => i.FileName));
        Assert.Equal(1, plan.ExistingCount);
        Assert.Equal(["003_古いキャプション.png"], plan.StaleFiles);

        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 0, End = 5 }]);
        var json = ImageExportService.BuildMetadataJson(@"C:\v\manual.mp4", plan, keep);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("manual.mp4", doc.RootElement.GetProperty("source").GetString());
        var first = doc.RootElement.GetProperty("captures")[0];
        Assert.Equal(1, first.GetProperty("sequence").GetInt32());
        Assert.Equal(7.12, first.GetProperty("timestamp").GetDouble());
        Assert.Equal(7.32, first.GetProperty("selectedFrameTime").GetDouble());
        Assert.Equal(2.32, first.GetProperty("editedVideoTime").GetDouble(), 3);
        Assert.Equal("001_Windowsの設定画面を開く.png", first.GetProperty("file").GetString());
        Assert.Equal("ai", first.GetProperty("captionSource").GetString());
    }

    [Fact]
    public void DefaultOutputPaths()
    {
        var s = new ExportSettings();
        Assert.Equal(@"C:\v\manual_edited.mp4", VideoExportService.DefaultOutputPath(@"C:\v\manual.mp4", s));
        Assert.Equal(@"C:\v\images", ImageExportService.DefaultDirectory(@"C:\v\manual.mp4", s));
        s.VideoOutputDirectory = @"D:\out";
        s.VideoOutputFileName = "x.mp4";
        Assert.Equal(@"D:\out\x.mp4", VideoExportService.DefaultOutputPath(@"C:\v\manual.mp4", s));
    }
}
