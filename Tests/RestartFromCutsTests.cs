using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>
/// ［現在の編集を残して、カットからやり直す］（v1.12）。最新状態の保存 → 別の編集の作成 → 開く、の流れと、失敗時に元の編集へ留まること。
/// </summary>
public class RestartFromCutsTests
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

    private sealed class NullClipboard : IClipboardService
    {
        public bool SetText(string text) => true;
        public bool TryGetText(out string text) { text = ""; return false; }
    }

    /// <summary>edits\ への保存だけ失敗させられる ProjectService。</summary>
    private sealed class FlakyProjects : IProjectService
    {
        private readonly ProjectService _real = new();
        public bool FailEditsSave { get; set; }
        public int Saves { get; private set; }
        public void Save(ProjectDocument p, string path)
        {
            Saves++;
            if (FailEditsSave && path.Contains(@"\edits\", StringComparison.OrdinalIgnoreCase)) throw new IOException("disk full (test)");
            _real.Save(p, path);
        }
        public ProjectLoadResult Load(string path) => _real.Load(path);
    }

    private sealed class Rig : IDisposable
    {
        public required TempRoot Temp;
        public required MainViewModel Vm;
        public required ProjectListViewModelTests.Dialogs Dialogs;
        public required ProjectListViewModelTests.Windows Windows;
        public required FlakyProjects Projects;
        public required string Folder;
        public required string First;
        public void Dispose() => Temp.Dispose();
    }

    private static async Task<Rig> OpenAsync(bool saved = true, string uiMode = UiModes.Full)
    {
        var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var first = TempRoot.SaveProject(Path.Combine(a, "20260923_1015_a.mvsproj.json"), video, p =>
        {
            p.EditPhase = EditPhase.Subtitle;
            p.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
            p.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "字幕" });
            p.ShapeGroups.Add(new ShapeGroup { Start = 1, End = 2, ImageWidth = 1920, ImageHeight = 1080, Shapes = [new RectangleAnnotation { X = 10, Y = 10, Width = 100, Height = 50 }] });
            p.CapturePoints.Add(new CapturePoint { Id = 1, Sequence = 1, Timestamp = 3, SelectedFrameTime = 3, Caption = "手順書の画像", OutputFilename = "01_手順書の画像.png" });
            p.NextCaptureId = 2;
            p.ExportSettings.VideoOutputFileName = "完成.mp4";
        });
        var settings = new ProjectListViewModelTests.Settings(t.Root);
        settings.Current.UiMode = uiMode;
        var dialogs = new ProjectListViewModelTests.Dialogs { SaveFileResult = Path.Combine(a, "saved-as.mvsproj.json") };
        var windows = new ProjectListViewModelTests.Windows { PromptResult = "新しいカット" };
        var projects = new FlakyProjects();
        var unused = new Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var store = new CaptionRequestStore(Path.Combine(t.Base, "caption-requests"));
        var vm = new MainViewModel(settings, projects, unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, windows, unused, ffmpeg, NullLogger<MainViewModel>.Instance,
            captionRequests: new CaptionRequestService(unused, store), captionStore: store,
            promptTemplates: new Core.Ai.PromptTemplateService(Path.Combine(t.Base, "prompt-templates.json")), clipboard: new NullClipboard());
        await vm.OpenProjectFileAsync(first);
        Assert.True(vm.IsSubtitlePhase);
        if (!saved) vm.ProjectPath = null;
        return new Rig { Temp = t, Vm = vm, Dialogs = dialogs, Windows = windows, Projects = projects, Folder = a, First = first };
    }

    [Fact]
    public async Task SavesLatestEditsThenOpensNewEditFromCuts()
    {
        using var r = await OpenAsync();
        // 未保存の字幕修正（最新状態）を作る。
        var seg = r.Vm.Project!.SubtitleSegments[0];
        Assert.True(r.Vm.Editor!.UpdateSubtitle(seg.Id, 1, 2.5, "最新の字幕", SubtitlePosition.Bottom, 48).Success);
        Assert.True(r.Vm.IsDirty);

        Assert.True(r.Vm.RestartFromCutsKeepingCurrentCommand.CanExecute(null));
        await r.Vm.RestartFromCutsKeepingCurrentCommand.ExecuteAsync(null);

        // 元の編集には最新の字幕・図形・カット・手順書画像が保存されている。
        var original = new ProjectService().Load(r.First).Project;
        Assert.Equal(EditPhase.Subtitle, original.EditPhase);
        Assert.Equal(["最新の字幕"], original.SubtitleSegments.Select(s => s.Text));
        Assert.Single(original.ShapeGroups);
        Assert.Single(original.CutSegments);
        Assert.Equal("完成.mp4", original.ExportSettings.VideoOutputFileName);

        // 新しい編集: edits\ の別ファイル、カット編集の段階、カットと手順書画像は引き継ぎ、字幕・図形は無い。
        var path = r.Vm.ProjectPath!;
        Assert.NotEqual(r.First, path);
        Assert.StartsWith(Path.Combine(r.Folder, "edits") + @"\", path);
        Assert.True(r.Vm.IsCutPhase);
        Assert.False(r.Vm.IsDirty);
        Assert.Single(r.Vm.Project!.CutSegments);
        Assert.Empty(r.Vm.Project.SubtitleSegments);
        Assert.Empty(r.Vm.Project.ShapeGroups);
        var cap = Assert.Single(r.Vm.Project.CapturePoints);
        Assert.Equal("手順書の画像", cap.Caption);
        Assert.Equal("", cap.OutputFilename); // 画像はまだ新しい編集のフォルダーに書き出していない
        Assert.Equal("", r.Vm.Project.ExportSettings.VideoOutputFileName); // 以前の完成物の名前を引き継がない（衝突しない）
        // 同じ元動画を参照する（コピー・移動しない）。
        Assert.Equal(original.SourceVideo.Path, r.Vm.Project.SourceVideo.Path);
        Assert.True(File.Exists(original.SourceVideo.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.mp4"));

        // その後の保存は新しい編集だけを更新し、元の編集は上書きしない。
        r.Vm.Editor!.AddCut(20, 25, SegmentAction.Remove, 0);
        r.Vm.SaveProjectCommand.Execute(null);
        Assert.Equal(2, new ProjectService().Load(path).Project.CutSegments.Count);
        Assert.Single(new ProjectService().Load(r.First).Project.CutSegments);

        // 元の編集を開けば、以前のカット・字幕・図形が戻る。一覧にも両方載る。
        await r.Vm.OpenProjectFileAsync(r.First);
        Assert.True(r.Vm.IsSubtitlePhase);
        Assert.Equal(["最新の字幕"], r.Vm.Project!.SubtitleSegments.Select(s => s.Text));
        Assert.Single(r.Vm.Project.ShapeGroups);
        var rec = Assert.Single(new ProjectIndexStore(r.Temp.Root).TryLoad()!.Recordings);
        Assert.Equal(2, rec.Edits.Count);
        Assert.Empty(r.Dialogs.Messages);
    }

    [Fact]
    public async Task CancelingNameKeepsCurrentEditAndSavesNothing()
    {
        using var r = await OpenAsync();
        r.Windows.PromptResult = null;
        await r.Vm.RestartFromCutsKeepingCurrentCommand.ExecuteAsync(null);
        Assert.Equal(r.First, r.Vm.ProjectPath);
        Assert.True(r.Vm.IsSubtitlePhase);
        Assert.Single(r.Vm.Project!.SubtitleSegments);
        Assert.Single(r.Vm.Project.ShapeGroups);
        Assert.Equal(0, r.Projects.Saves); // 取りやめでは何も保存しない
        Assert.False(Directory.Exists(Path.Combine(r.Folder, "edits")));
    }

    [Fact]
    public async Task SaveFailureOrCancelStaysWithSubtitlesAndShapes()
    {
        // 未保存（保存先を尋ねる）で、保存をキャンセルした場合。「保存しないで続行」は無い。
        using var r = await OpenAsync(saved: false);
        r.Dialogs.SaveFileResult = null;
        await r.Vm.RestartFromCutsKeepingCurrentCommand.ExecuteAsync(null);
        Assert.Null(r.Vm.ProjectPath);
        Assert.True(r.Vm.IsSubtitlePhase);
        Assert.Single(r.Vm.Project!.SubtitleSegments);
        Assert.Single(r.Vm.Project.ShapeGroups);
        Assert.False(Directory.Exists(Path.Combine(r.Folder, "edits")));
    }

    [Fact]
    public async Task NewEditSaveFailureKeepsOriginalOpenWithoutLeavingEmptyFolder()
    {
        using var r = await OpenAsync();
        var seg = r.Vm.Project!.SubtitleSegments[0];
        r.Vm.Editor!.UpdateSubtitle(seg.Id, 1, 2.5, "最新の字幕", SubtitlePosition.Bottom, 48);
        r.Projects.FailEditsSave = true;
        await r.Vm.RestartFromCutsKeepingCurrentCommand.ExecuteAsync(null);

        Assert.Equal(r.First, r.Vm.ProjectPath);
        Assert.True(r.Vm.IsSubtitlePhase);
        Assert.Equal(["最新の字幕"], r.Vm.Project!.SubtitleSegments.Select(s => s.Text));
        Assert.Single(r.Vm.Project.ShapeGroups);
        // 元の編集には最新の状態が保存済み。途中の空フォルダーは残さない。
        Assert.Equal(["最新の字幕"], new ProjectService().Load(r.First).Project.SubtitleSegments.Select(s => s.Text));
        var edits = Path.Combine(r.Folder, "edits");
        Assert.True(!Directory.Exists(edits) || Directory.GetDirectories(edits).Length == 0);
        Assert.Contains(r.Dialogs.Messages, m => m.StartsWith("error:") && m.Contains("そのまま"));
    }

    [Fact]
    public async Task OutsideProjectRootCannotCreateAnotherEditAndStays()
    {
        using var r = await OpenAsync();
        using var outside = new TempRoot();
        var other = outside.Recording("20260101_0000_x");
        var path = TempRoot.SaveProject(Path.Combine(other, "x.mvsproj.json"), Path.Combine(other, "rec_20260923_101500.mp4"), p =>
        {
            p.EditPhase = EditPhase.Subtitle;
            p.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "外" });
        });
        await r.Vm.OpenProjectFileAsync(path);
        await r.Vm.RestartFromCutsKeepingCurrentCommand.ExecuteAsync(null);
        Assert.Equal(path, r.Vm.ProjectPath);
        Assert.Single(r.Vm.Project!.SubtitleSegments);
        Assert.Contains(r.Dialogs.Messages, m => m.StartsWith("warn:"));
    }

    [Theory]
    [InlineData(UiModes.Simple)]
    [InlineData(UiModes.Full)]
    public async Task GeminiEntryAndRestartAreAvailableInBothModes(string mode)
    {
        using var r = await OpenAsync(uiMode: mode);
        Assert.Equal(mode == UiModes.Simple, r.Vm.IsSimpleMode);
        Assert.True(r.Vm.OpenGeminiCaptionCommand.CanExecute(null));
        Assert.True(r.Vm.RestartFromCutsKeepingCurrentCommand.CanExecute(null));
        r.Vm.OpenGeminiCaptionCommand.Execute(null);
        Assert.Equal(1, r.Windows.GeminiOpened);
        // カット編集では［Gemini で字幕を作る］は使えない（カット後の動画が確定していないため）。
        r.Vm.Editor!.DiscardSubtitlesAndReturnToCutEditing();
        Assert.False(r.Vm.OpenGeminiCaptionCommand.CanExecute(null));
    }

    [Fact]
    public async Task CommandIsOnlyAvailableInSubtitlePhase()
    {
        using var r = await OpenAsync();
        Assert.True(r.Vm.RestartFromCutsKeepingCurrentCommand.CanExecute(null));
        r.Vm.Editor!.DiscardSubtitlesAndReturnToCutEditing();
        Assert.False(r.Vm.RestartFromCutsKeepingCurrentCommand.CanExecute(null));
    }

    [Fact]
    public async Task CutChangeAfterReturnInvalidatesPreparedRequestVideo()
    {
        using var r = await OpenAsync();
        var video = Path.Combine(r.Temp.Base, "req_gemini.mp4");
        File.WriteAllBytes(video, new byte[10]);
        r.Vm.CaptionRequest.Video = new CaptionRequestVideo(video, "fp", 10, DateTimeOffset.Now);
        r.Vm.Editor!.DiscardSubtitlesAndReturnToCutEditing(); // カット編集へ戻る → 以前の依頼用動画・取り込み待ちは無効
        Assert.Null(r.Vm.CaptionRequest.Video);
        Assert.Single(r.Vm.CaptionRequest.Orphans);
        Assert.True(File.Exists(video)); // 確認なしには消さない
    }
}
