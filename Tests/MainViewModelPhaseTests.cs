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
/// 主画面（MainViewModel）の「カット → 字幕」の状態遷移（仕様 §40 のケース 1～4）。
/// ダイアログ・画面は偽物に差し替え、ボタン（コマンド）の有効 / 無効と一覧の内容を確認する。
/// </summary>
public class MainViewModelPhaseTests
{
    internal sealed class FakeDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Infos { get; } = [];
        public void ShowInfo(string message, string title = "情報") => Infos.Add(message);
        public void ShowWarning(string message, string title = "注意") => Warnings.Add(message);
        public void ShowError(string message, string? detail = null, string title = "エラー") => Errors.Add(message);
        public bool Confirm(string message, string title = "確認")
        {
            LastConfirm = message;
            return ConfirmResult;
        }
        public bool? YesNoCancelResult { get; set; } = false;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => YesNoCancelResult;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFileResult { get; set; }
        public int SaveFileCalls { get; private set; }
        public string LastConfirm { get; private set; } = "";
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存")
        {
            SaveFileCalls++;
            return SaveFileResult;
        }
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    internal sealed class FakeWindows : IWindowService
    {
        public bool DangerResult { get; set; }
        public List<string> DangerMessages { get; } = [];
        public int EditCutCalls { get; private set; }
        public string SubtitleText { get; set; } = "ボタンを押します";

        /// <summary>設定画面の［OK］で行う変更（null ならキャンセル扱い）。</summary>
        public Action? SettingsChange { get; set; }
        public Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null)
        {
            if (SettingsChange is null) return Task.FromResult(new SettingsDialogResult(false, false));
            SettingsChange();
            return Task.FromResult(new SettingsDialogResult(true, false));
        }
        public void ShowAiAssist() { }
        public void CloseAiAssist() => AiClosed++;
        public int AiClosed;
        public double? ChooseFrame(FrameCandidatesViewModel vm) => null;
        public bool EditCut(CutEditViewModel vm) { EditCutCalls++; return false; }
        public void EditImage(ImageEditorViewModel vm) { }
        public string? PromptText(string title, string message, string initial, bool singleLine = false) => null;
        public Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work) => Task.FromResult(false);

        public bool EditSubtitle(SubtitleEditViewModel vm)
        {
            bool? closed = null;
            vm.CloseRequested += ok => closed = ok;
            vm.Text = SubtitleText;
            vm.OkCommand.Execute(null);
            return closed == true;
        }

        public bool ChooseGifQuality(GifExportViewModel vm) => false;
        /// <summary>タイトル編集画面の代わり（null ならキャンセル扱い）。</summary>
        public Func<TitleEditViewModel, bool>? TitleEdit { get; set; }
        public void ShowAbout(AboutViewModel vm) { }
        public bool EditTitle(TitleEditViewModel vm) => TitleEdit?.Invoke(vm) ?? false;

        public bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel)
        {
            DangerMessages.Add(message + "|" + okLabel + "|" + cancelLabel);
            return DangerResult;
        }

        public Task<RecordingOutcome?> ShowRecorderAsync() => Task.FromResult<RecordingOutcome?>(null);
        public bool IsRecorderOpen => false;
        public void ActivateRecorder() { }
        public ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath) => null;
        public List<TejunshoExportDone> TejunshoDone { get; } = [];
        public void ShowTejunshoExportDone(TejunshoExportDone result) => TejunshoDone.Add(result);
        public void ShowGeminiCaption(GeminiCaptionViewModel vm) { GeminiCaptionOpened++; GeminiVm = vm; }
        public int GeminiCaptionOpened;
        public GeminiCaptionViewModel? GeminiVm;
        /// <summary>削除確認の答え（既定は［削除する］）。</summary>
        public bool? DeleteAnswer { get; set; } = true;
        public List<string> DeletePaths { get; } = [];
        public bool? ConfirmDeleteRequestVideo(string message, string path) { DeletePaths.Add(path); return DeleteAnswer; }
    }

    internal sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public AppSettings Load() => Current;
        public void Save() { }
    }

    internal sealed class FakeProjects(ProjectDocument project) : IProjectService
    {
        public List<(string Path, EditPhase Phase)> Saved { get; } = [];
        public void Save(ProjectDocument p, string path) => Saved.Add((path, p.EditPhase));
        public ProjectLoadResult Load(string path) => new(project, true, null);
    }

    internal sealed class Unused : IMediaMetadataService, IFrameExtractionService, IVideoExportService, IGifExportService, ILocalAnalysisService, IShellService, IFFmpegInfoService
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

    /// <summary>
    /// 録画・動画から開いて一度も保存していない状態（ProjectPath が null）。動画は root 直下のプロジェクトフォルダーに置く。
    /// </summary>
    private static async Task<(MainViewModel Vm, FakeDialogs Dialogs, FakeProjects Projects)> OpenUnsavedAsync(string root, string folderName)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, folderName)).FullName;
        var project = TestProjects.Create(60);
        project.SourceVideo.Path = Path.Combine(folder, "rec_20260926_120000.mp4");
        var dialogs = new FakeDialogs();
        var settings = new FakeSettings();
        settings.Current.Recording.OutputDirectory = root;
        var projects = new FakeProjects(project);
        var unused = new Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var vm = new MainViewModel(settings, projects, unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, new FakeWindows(), unused, ffmpeg, NullLogger<MainViewModel>.Instance);
        await vm.OpenProjectFileAsync(Path.Combine(folder, "loaded.mvsproj.json"));
        vm.ProjectPath = null;
        return (vm, dialogs, projects);
    }

    internal static async Task<(MainViewModel Vm, FakeDialogs Dialogs, FakeWindows Windows)> OpenAsync(string uiMode = UiModes.Full, bool externalDocument = false)
    {
        var dialogs = new FakeDialogs();
        var windows = new FakeWindows();
        var settings = new FakeSettings();
        settings.Current.UiMode = uiMode;
        settings.Current.EnableExternalDocumentExport = externalDocument; // 既定は OFF。文書作成用の機能を確かめるテストだけ明示的に ON にする（v1.14）
        var unused = new Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var vm = new MainViewModel(settings, new FakeProjects(TestProjects.Create(60)), unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, windows, unused, ffmpeg, NullLogger<MainViewModel>.Instance);
        await vm.OpenProjectFileAsync(Path.Combine(Path.GetTempPath(), "phase-test.mvsproj.json"));
        Assert.True(vm.HasProject);
        return (vm, dialogs, windows);
    }

    private static void AddRemoveCut(MainViewModel vm, double start, double end)
    {
        vm.SeekTo(start);
        vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(end);
        vm.SetMarkOutCommand.Execute(null);
        vm.AddRemoveCutCommand.Execute(null);
    }

    private static void AddSubtitle(MainViewModel vm, double sourceStart, double sourceEnd)
    {
        vm.SeekTo(sourceStart);
        vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(sourceEnd);
        vm.SetMarkOutCommand.Execute(null);
        Assert.True(vm.AddSubtitleCommand.CanExecute(null));
        vm.AddSubtitleCommand.Execute(null);
    }

    [Fact]
    public async Task Case1_LoadCutConfirmSubtitleThenCutUiIsDisabled()
    {
        var (vm, _, windows) = await OpenAsync();
        Assert.True(vm.IsCutPhase);
        Assert.False(vm.AddSubtitleCommand.CanExecute(null)); // カット確定前は字幕を追加できない
        AddRemoveCut(vm, 5, 10);
        Assert.Single(vm.CutItems);

        Assert.True(vm.ConfirmCutsCommand.CanExecute(null));
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        Assert.True(vm.IsCutLocked);
        Assert.False(vm.ConfirmCutsCommand.CanExecute(null));
        Assert.Equal(MainViewModel.TabSubtitles, vm.SelectedTabIndex);

        AddSubtitle(vm, 12, 15); // 元動画 12～15 秒 = 編集後 7～10 秒
        var item = Assert.Single(vm.SubtitleItems);
        Assert.Equal("00:07.000", item.StartText);
        Assert.Equal("00:10.000", item.EndText);

        // カット操作はできない（開始・終了を指定してもボタンは無効、コマンドを直接呼んでも変わらない）。
        vm.SeekTo(20);
        vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(25);
        vm.SetMarkOutCommand.Execute(null);
        Assert.False(vm.AddRemoveCutCommand.CanExecute(null));
        vm.AddRemoveCutCommand.Execute(null);
        vm.DeleteCutsCommand.Execute(vm.CutItems.ToList());
        vm.EditCutCommand.Execute(vm.CutItems[0]);
        Assert.Single(vm.CutItems);
        Assert.Equal(0, windows.EditCutCalls);
        Assert.Contains("カットを変更できません", vm.StatusMessage);
    }

    [Fact]
    public async Task Case2_ReturnToCutsWithSubtitlesWarnsAndCancelKeepsSubtitles()
    {
        var (vm, _, windows) = await OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        AddSubtitle(vm, 1, 3);
        windows.DangerResult = false; // キャンセル
        vm.ReturnToCutEditingCommand.Execute(null);

        var shown = Assert.Single(windows.DangerMessages);
        Assert.Contains("字幕が存在するため動画カットを変更できません", shown);
        Assert.Contains("字幕をすべて破棄してカット編集へ戻る|キャンセル", shown);
        Assert.Single(vm.SubtitleItems);
        Assert.True(vm.IsSubtitlePhase);
        Assert.True(vm.IsCutLocked);
    }

    [Fact]
    public async Task Case3_DiscardAllSubtitlesMakesCutEditable()
    {
        var (vm, _, windows) = await OpenAsync();
        AddRemoveCut(vm, 5, 10);
        vm.ConfirmCutsCommand.Execute(null);
        AddSubtitle(vm, 1, 3);
        AddSubtitle(vm, 20, 22);
        Assert.Equal(2, vm.SubtitleItems.Count);

        windows.DangerResult = true; // 字幕をすべて破棄してカット編集へ戻る
        vm.ReturnToCutEditingCommand.Execute(null);
        Assert.Empty(vm.SubtitleItems);
        Assert.Empty(vm.Project!.SubtitleSegments);
        Assert.Empty(vm.TimelineSubtitles);
        Assert.True(vm.IsCutPhase);
        Assert.False(vm.IsCutLocked);

        AddRemoveCut(vm, 30, 35);
        Assert.Equal(2, vm.CutItems.Count);
    }

    [Fact]
    public async Task Case4_WithoutSubtitlesReturnsWithoutWarning()
    {
        var (vm, _, windows) = await OpenAsync();
        AddRemoveCut(vm, 5, 10);
        vm.ConfirmCutsCommand.Execute(null);
        vm.ReturnToCutEditingCommand.Execute(null);
        Assert.Empty(windows.DangerMessages);
        Assert.True(vm.IsCutPhase);
        AddRemoveCut(vm, 30, 35);
        Assert.Equal(2, vm.CutItems.Count);
    }

    [Fact]
    public async Task ConfirmCutsSavesUnsavedProjectToProjectFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mvs-phase-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, dialogs, projects) = await OpenUnsavedAsync(root, "20260926_1200_初期設定");
            AddRemoveCut(vm, 5, 10);
            var expected = Path.Combine(root, "20260926_1200_初期設定", "20260926_1200_初期設定" + ProjectService.Extension);
            vm.ConfirmCutsCommand.Execute(null);
            Assert.Contains("字幕編集へ進むときに保存します", dialogs.LastConfirm);
            Assert.Contains(expected, dialogs.LastConfirm);
            Assert.True(vm.IsSubtitlePhase);
            Assert.Equal(0, dialogs.SaveFileCalls);                       // 保存先は尋ねない
            Assert.Equal((expected, EditPhase.Subtitle), Assert.Single(projects.Saved));   // 字幕編集の段階で保存する
            Assert.Equal(expected, vm.ProjectPath);
            Assert.False(vm.IsDirty);
            Assert.Contains("プロジェクトを保存しました", vm.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConfirmCutsAsksWhereToSaveWhenProjectFileAlreadyExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "mvs-phase-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, dialogs, projects) = await OpenUnsavedAsync(root, "20260926_1200_初期設定");
            File.WriteAllText(Path.Combine(root, "20260926_1200_初期設定", "20260926_1200_初期設定" + ProjectService.Extension), "{}");
            vm.ConfirmCutsCommand.Execute(null);
            Assert.Contains("保存先を尋ねます", dialogs.LastConfirm);
            Assert.Equal(1, dialogs.SaveFileCalls);
            // 取り消してもカットの確定はそのまま。保存していないことを知らせる。
            Assert.True(vm.IsSubtitlePhase);
            Assert.Empty(projects.Saved);
            Assert.Null(vm.ProjectPath);
            Assert.Contains("まだ保存されていません", vm.StatusMessage);

            // 選んだ場所へ保存する場合。
            vm.ReturnToCutEditingCommand.Execute(null);
            dialogs.SaveFileResult = Path.Combine(root, "別名.mvsproj.json");
            vm.ConfirmCutsCommand.Execute(null);
            Assert.Equal(dialogs.SaveFileResult, Assert.Single(projects.Saved).Path);
            Assert.Equal(dialogs.SaveFileResult, vm.ProjectPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConfirmCutsDoesNotSaveProjectThatAlreadyHasFile()
    {
        var (vm, dialogs, _) = await OpenAsync();
        AddRemoveCut(vm, 5, 10);
        vm.ConfirmCutsCommand.Execute(null);
        Assert.DoesNotContain("保存", dialogs.LastConfirm);
        Assert.Equal(0, dialogs.SaveFileCalls);
        Assert.True(vm.IsDirty);   // 保存済みのプロジェクトは今までどおり（明示的に保存する）
    }

    [Fact]
    public async Task ConfirmCanBeCanceled()
    {
        var (vm, dialogs, _) = await OpenAsync();
        dialogs.ConfirmResult = false;
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsCutPhase);
    }

    [Fact]
    public async Task UndoRestoresDiscardedSubtitles()
    {
        var (vm, _, windows) = await OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        AddSubtitle(vm, 1, 3);
        windows.DangerResult = true;
        vm.ReturnToCutEditingCommand.Execute(null);
        Assert.Empty(vm.SubtitleItems);
        vm.UndoCommand.Execute(null);
        Assert.Single(vm.SubtitleItems);
        Assert.True(vm.IsSubtitlePhase);
    }

    [Fact]
    public async Task PreviewShowsSubtitleAtCurrentPosition()
    {
        var (vm, _, _) = await OpenAsync();
        AddRemoveCut(vm, 5, 10);
        vm.ConfirmCutsCommand.Execute(null);
        AddSubtitle(vm, 12, 15);
        vm.SeekTo(13);
        Assert.Equal("ボタンを押します", vm.CurrentSubtitle?.Text);
        vm.SeekTo(7); // 削除される部分では出さない
        Assert.Null(vm.CurrentSubtitle);
        vm.SeekTo(16);
        Assert.Null(vm.CurrentSubtitle);
        Assert.Equal(3, Assert.Single(vm.TimelineSubtitles).SourceRanges.Sum(r => r.Duration), 3);
    }

    [Fact]
    public async Task Case5_ShapesAreListedPreviewedAndDiscardedWithSubtitles()
    {
        var (vm, _, windows) = await OpenAsync();
        Assert.False(vm.AddShapesCommand.CanExecute(null)); // カット確定前は図形も入れられない
        AddRemoveCut(vm, 5, 10);
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.AddShapesCommand.CanExecute(null));
        // 編集後 7～10 秒（元動画 12～15 秒）に四角形
        Assert.True(vm.Editor!.AddShapeGroup(7, 10, [new RectangleAnnotation { Width = 10, Height = 10 }], 1920, 1080).Result.Success);
        var item = Assert.Single(vm.ShapeItems);
        Assert.Equal("00:07.000", item.StartText);
        Assert.Equal("四角形", item.Summary);
        var track = Assert.Single(vm.TimelineShapes);
        Assert.Equal(12, track.SourceRanges[0].Start, 3);
        Assert.False(track.HasMosaic);

        vm.SeekTo(13);
        Assert.Single(vm.CurrentShapeGroups);
        vm.SeekTo(16);
        Assert.Empty(vm.CurrentShapeGroups);

        windows.DangerResult = false;
        vm.ReturnToCutEditingCommand.Execute(null);
        var shown = Assert.Single(windows.DangerMessages);
        Assert.Contains("字幕・図形が存在するため", shown);
        Assert.Contains("図形: 1 件", shown);
        Assert.Contains("字幕・図形をすべて破棄してカット編集へ戻る|キャンセル", shown);
        Assert.Single(vm.ShapeItems);

        windows.DangerResult = true;
        vm.ReturnToCutEditingCommand.Execute(null);
        Assert.Empty(vm.ShapeItems);
        Assert.Empty(vm.TimelineShapes);
        Assert.True(vm.IsCutPhase);
        vm.UndoCommand.Execute(null);
        Assert.Single(vm.ShapeItems);
        Assert.True(vm.IsSubtitlePhase);
    }

    // ---------------- 簡易モード（v1.11） ----------------

    private static readonly string[] FullOnlyCommands = ["AddCapture", "ExportImages", "ExportTejunsho", "ShowPromptSettings"];

    private static Dictionary<string, bool> FullOnlyCanExecute(MainViewModel vm) => new()
    {
        ["AddCapture"] = vm.AddCaptureCommand.CanExecute(null),
        ["ExportImages"] = vm.ExportImagesCommand.CanExecute(null),
        ["ExportTejunsho"] = vm.ExportTejunshoCommand.CanExecute(null),
        ["ShowPromptSettings"] = vm.ShowPromptSettingsCommand.CanExecute(null),
    };

    [Fact]
    public async Task SimpleModeDisablesImageAndAiCommands()
    {
        var (vm, _, _) = await OpenAsync(UiModes.Simple);
        Assert.True(vm.IsSimpleMode);
        Assert.Equal("簡易モード", vm.UiModeText);
        foreach (var (name, can) in FullOnlyCanExecute(vm)) Assert.False(can, name);
        // カット・書き出し・自動解析は使える。
        Assert.True(vm.SetMarkInCommand.CanExecute(null));
        Assert.True(vm.ExportVideoCommand.CanExecute(null));
        Assert.True(vm.ExportGifCommand.CanExecute(null));
        Assert.True(vm.RunAnalysisCommand.CanExecute(null));
        // AI 連携（不要区間の解析）は簡易モードでも使える（v1.17.0）。
        Assert.True(vm.ShowAiAssistCommand.CanExecute(null));
        Assert.True(vm.AskAiForCandidatesCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UiModes.Full)]
    [InlineData("")]
    public async Task FullModeEnablesImageAndAiCommands(string mode)
    {
        var (vm, _, _) = await OpenAsync(mode, externalDocument: true);
        Assert.False(vm.IsSimpleMode);
        Assert.Equal("", vm.UiModeText);
        Assert.Equal(FullOnlyCommands, FullOnlyCanExecute(vm).Where(p => p.Value).Select(p => p.Key));
    }

    [Fact]
    public async Task SimpleModeNeverSelectsCaptureTab()
    {
        var (vm, _, _) = await OpenAsync(UiModes.Simple);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
        vm.SelectedTabIndex = MainViewModel.TabCandidates;
        vm.SelectedTabIndex = MainViewModel.TabCaptures;
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task SwitchingToSimpleHidesCapturesButKeepsThem()
    {
        var (vm, _, windows) = await OpenAsync(UiModes.Full);
        Assert.True(vm.Editor!.AddCapture(10).Result.Success);
        vm.SelectedTabIndex = MainViewModel.TabCaptures;
        Assert.Single(vm.TimelineCaptures);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        windows.SettingsChange = () => vm.Settings.UiMode = UiModes.Simple;
        await vm.ShowSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.IsSimpleMode);
        Assert.Contains(nameof(MainViewModel.IsFullMode), changed);
        Assert.Contains(nameof(MainViewModel.UiModeText), changed);
        Assert.Contains(nameof(MainViewModel.TimelineCaptures), changed);
        Assert.Empty(vm.TimelineCaptures);
        Assert.Single(vm.Project!.CapturePoints);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
        Assert.Equal(1, windows.AiClosed);
        foreach (var (name, can) in FullOnlyCanExecute(vm)) Assert.False(can, name);

        // 全機能モードに戻すと、残っていた画像ポイントがまた見える。
        windows.SettingsChange = () => vm.Settings.UiMode = UiModes.Full;
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.Single(vm.TimelineCaptures);
        Assert.Single(vm.Captures);
        Assert.True(vm.AddCaptureCommand.CanExecute(null));
        Assert.Equal(1, windows.AiClosed);
    }

    [Fact]
    public async Task SettingsWithoutModeChangeDoNotCloseAiWindow()
    {
        var (vm, _, windows) = await OpenAsync(UiModes.Simple);
        windows.SettingsChange = () => vm.Settings.Language = "English";
        await vm.ShowSettingsCommand.ExecuteAsync(null);
        Assert.Equal(0, windows.AiClosed);
        Assert.True(vm.IsSimpleMode);
    }
}
