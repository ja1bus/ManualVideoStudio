using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>Gemini の回答（SRT）の解析・検証（v1.12）。</summary>
public class CaptionSrtParserTests
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:04,500\n［System］→［Settings］を開きます。\n\n2\n00:00:05,000 --> 00:00:08,000\nTime zoneを「Asia/Tokyo」に変更します。\n2 行目の説明です。\n";

    [Fact]
    public void ParsesWholeAnswerAndIgnoresSurroundingText()
    {
        var answer = "動画の解析結果です。\n\n【推奨字幕設定】\n推奨FontSize：18\n推奨MarginV：30\n\n| 開始 | 終了 |\n| --- | --- |\n| 0:01 | 0:04 |\n\n```srt\n" + Srt + "```\n以上です。";
        var r = CaptionSrtParser.Parse(answer, 60);
        Assert.True(r.IsValid);
        Assert.True(r.ExtractedFromCodeBlock);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal(1.0, r.Entries[0].Start);
        Assert.Equal(4.5, r.Entries[0].End);
        // 複数行字幕は改行（LF）つきで取り込む。
        Assert.Equal("Time zoneを「Asia/Tokyo」に変更します。\n2 行目の説明です。", r.Entries[1].Text);
    }

    [Fact]
    public void ParsesPlainSrtAndCrLf()
    {
        var r = CaptionSrtParser.Parse(Srt.Replace("\n", "\r\n"), 60);
        Assert.True(r.IsValid);
        Assert.False(r.ExtractedFromCodeBlock);
        Assert.Equal(2, r.Entries.Count);
    }

    [Fact]
    public void RecommendedStyleIsNotReflectedInSegments()
    {
        var r = CaptionSrtParser.Parse("推奨FontSize：99\n推奨MarginV：200\n```srt\n" + Srt + "```", 60);
        var defaults = new SubtitleStyle { FontSize = 56, Position = SubtitlePosition.Top };
        var segs = r.ToSegments(defaults);
        // 位置・サイズはアプリの既定（設定）。回答の推奨値は使わない。
        Assert.All(segs, s => { Assert.Equal(56, s.FontSize); Assert.Equal(SubtitlePosition.Top, s.Position); Assert.Equal(0, s.VerticalOffset); });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyIsError(string input)
    {
        var r = CaptionSrtParser.Parse(input, 60);
        Assert.False(r.IsValid);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void StructureErrorsShowLine()
    {
        var r = CaptionSrtParser.Parse("1\n00:00:01.000 --> 00:00:03.000\nテキスト\n", 60);
        Assert.False(r.IsValid);
        var e = Assert.Single(r.Errors);
        Assert.Equal(1, e.LineIndex);
        Assert.Contains("2 行目", e.Display);
        Assert.Contains("カンマ", e.Message);
    }

    [Fact]
    public void MissingBlankLineBetweenBlocksIsDetected()
        => Assert.False(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:03,000\nA\n2\n00:00:04,000 --> 00:00:06,000\nB\n", 60).IsValid);

    [Fact]
    public void NumberMustBeSequential()
        => Assert.False(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:03,000\nA\n\n3\n00:00:04,000 --> 00:00:06,000\nB\n", 60).IsValid);

    [Fact]
    public void FullWidthDigitsDoNotCrash()
        => Assert.False(CaptionSrtParser.Parse("１\n００:００:０１,０００ --> ００:００:０３,０００\nA\n", 60).IsValid);

    [Fact]
    public void OverlapIsReportedNotTrimmed()
    {
        var r = CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:05,000\nA\n\n2\n00:00:04,000 --> 00:00:07,000\nB\n", 60);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("重なって") && e.BlockNumber == 2);
        // 時刻は黙って直さない。
        Assert.Equal(4.0, r.Entries[1].Start);
    }

    [Fact]
    public void TouchingSubtitlesAreAllowed()
        => Assert.True(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:04,000\nA\n\n2\n00:00:04,000 --> 00:00:07,000\nB\n", 60).IsValid);

    [Fact]
    public void BeyondOutputDurationIsError()
    {
        var r = CaptionSrtParser.Parse("1\n00:00:58,000 --> 00:01:05,000\nA\n", 60);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Message.Contains("動画の長さ"));
    }

    [Fact]
    public void TooShortIsError()
        => Assert.False(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:01,100\nA\n", 60).IsValid);

    [Fact]
    public void TooManyLinesAndTooLongAreErrors()
    {
        Assert.False(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:05,000\nA\nB\nC\nD\n", 60).IsValid);
        Assert.False(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:05,000\n" + new string('あ', 201) + "\n", 60).IsValid);
        Assert.True(CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:05,000\nA\nB\nC\n", 60).IsValid);
    }

    [Fact]
    public void ReportsAllValidationErrors()
    {
        var r = CaptionSrtParser.Parse("1\n00:00:01,000 --> 00:00:01,050\nA\n\n2\n00:00:59,000 --> 00:01:30,000\nB\n", 60);
        Assert.Equal(2, r.Errors.Select(e => e.BlockNumber).Distinct().Count());
    }

    [Fact]
    public void MultipleSrtCodeBlocksAreAmbiguous()
        => Assert.False(CaptionSrtParser.Parse("```srt\n" + Srt + "```\n```srt\n" + Srt + "```", 60).IsValid);
}

/// <summary>取り込み（ProjectEditor.ImportSubtitles）。</summary>
public class ImportSubtitlesEditorTests
{
    private static ProjectEditor Editor(bool subtitlePhase = true)
    {
        var e = new ProjectEditor(TestProjects.Create(60));
        if (subtitlePhase) Assert.True(e.ConfirmCuts().Success);
        return e;
    }

    private static List<(double, double, string)> Entries(params (double, double, string)[] e) => e.ToList();

    [Fact]
    public void ImportsAsOneUndoStep()
    {
        var e = Editor();
        e.SubtitleDefaults = new SubtitleStyle { FontSize = 64, Position = SubtitlePosition.Middle };
        var r = e.ImportSubtitles(Entries((1, 3, "A"), (4, 6, "B\nC")));
        Assert.True(r.Success);
        Assert.Equal(2, e.Project.SubtitleSegments.Count);
        Assert.All(e.Project.SubtitleSegments, s => { Assert.Equal(64, s.FontSize); Assert.Equal(SubtitlePosition.Middle, s.Position); });
        e.Undo();
        Assert.Empty(e.Project.SubtitleSegments);
        e.Redo();
        Assert.Equal(2, e.Project.SubtitleSegments.Count);
    }

    [Fact]
    public void ReimportReplacesAndUndoRestoresPrevious()
    {
        var e = Editor();
        e.AddSubtitle(1, 3, "手動");
        e.AddSubtitle(10, 12, "手動2");
        var r = e.ImportSubtitles(Entries((2, 4, "新")));
        Assert.True(r.Success);
        Assert.Contains("2 件", r.Message);
        Assert.Equal(["新"], e.Project.SubtitleSegments.Select(s => s.Text));
        e.Undo();
        Assert.Equal(["手動", "手動2"], e.Project.SubtitleSegments.Select(s => s.Text));
    }

    [Fact]
    public void InvalidImportChangesNothing()
    {
        var e = Editor();
        e.AddSubtitle(1, 3, "手動");
        var undoLabel = e.History.UndoLabel;
        var r = e.ImportSubtitles(Entries((1, 3, "A"), (2, 4, "B")));
        Assert.False(r.Success);
        Assert.Equal(["手動"], e.Project.SubtitleSegments.Select(s => s.Text));
        Assert.Equal(undoLabel, e.History.UndoLabel);
    }

    [Fact]
    public void BeyondOutputDurationIsRejectedUsingOutputTimeline()
    {
        var e = Editor(subtitlePhase: false);
        e.AddCut(10, 40, SegmentAction.Remove, 0); // 出力は 30 秒
        e.ConfirmCuts();
        Assert.Equal(30, e.OutputDuration, 3);
        Assert.False(e.ImportSubtitles(Entries((29, 35, "A"))).Success);
        Assert.True(e.ImportSubtitles(Entries((20, 30, "A"))).Success);
    }

    [Fact]
    public void RequiresSubtitlePhaseAndEntries()
    {
        Assert.False(Editor(subtitlePhase: false).ImportSubtitles(Entries((1, 3, "A"))).Success);
        Assert.False(Editor().ImportSubtitles([]).Success);
    }

    [Fact]
    public void ImportedSubtitlesRoundTripThroughProjectFile()
    {
        var e = Editor();
        e.ImportSubtitles(Entries((1, 3, "A\nB"), (4, 6, "C")));
        var path = Path.Combine(Path.GetTempPath(), "mvs-import-" + Guid.NewGuid().ToString("N") + ".mvsproj.json");
        try
        {
            e.Project.SourceVideo = new SourceVideo { Path = path };
            new ProjectService().Save(e.Project, path);
            var loaded = new ProjectService().Load(path).Project;
            var e2 = new ProjectEditor(loaded);
            Assert.Equal(["A\nB", "C"], loaded.SubtitleSegments.Select(s => s.Text));
            // 読み込み後も手動字幕と同じように編集できる。
            Assert.True(e2.UpdateSubtitle(loaded.SubtitleSegments[0].Id, 1, 3.5, "A2", SubtitlePosition.Bottom, 48).Success);
            Assert.True(e2.AddSubtitle(10, 12, "D").Result.Success);
            Assert.True(e2.RemoveSubtitle(loaded.SubtitleSegments[1].Id).Success);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}

/// <summary>依頼用動画の保存場所・削除の限定・指紋・作成。</summary>
public class CaptionRequestStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-cr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private CaptionRequestStore Store() => new(Path.Combine(_dir, "caption-requests"));

    [Fact]
    public void NewPathIsUniqueAndHasNoEncryptedExtension()
    {
        var s = Store();
        var now = new DateTime(2026, 10, 7, 12, 0, 0);
        var a = s.NewPath("rec:ord", now);
        File.WriteAllText(a, "x");
        var b = s.NewPath("rec:ord", now);
        Assert.NotEqual(a, b);
        Assert.Equal(".mp4", Path.GetExtension(a));
        Assert.Equal(s.Root, Path.GetDirectoryName(a));
    }

    [Fact]
    public void DeleteOnlyRemovesRegisteredFile()
    {
        var s = Store();
        var mine = s.NewPath("a", DateTime.Now);
        File.WriteAllText(mine, "x");
        s.Register(mine);
        // 同じフォルダーの別のファイル・名前が似たファイル・外のファイルは消さない。
        var other = Path.Combine(s.Root, "other_gemini.mp4");
        File.WriteAllText(other, "x");
        Directory.CreateDirectory(_dir);
        var outside = Path.Combine(_dir, "outside.mp4");
        File.WriteAllText(outside, "x");
        Assert.False(s.Delete(other).Success);
        Assert.False(s.Delete(outside).Success);
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(outside));

        var r = s.Delete(mine);
        Assert.True(r.Success);
        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(s.Root));
    }

    [Fact]
    public void RegisteredPathOutsideRootIsStillRefused()
    {
        var s = Store();
        Directory.CreateDirectory(_dir);
        var outside = Path.Combine(_dir, "x.mp4");
        File.WriteAllText(outside, "x");
        s.Register(outside);
        Assert.False(s.Delete(outside).Success);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteTreatsMissingAsSuccessAndReportsLockedFile()
    {
        var s = Store();
        var p = s.NewPath("a", DateTime.Now);
        File.WriteAllText(p, "x");
        s.Register(p);
        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = s.Delete(p);
            Assert.False(locked.Success);
            Assert.NotEmpty(locked.Message);
        }
        Assert.True(File.Exists(p));
        Assert.True(s.Delete(p).Success);
        // 記録済みのファイルが外部で先に消されていた場合も、成功として扱う。
        var q = s.NewPath("b", DateTime.Now);
        File.WriteAllText(q, "x");
        s.Register(q);
        File.Delete(q);
        Assert.True(s.Delete(q).Success);
    }

    [Fact]
    public void FingerprintChangesWithCutsAndTransition()
    {
        var p = TestProjects.Create(60);
        var f0 = CaptionRequestService.Fingerprint(p, 0.3);
        Assert.Equal(f0, CaptionRequestService.Fingerprint(TestProjects.Create(60), 0.3));
        Assert.NotEqual(f0, CaptionRequestService.Fingerprint(p, 0));
        p.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
        var f1 = CaptionRequestService.Fingerprint(p, 0.3);
        Assert.NotEqual(f0, f1);
        p.CutSegments[0].End = 11;
        Assert.NotEqual(f1, CaptionRequestService.Fingerprint(p, 0.3));
        p.CutSegments[0].Action = SegmentAction.Shorten;
        p.CutSegments[0].KeepSeconds = 1;
        Assert.NotEqual(f1, CaptionRequestService.Fingerprint(p, 0.3));
    }

    private sealed class FakeExport(Func<VideoExportRequest, CancellationToken, Task>? onExport = null) : IVideoExportService
    {
        public VideoExportRequest? Request { get; private set; }
        public async Task<VideoExportResult> ExportAsync(VideoExportRequest request, IProgress<VideoExportProgress>? progress, CancellationToken ct)
        {
            Request = request;
            if (onExport is not null) await onExport(request, ct);
            else await File.WriteAllBytesAsync(request.OutputPath, new byte[100], ct);
            return new VideoExportResult(request.OutputPath, 42, TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PrepareBuildsSubtitleFreeRequestWithSameCutsAndTransition()
    {
        var store = Store();
        var export = new FakeExport();
        var project = TestProjects.Create(60);
        project.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
        project.ShapeGroups.Add(new ShapeGroup { Start = 1, End = 2 });
        project.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "既存" });
        var svc = new CaptionRequestService(export, store);

        var v = await svc.PrepareAsync("ffmpeg", 7, project, "rec", null, CancellationToken.None);

        var req = export.Request!;
        Assert.Null(req.Subtitles);       // 既存字幕は焼き込まない
        Assert.Null(req.Shapes);          // 初期版では図形も含めない
        Assert.Equal(project.ExportSettings.TransitionSecondsForExport, req.TransitionSeconds); // クロスフェードは完成出力と同じ
        Assert.Equal(project.CutSegments, req.CutSegments);
        Assert.Equal(project.SourceVideo.Path, req.InputPath); // 元動画から作る
        Assert.True(store.IsManaged(v.Path));
        Assert.True(File.Exists(v.Path));
        Assert.True(CaptionRequestService.IsCurrent(v, project, project.ExportSettings.TransitionSecondsForExport));
        // カットを変えると再利用できない。
        project.CutSegments[0].End = 12;
        Assert.False(CaptionRequestService.IsCurrent(v, project, project.ExportSettings.TransitionSecondsForExport));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void RequestVideoUsesSameTimelineAsFinalExport(bool crossfade, bool hasAudio)
    {
        var project = TestProjects.Create(60, hasAudio);
        project.CutSegments.Add(new CutSegment { Start = 5, End = 10 });
        project.CutSegments.Add(new CutSegment { Start = 20, End = 30, Action = SegmentAction.Shorten, KeepSeconds = 2 });
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, project.CutSegments);
        double t = crossfade ? ExportSettings.CutTransitionSeconds : 0;
        var request = FFmpegArguments.Export("in.mp4", keep, project.VideoMetadata!, "out.mp4", CaptionRequestService.Encode, 7, null, t, null);
        var final = FFmpegArguments.Export("in.mp4", keep, project.VideoMetadata!, "out.mp4",
            new VideoEncodeOptions { Crf = 22, Preset = "medium", AudioBitrateKbps = 192 }, 7, null, t, null);
        // 画質（CRF・プリセット・音声ビットレート）以外は完成出力と同じ引数（カット・クロスフェード・フィルタ）。
        Assert.Equal(request.FilterScript, final.FilterScript);
        string[] Norm(IEnumerable<string> a) => a.Where(x => x is not ("24" or "22" or "veryfast" or "medium" or "128k" or "192k")).ToArray();
        Assert.Equal(Norm(final.Arguments), Norm(request.Arguments));
        // 長さも同じ（カット後）。
        Assert.Equal(EditTimelineCalculator.OutputDuration(keep), EditTimelineCalculator.OutputDuration(EditTimelineCalculator.ComputeKeepRanges(60, project.CutSegments)));
    }

    [Fact]
    public async Task CancelOrFailureLeavesNothingRegistered()
    {
        var store = Store();
        var project = TestProjects.Create(60);
        var cancel = new FakeExport((r, ct) => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CaptionRequestService(cancel, store).PrepareAsync("ffmpeg", 7, project, "rec", null, CancellationToken.None));
        Assert.False(store.IsManaged(cancel.Request!.OutputPath));
        Assert.False(File.Exists(cancel.Request.OutputPath));

        // ffmpeg が成功を返しても、出力ファイルが無い（空の）ものは準備完了にしない。
        var empty = new FakeExport((r, ct) => File.WriteAllBytesAsync(r.OutputPath, [], ct));
        await Assert.ThrowsAsync<FFmpegException>(() =>
            new CaptionRequestService(empty, store).PrepareAsync("ffmpeg", 7, project, "rec", null, CancellationToken.None));
        Assert.False(store.IsManaged(empty.Request!.OutputPath));
    }

    [Fact]
    public void CacheCleanerDoesNotTouchCaptionRequestDirectory()
    {
        var paths = new AppPaths(Path.Combine(_dir, "roam"), Path.Combine(_dir, "local"));
        Directory.CreateDirectory(paths.CaptionRequestDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        var kept = Path.Combine(paths.CaptionRequestDirectory, "old_gemini.mp4");
        File.WriteAllText(kept, "x");
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-90));
        FrameCacheCleaner.Cleanup(paths.CacheDirectory, TimeSpan.FromDays(30), 1);
        Assert.NotEqual(paths.CacheDirectory, paths.CaptionRequestDirectory);
        Assert.False(paths.CaptionRequestDirectory.StartsWith(paths.CacheDirectory + Path.DirectorySeparatorChar));
        Assert.True(File.Exists(kept));
    }
}

/// <summary>字幕依頼の画面（GeminiCaptionViewModel）の流れ。</summary>
public class GeminiCaptionViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-gv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Dialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<string> Confirms { get; } = [];
        public List<string> Errors { get; } = [];
        public void ShowInfo(string message, string title = "情報") { }
        public void ShowWarning(string message, string title = "注意") { }
        public void ShowError(string message, string? detail = null, string title = "エラー") => Errors.Add(message + "|" + detail);
        public bool Confirm(string message, string title = "確認") { Confirms.Add(message); return ConfirmResult; }
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => null;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    private sealed class Windows : IWindowService
    {
        public bool? DeleteAnswer { get; set; } = true;
        public List<(string Message, string Path)> DeleteAsks { get; } = [];
        public Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null) => throw new NotSupportedException();
        public void ShowAiAssist() { }
        public void CloseAiAssist() { }
        public double? ChooseFrame(FrameCandidatesViewModel vm) => null;
        public bool EditCut(CutEditViewModel vm) => false;
        public void EditImage(ImageEditorViewModel vm) { }
        public string? PromptText(string title, string message, string initial, bool singleLine = false) => null;
        public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
        {
            await work(new Progress<ProgressInfo>(), CancellationToken.None);
            return true;
        }
        public bool EditSubtitle(SubtitleEditViewModel vm) => false;
        public bool ChooseGifQuality(GifExportViewModel vm) => false;
        public bool EditTitle(TitleEditViewModel vm) => false;
        public bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel) => false;
        public Task<RecordingOutcome?> ShowRecorderAsync() => Task.FromResult<RecordingOutcome?>(null);
        public bool IsRecorderOpen => false;
        public void ActivateRecorder() { }
        public ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath) => null;
        public void ShowTejunshoExportDone(TejunshoExportDone result) { }
        public void ShowGeminiCaption(GeminiCaptionViewModel vm) { }
        public bool? ConfirmDeleteRequestVideo(string message, string path) { DeleteAsks.Add((message, path)); return DeleteAnswer; }
    }

    private sealed class Clipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public bool SetText(string text) { Text = text; return true; }
        public string? Stored { get; set; }
        public bool TryGetText(out string text) { text = Stored ?? ""; return !string.IsNullOrEmpty(Stored); }
    }

    private sealed class Shell : IShellService
    {
        public List<string> Urls { get; } = [];
        public List<string> Revealed { get; } = [];
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) => Revealed.Add(file);
        public void OpenUrl(string url) => Urls.Add(url);
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public AppSettings Load() => Current;
        public void Save() { }
    }

    private sealed class Export : IVideoExportService, IMediaMetadataService, IFFmpegInfoService
    {
        public int Calls;
        public Task<VideoExportResult> ExportAsync(VideoExportRequest r, IProgress<VideoExportProgress>? p, CancellationToken ct)
        {
            Calls++;
            File.WriteAllBytes(r.OutputPath, new byte[1000]);
            return Task.FromResult(new VideoExportResult(r.OutputPath, 50, TimeSpan.Zero));
        }
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Rig
    {
        public required GeminiCaptionViewModel Vm;
        public required ProjectEditor Editor;
        public required CaptionRequestSession Session;
        public required Dialogs Dialogs;
        public required Windows Windows;
        public required CaptionRequestStore Store;
        public required Clipboard Clipboard;
        public required Shell Shell;
        public required Export Export;
        public required PromptTemplateService Templates;
    }

    private Rig Create(bool withVideo, Action<ProjectEditor>? setup = null)
    {
        var editor = new ProjectEditor(TestProjects.Create(60));
        editor.ConfirmCuts();
        setup?.Invoke(editor);
        var store = new CaptionRequestStore(Path.Combine(_dir, "caption-requests"));
        var export = new Export();
        var dialogs = new Dialogs();
        var windows = new Windows();
        var clip = new Clipboard();
        var shell = new Shell();
        var settings = new Settings();
        var templates = new PromptTemplateService(Path.Combine(_dir, "prompt-templates.json"));
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), export, settings, NullLogger<FFmpegStatus>.Instance);
        var session = new CaptionRequestSession();
        if (withVideo)
        {
            var path = store.NewPath("rec", DateTime.Now);
            File.WriteAllBytes(path, new byte[100]);
            store.Register(path);
            session.Video = new CaptionRequestVideo(path, CaptionRequestService.Fingerprint(editor.Project, editor.Project.ExportSettings.TransitionSecondsForExport), 50, DateTimeOffset.Now);
        }
        var services = new GeminiCaptionServices(new CaptionRequestService(export, store), store, templates, clip, shell, dialogs, windows, ffmpeg, settings, NullLogger.Instance);
        var vm = new GeminiCaptionViewModel(editor, session, services, "rec");
        return new Rig { Vm = vm, Editor = editor, Session = session, Dialogs = dialogs, Windows = windows, Store = store, Clipboard = clip, Shell = shell, Export = export, Templates = templates };
    }

    private const string Answer = "解析結果\n推奨FontSize：18\n```srt\n1\n00:00:01,000 --> 00:00:04,000\nA\n\n2\n00:00:05,000 --> 00:00:08,000\nB\n```\n";

    [Fact]
    public void ParseOnlyNeverAsksOrDeletes()
    {
        var r = Create(withVideo: true);
        r.Vm.ResponseText = Answer;
        Assert.True(r.Vm.ParseOk);
        Assert.Contains("2 件", r.Vm.ParseSummary);
        Assert.Empty(r.Windows.DeleteAsks);
        Assert.True(File.Exists(r.Session.Video!.Path));
        Assert.Empty(r.Editor.Project.SubtitleSegments);
    }

    [Fact]
    public void InvalidAnswerCannotBeImportedAndNeverAsksToDelete()
    {
        var r = Create(withVideo: true);
        r.Vm.ResponseText = "1\n00:00:01,000 --> 00:00:09,000\nA\n\n2\n00:00:05,000 --> 00:00:08,000\nB\n"; // 重なり
        Assert.False(r.Vm.ParseOk);
        Assert.False(r.Vm.ImportCommand.CanExecute(null));
        Assert.NotEmpty(r.Vm.Issues);
        r.Vm.ImportCommand.Execute(null);
        Assert.Empty(r.Windows.DeleteAsks);
        Assert.Empty(r.Editor.Project.SubtitleSegments);
        Assert.True(File.Exists(r.Session.Video!.Path));
    }

    [Fact]
    public void ImportThenDeleteAnswerYesDeletesOnlyThatVideo()
    {
        var r = Create(withVideo: true);
        var path = r.Session.Video!.Path;
        var bystander = Path.Combine(r.Store.Root, "bystander_gemini.mp4");
        File.WriteAllBytes(bystander, new byte[10]);
        r.Vm.ResponseText = Answer;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);
        var ask = Assert.Single(r.Windows.DeleteAsks);
        Assert.Equal(path, ask.Path);
        Assert.Contains("削除しますか", ask.Message);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(bystander));
        Assert.Null(r.Session.Video);
        Assert.False(r.Vm.HasVideo);
        // 削除後も、字幕の手動編集と Undo・保存形式の往復ができる。
        Assert.True(r.Editor.AddSubtitle(10, 12, "手動").Result.Success);
        r.Editor.Undo();
        r.Editor.Undo();
        Assert.Empty(r.Editor.Project.SubtitleSegments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ImportThenKeepOrCloseKeepsFileAndAsksAgainNextTime(bool? answer)
    {
        var r = Create(withVideo: true);
        r.Windows.DeleteAnswer = answer;
        var path = r.Session.Video!.Path;
        r.Vm.ResponseText = Answer;
        r.Vm.ImportCommand.Execute(null);
        Assert.True(File.Exists(path));
        Assert.NotNull(r.Session.Video);
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);

        // 2 回目の取り込みでも、また確認する（［削除しない］は今回限り）。既定の答えは［削除する］。
        r.Windows.DeleteAnswer = true;
        r.Vm.ResponseText = Answer.Replace("A\n", "A2\n");
        Assert.True(r.Vm.ImportCommand.CanExecute(null));
        r.Dialogs.ConfirmResult = true; // 置き換えの確認
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(2, r.Windows.DeleteAsks.Count);
        Assert.False(File.Exists(path));
        Assert.Equal(["A2", "B"], r.Editor.Project.SubtitleSegments.Select(s => s.Text));
    }

    private static (List<string> Imported, Func<int> Closed) Watch(GeminiCaptionViewModel vm)
    {
        var imported = new List<string>();
        int closed = 0;
        vm.Imported += m => imported.Add(m);
        vm.CloseRequested += () => closed++;
        return (imported, () => closed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScreenClosesAfterDeleteDialogIsAnsweredEitherWay(bool delete)
    {
        var r = Create(withVideo: true);
        var (imported, closed) = Watch(r.Vm);
        r.Windows.DeleteAnswer = delete;
        r.Vm.ResponseText = Answer;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(1, closed());
        Assert.Equal(!delete, r.Session.Video is not null); // 削除したら準備済みを解除、残したら準備済みのまま
        // 閉じても結果が分かるよう、主画面向けに取り込みと削除の結果を 1 つにまとめて通知する。
        var message = Assert.Single(imported);
        Assert.Contains(delete ? "削除しました" : "そのまま残しました", message);
        Assert.DoesNotContain("\n", message);
    }

    [Fact]
    public void ClosingTheDeleteDialogWithXKeepsScreenOpen()
    {
        var r = Create(withVideo: true);
        var (_, closed) = Watch(r.Vm);
        r.Windows.DeleteAnswer = null;
        r.Vm.ResponseText = Answer;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(0, closed());
        Assert.True(File.Exists(r.Session.Video!.Path));
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);
    }

    [Fact]
    public void ScreenStaysOpenWhenImportFailsIsCanceledOrNothingToDelete()
    {
        var r = Create(withVideo: true, e => e.AddSubtitle(1, 2, "手動"));
        var (_, closed) = Watch(r.Vm);
        r.Vm.ResponseText = Answer;
        r.Dialogs.ConfirmResult = false; // 置き換えをキャンセル
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(0, closed());
        r.Vm.ResponseText = "1\n00:00:01,000 --> 00:00:09,000\nA\n\n2\n00:00:05,000 --> 00:00:08,000\nB\n"; // 不正
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(0, closed());
        Assert.Empty(r.Windows.DeleteAsks);

        // 準備済みの動画が無ければ削除確認が出ないため、画面もそのまま。
        var r2 = Create(withVideo: false);
        var (_, closed2) = Watch(r2.Vm);
        r2.Vm.ResponseText = Answer;
        r2.Vm.ImportCommand.Execute(null);
        Assert.Equal(0, closed2());
        Assert.Equal(2, r2.Editor.Project.SubtitleSegments.Count);
    }

    [Fact]
    public void DeleteFailureShowsErrorThenClosesAndReportsInMessage()
    {
        var r = Create(withVideo: true);
        var (imported, closed) = Watch(r.Vm);
        r.Vm.ResponseText = Answer;
        using (new FileStream(r.Session.Video!.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r.Vm.ImportCommand.Execute(null);
        }
        Assert.Single(r.Dialogs.Errors);
        Assert.Equal(1, closed());
        Assert.Contains("削除できませんでした", Assert.Single(imported));
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);
    }

    [Fact]
    public void PasteFromClipboardReplacesAnswerAndValidates()
    {
        var r = Create(withVideo: false);
        r.Vm.ResponseText = "古い回答";
        r.Clipboard.Stored = Answer;
        r.Vm.PasteFromClipboardCommand.Execute(null);
        Assert.Equal(Answer, r.Vm.ResponseText); // 追記ではなく置き換え
        Assert.True(r.Vm.ParseOk);
        Assert.Contains("貼り付けました", r.Vm.PasteMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PasteWithoutTextKeepsAnswerAndExplains(string? stored)
    {
        var r = Create(withVideo: false);
        r.Vm.ResponseText = "そのまま";
        r.Clipboard.Stored = stored;
        r.Vm.PasteFromClipboardCommand.Execute(null);
        Assert.Equal("そのまま", r.Vm.ResponseText);
        Assert.Contains("文字列がありません", r.Vm.PasteMessage);
    }

    [Fact]
    public void DeleteFailureKeepsImportedSubtitlesAndShowsPathAndReason()
    {
        var r = Create(withVideo: true);
        var path = r.Session.Video!.Path;
        r.Vm.ResponseText = Answer;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r.Vm.ImportCommand.Execute(null);
        }
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);
        var err = Assert.Single(r.Dialogs.Errors);
        Assert.Contains(path, err);
        Assert.True(File.Exists(path));
        Assert.NotNull(r.Session.Video); // ファイルが残っているので、準備済みのまま
    }

    [Fact]
    public void ReplacingExistingSubtitlesNeedsConfirmationAndCancelChangesNothing()
    {
        var r = Create(withVideo: true, e => e.AddSubtitle(1, 2, "手動で直した字幕"));
        r.Vm.ResponseText = Answer;
        Assert.Contains("1 件", r.Vm.ReplaceNotice);
        r.Dialogs.ConfirmResult = false;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(["手動で直した字幕"], r.Editor.Project.SubtitleSegments.Select(s => s.Text));
        Assert.Empty(r.Windows.DeleteAsks);
        Assert.True(File.Exists(r.Session.Video!.Path));
        Assert.Contains("1 件", r.Dialogs.Confirms[0]);
        Assert.Contains("2 件", r.Dialogs.Confirms[0]);

        r.Dialogs.ConfirmResult = true;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(["A", "B"], r.Editor.Project.SubtitleSegments.Select(s => s.Text));
        r.Editor.Undo();
        Assert.Equal(["手動で直した字幕"], r.Editor.Project.SubtitleSegments.Select(s => s.Text));
    }

    [Fact]
    public void ImportWithoutPreparedVideoDoesNotAskToDelete()
    {
        var r = Create(withVideo: false);
        r.Vm.ResponseText = Answer;
        r.Vm.ImportCommand.Execute(null);
        Assert.Equal(2, r.Editor.Project.SubtitleSegments.Count);
        Assert.Empty(r.Windows.DeleteAsks);
    }

    [Fact]
    public async Task PrepareCreatesReusesAndInvalidatesOnCutChange()
    {
        var r = Create(withVideo: false);
        // ffmpeg の場所は設定されていない（テスト）ため、まず案内が出て作成しない。
        await r.Vm.PrepareCommand.ExecuteAsync(null);
        Assert.Single(r.Dialogs.Errors);
        Assert.Equal(0, r.Export.Calls);
    }

    [Fact]
    public void SessionInvalidatesWhenProjectNoLongerMatches()
    {
        var r = Create(withVideo: true);
        var path = r.Session.Video!.Path;
        Assert.False(r.Session.InvalidateIfStale(r.Editor.Project, subtitlePhase: true));
        // カットが変わった（カット編集へ戻ってやり直した）。
        r.Editor.Project.CutSegments.Add(new CutSegment { Start = 1, End = 5 });
        Assert.True(r.Session.InvalidateIfStale(r.Editor.Project, subtitlePhase: true));
        Assert.Null(r.Session.Video);
        // ファイルは確認なしには削除せず、古いものとして残す。
        Assert.True(File.Exists(path));
        Assert.Single(r.Session.Orphans);
    }

    [Fact]
    public void OrphansAreDeletedOneByOneWithConfirmation()
    {
        var r = Create(withVideo: true);
        var path = r.Session.Video!.Path;
        r.Session.Invalidate();
        r.Windows.DeleteAnswer = false;
        r.Vm.DeleteOrphansCommand.Execute(null);
        Assert.True(File.Exists(path));
        r.Windows.DeleteAnswer = true;
        r.Vm.DeleteOrphansCommand.Execute(null);
        Assert.False(File.Exists(path));
        Assert.Empty(r.Session.Orphans);
        Assert.Equal(2, r.Windows.DeleteAsks.Count);
    }

    [Fact]
    public void PromptHasNoStyleRecommendationAndMatchesSubtitleRules()
    {
        var r = Create(withVideo: false);
        var text = r.Vm.BuildPrompt().Text;
        // 見た目・位置・サイズの提案は依頼しない（回答の推奨値を設定へ反映する機能もない）。
        Assert.DoesNotContain("推奨FontSize", text);
        Assert.DoesNotContain("推奨MarginV", text);
        Assert.DoesNotContain("【推奨字幕設定】", text);
        Assert.Contains("```srt", text);
        // ManualVideoStudio の字幕の規則に合わせた条件。
        Assert.Contains("200文字", text);
        Assert.Contains("0.2秒", text);
        Assert.Contains("3行", text);
        Assert.DoesNotContain("{{", text); // 変数は展開済み
        Assert.Contains("00:01:00", text); // 動画の長さ（カット後）
        r.Vm.CopyPromptCommand.Execute(null);
        Assert.Equal(text, r.Clipboard.Text);
    }

    [Fact]
    public void OpenGeminiUsesGeminiUrl()
    {
        var r = Create(withVideo: false);
        r.Vm.OpenGeminiCommand.Execute(null);
        Assert.Equal([GeminiCaptionViewModel.GeminiUrl], r.Shell.Urls);
    }

    [Fact]
    public void OpenFolderRevealsPreparedVideo()
    {
        var r = Create(withVideo: true);
        r.Vm.OpenVideoFolderCommand.Execute(null);
        Assert.Equal([r.Session.Video!.Path], r.Shell.Revealed);
    }

    [Fact]
    public void SubtitleRequestTemplateIsCustomizableAndKeptApartFromOlderTemplateKeys()
    {
        var r = Create(withVideo: false);
        Assert.False(r.Templates.IsCustomized(AiPurpose.SubtitleRequest));
        r.Templates.Save(AiPurpose.SubtitleRequest, "独自の依頼 {{EDITED_DURATION}}");
        var reloaded = new PromptTemplateService(Path.Combine(_dir, "prompt-templates.json"));
        Assert.True(reloaded.IsCustomized(AiPurpose.SubtitleRequest));
        Assert.Equal("独自の依頼 {{EDITED_DURATION}}", reloaded.Get(AiPurpose.SubtitleRequest));
        // 既存の用途のテンプレートには影響しない。
        Assert.False(reloaded.IsCustomized(AiPurpose.CaptureCaption));
        reloaded.ResetToDefault(AiPurpose.SubtitleRequest);
        Assert.False(reloaded.IsCustomized(AiPurpose.SubtitleRequest));
    }
}
