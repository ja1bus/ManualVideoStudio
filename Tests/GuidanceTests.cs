using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Guidance;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Tests;

/// <summary>作業の流れ・次にすること・空の一覧の案内の判定（v1.13。UI 非依存）。</summary>
public class WorkflowGuideTests
{
    [Fact]
    public void VideoFlowIsSourceCutSubtitleShapeExport()
        => Assert.Equal([FlowStep.Source, FlowStep.Cut, FlowStep.SubtitleShape, FlowStep.Export], WorkflowGuide.Steps(GuideWorkspace.Video));

    [Fact]
    public void ImageFlowIsSourceImageAddCaptionExport()
        => Assert.Equal([FlowStep.Source, FlowStep.ImageAdd, FlowStep.CaptionAnnotation, FlowStep.Export], WorkflowGuide.Steps(GuideWorkspace.Images));

    [Theory]
    [InlineData(false, GuideWorkspace.Video, false, 0, FlowStep.Source)]
    [InlineData(false, GuideWorkspace.Images, false, 3, FlowStep.Source)]
    [InlineData(true, GuideWorkspace.Video, false, 0, FlowStep.Cut)]
    [InlineData(true, GuideWorkspace.Video, true, 0, FlowStep.SubtitleShape)]
    [InlineData(true, GuideWorkspace.Images, false, 0, FlowStep.ImageAdd)]
    [InlineData(true, GuideWorkspace.Images, true, 2, FlowStep.CaptionAnnotation)]
    public void CurrentStepFollowsState(bool hasProject, GuideWorkspace ws, bool subtitlePhase, int captures, FlowStep expected)
        => Assert.Equal(expected, WorkflowGuide.CurrentStep(hasProject, ws, subtitlePhase, captures));

    [Theory]
    [InlineData(false, GuideWorkspace.Video, false, false, GuideNextAction.StartNew)]
    [InlineData(true, GuideWorkspace.Video, false, false, GuideNextAction.CutEditing)]
    [InlineData(true, GuideWorkspace.Video, true, false, GuideNextAction.SubtitleEditing)]
    [InlineData(true, GuideWorkspace.Video, true, true, GuideNextAction.SubtitlesImported)]
    [InlineData(true, GuideWorkspace.Video, false, true, GuideNextAction.CutEditing)] // 取り込み直後の案内は字幕編集中だけ
    [InlineData(true, GuideWorkspace.Images, false, false, GuideNextAction.ImageCreation)]
    [InlineData(true, GuideWorkspace.Images, true, true, GuideNextAction.ImageCreation)]
    public void NextActionFollowsState(bool hasProject, GuideWorkspace ws, bool subtitlePhase, bool imported, GuideNextAction expected)
        => Assert.Equal(expected, WorkflowGuide.NextAction(hasProject, ws, subtitlePhase, imported));

    [Theory]
    [InlineData(AnalysisRunState.NotRun, 0, 0, CandidateEmptyKind.NotAnalyzed)]
    [InlineData(AnalysisRunState.Done, 0, 0, CandidateEmptyKind.AnalyzedNoCandidates)]
    [InlineData(AnalysisRunState.Failed, 0, 0, CandidateEmptyKind.AnalysisFailed)] // 失敗を「候補なし」にしない
    [InlineData(AnalysisRunState.Failed, 2, 0, CandidateEmptyKind.AnalysisFailed)]
    [InlineData(AnalysisRunState.Done, 3, 0, CandidateEmptyKind.OnlyHandled)]
    [InlineData(AnalysisRunState.Done, 3, 2, CandidateEmptyKind.None)]
    [InlineData(AnalysisRunState.NotRun, 1, 1, CandidateEmptyKind.None)]
    public void CandidateEmptyDistinguishesStates(AnalysisRunState state, int total, int shown, CandidateEmptyKind expected)
        => Assert.Equal(expected, WorkflowGuide.CandidateEmpty(state, total, shown));

    [Theory]
    [InlineData(0, 0, AnalysisRunState.NotRun)]
    [InlineData(5, 0, AnalysisRunState.Done)]
    [InlineData(0, 2, AnalysisRunState.Done)]
    public void InferAnalysisStateFromSavedResults(int scenes, int candidates, AnalysisRunState expected)
        => Assert.Equal(expected, WorkflowGuide.InferAnalysisState(scenes, candidates));
}

/// <summary>開始画面の「最近のプロジェクト」（既存のプロジェクト索引から作る）。</summary>
public class RecentProjectListTests
{
    private static ProjectIndex Index() => new()
    {
        Recordings =
        [
            new IndexedRecording
            {
                Folder = "20261001_1000_a", Name = "a", Date = new DateTime(2026, 10, 1, 10, 0, 0), Video = "rec.mp4",
                Edits =
                [
                    new IndexedEdit { Name = "a", Path = "a.mvsproj.json", UpdatedAt = new DateTime(2026, 10, 3) },
                    new IndexedEdit { Name = "a2", Path = @"edits\x\x.mvsproj.json", UpdatedAt = new DateTime(2026, 10, 5), Problem = "元の動画が見つかりません" },
                ],
            },
            new IndexedRecording { Folder = "20261002_1000_b", Name = "b", Date = new DateTime(2026, 10, 2), Video = "rec.mp4" },
            new IndexedRecording { Folder = "20261004_1000_c", Name = "c", Date = new DateTime(2026, 10, 4), Video = "" }, // 動画なしの録画は出さない
        ],
    };

    [Fact]
    public void NewestFirstWithEditsAndVideoOnlyRecordings()
    {
        var root = Path.Combine(Path.GetTempPath(), "mvs-root");
        var list = RecentProjectList.Build(Index(), root, 10);
        Assert.Equal(["a2", "a", "b"], list.Select(x => x.Name));
        Assert.False(list[0].IsVideoOnly);
        Assert.Equal("元の動画が見つかりません", list[0].Problem);
        Assert.True(list[2].IsVideoOnly);
        Assert.EndsWith("rec.mp4", list[2].FullPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "20261001_1000_a", @"edits\x\x.mvsproj.json")), list[0].FullPath);
    }

    [Fact]
    public void CountLimitsTheList()
    {
        Assert.Single(RecentProjectList.Build(Index(), "r", 1));
        Assert.Empty(RecentProjectList.Build(Index(), "r", 0));
        Assert.Empty(RecentProjectList.Build(new ProjectIndex(), "r", 5));
    }
}

/// <summary>文字列リソース（v1.13）。キーの抜け・空の文言・書式の食い違いを防ぐ。</summary>
public class LocalizationTests
{
    private static readonly ResourceManager Manager = new("ManualVideoStudio.Resources.Strings", typeof(Loc).Assembly);

    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ManualVideoStudio.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("ManualVideoStudio.csproj が見つかりません。");
    }

    [Fact]
    public void UnknownKeyIsVisibleNotEmpty() => Assert.Equal("!No.Such.Key!", Loc.Get("No.Such.Key"));

    [Fact]
    public void FormatFillsPlaceholders()
        => Assert.Equal("画像 No.3 を追加しました（00:00:10.000）。［画像作成］で確認・編集できます。［前後比較］で表示の整ったフレームを選べます。",
            Loc.Format("Status.CapturePoint", 3, "00:00:10.000"));

    [Fact]
    public void EveryResourceHasTextAndWholeSentencePlaceholders()
    {
        var resx = File.ReadAllText(Path.Combine(ProjectRoot(), "Resources", "Strings.resx"));
        var keys = Regex.Matches(resx, "<data name=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(keys.Count > 100);
        Assert.Equal(keys.Count, keys.Distinct().Count()); // キーの重複なし
        foreach (var key in keys)
        {
            var text = Manager.GetString(key, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            // {0} などの書式の番号は 0 から連続している（文章全体を翻訳でき、番号が飛んでいない）。
            var nums = Regex.Matches(text!, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(0, nums.Count), nums);
        }
    }

    [Fact]
    public void KeysUsedInXamlAndCodeExist()
    {
        var root = ProjectRoot();
        var used = new HashSet<string>();
        foreach (var file in new[] { "MainWindow.xaml", @"Views\ProjectListWindow.xaml", @"Views\RecorderWindow.xaml" })
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, file)), @"\{local:Str ([A-Za-z0-9_.]+)\}")) used.Add(m.Groups[1].Value);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "ViewModels"), "*.cs").Concat(Directory.GetFiles(root, "MainWindow.xaml.cs")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.(?:Get|Format)\(""([A-Za-z0-9_.]+)""")) used.Add(m.Groups[1].Value);
        Assert.NotEmpty(used);
        // "Guide." + 列挙値 のように後ろを足して作るキーは、KeysBuiltFromEnumsExist で確かめる。
        foreach (var key in used.Where(k => !k.EndsWith('.'))) Assert.True(Manager.GetString(key, CultureInfo.InvariantCulture) is not null, key);
    }

    [Fact]
    public void KeysBuiltFromEnumsExist()
    {
        foreach (var a in Enum.GetValues<GuideNextAction>()) Assert.NotNull(Manager.GetString("Guide." + a, CultureInfo.InvariantCulture));
        foreach (var s in Enum.GetValues<FlowStep>()) Assert.NotNull(Manager.GetString("Flow." + s, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NewVisibleStringsAreNotHardCodedInTheMainXaml()
    {
        // 今回新設・変更した操作欄（共通バー・開始画面・作業の流れ・カット操作・字幕操作）の文言は、直書きせずリソースを使う。
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot(), "MainWindow.xaml"));
        foreach (var literal in new[] { "Content=\"保存\"", "Content=\"プロジェクト\"", "Content=\"動画を開く\"", "Content=\"字幕・図形へ進む", "Content=\"範囲指定を解除\"", "Content=\"短縮を登録\"" })
            Assert.DoesNotContain(literal, xaml);
    }
}

/// <summary>主画面の案内・作業領域・空の一覧の表示が、状態・Undo/Redoに追従する（v1.13）。</summary>
public class MainViewModelGuidanceTests
{
    private static void AddRemoveCut(MainViewModel vm, double start, double end)
    {
        vm.SeekTo(start);
        vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(end);
        vm.SetMarkOutCommand.Execute(null);
        vm.AddRemoveCutCommand.Execute(null);
    }

    private static string Text(string key) => Loc.Get(key);

    private static async Task<(MainViewModel Vm, MainViewModelPhaseTests.FakeDialogs Dialogs, MainViewModelPhaseTests.FakeWindows Windows)> InSubtitlePhaseAsync(string mode = UiModes.Full)
    {
        var r = await MainViewModelPhaseTests.OpenAsync(mode);
        r.Vm.ConfirmCutsCommand.Execute(null);
        Assert.True(r.Vm.IsSubtitlePhase);
        return r;
    }

    private static void AddSubtitleAt(MainViewModel vm, double start, double end, string text)
    {
        vm.Editor!.AddSubtitle(start, end, text, SubtitlePosition.Bottom, 48);
    }

    [Fact]
    public async Task NoProjectShowsOnlyTheWelcomeAndStartGuidance()
    {
        var vm = new MainViewModelPhaseTests_Helper().NewEmpty();
        Assert.False(vm.HasProject);
        Assert.True(vm.ShowWelcome);
        Assert.False(vm.IsVideoWorkspace);
        Assert.False(vm.ShowCutTools);
        Assert.False(vm.ShowSubtitleTools);
        Assert.False(vm.IsImageWorkspace);
        Assert.Equal(GuideNextAction.StartNew, vm.NextAction);
        Assert.Equal("録画するか、動画を開いて始めてください。", vm.NextActionText);
        Assert.Equal(FlowStep.Source, vm.CurrentFlowStep);
        Assert.True(vm.StartRecordingCommand.CanExecute(null));
        Assert.True(vm.OpenVideoCommand.CanExecute(null));
        Assert.True(vm.OpenProjectCommand.CanExecute(null));
        Assert.True(vm.ShowProjectListCommand.CanExecute(null));
        Assert.False(vm.SaveProjectCommand.CanExecute(null));
        Assert.False(vm.ExportVideoCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CutPhaseGuidanceAndEmptyListsFollowEditsAndUndoRedo()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.False(vm.ShowWelcome);
        Assert.True(vm.ShowCutTools);
        Assert.False(vm.ShowSubtitleTools);
        Assert.Equal(GuideNextAction.CutEditing, vm.NextAction);
        Assert.Equal(FlowStep.Cut, vm.CurrentFlowStep);
        Assert.Equal(["素材", "カット", "字幕・図形", "書き出し"], vm.FlowSteps.Select(s => s.Label));
        Assert.Equal(["カット"], vm.FlowSteps.Where(s => s.IsCurrent).Select(s => s.Label));
        Assert.Equal(["素材"], vm.FlowSteps.Where(s => s.IsDone).Select(s => s.Label));
        Assert.True(vm.ShowCutsEmpty);

        AddRemoveCut(vm, 5, 10);
        Assert.False(vm.ShowCutsEmpty);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.ShowCutsEmpty);   // 元に戻すと案内も戻る
        vm.RedoCommand.Execute(null);
        Assert.False(vm.ShowCutsEmpty);  // やり直しで消える
    }

    [Fact]
    public async Task CutsAreNotRequiredToProceed()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full, externalDocument: true);
        Assert.Empty(vm.Project!.CutSegments);
        Assert.True(vm.ConfirmCutsCommand.CanExecute(null));
        Assert.True(vm.ExportVideoCommand.CanExecute(null));   // 字幕編集へ進まなくても既存の出力は使える
        Assert.True(vm.ExportGifCommand.CanExecute(null));
        Assert.True(vm.ExportImagesCommand.CanExecute(null));
        Assert.True(vm.ExportTejunshoCommand.CanExecute(null));
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
    }

    [Fact]
    public async Task SubtitlePhaseGuidanceLists_AndUndoOfConfirmReturnsToCutGuidance()
    {
        var (vm, _, _) = await InSubtitlePhaseAsync();
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction);
        Assert.Equal(FlowStep.SubtitleShape, vm.CurrentFlowStep);
        Assert.Equal(MainViewModel.TabSubtitles, vm.SelectedTabIndex);
        Assert.True(vm.ShowSubtitleTools);
        Assert.False(vm.ShowCutTools);
        Assert.True(vm.ShowSubtitlesEmpty);
        Assert.True(vm.ShowShapesEmpty);

        AddSubtitleAt(vm, 1, 3, "最初");
        Assert.False(vm.ShowSubtitlesEmpty);

        // カット確定の取り消し（Undo）では、カット編集の案内と一覧に戻る。
        vm.Editor!.Undo();   // 字幕の追加
        vm.UndoCommand.Execute(null); // カットの確定
        Assert.True(vm.IsCutPhase);
        Assert.Equal(GuideNextAction.CutEditing, vm.NextAction);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
        Assert.True(vm.ShowCutTools);
        vm.RedoCommand.Execute(null);
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction);
        Assert.Equal(MainViewModel.TabSubtitles, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task ImportedSubtitlesGuidanceIsShownUntilTheyChange()
    {
        var (vm, _, _) = await InSubtitlePhaseAsync();
        vm.Editor!.ImportSubtitles([(1.0, 3.0, "取り込み")]);
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction);  // 取り込みの通知の前は通常の案内
        vm.NotifySubtitlesImported();
        Assert.Equal(GuideNextAction.SubtitlesImported, vm.NextAction);
        Assert.Equal("取り込んだ字幕を再生して確認し、文章や表示時間を調整してください。", vm.NextActionText);

        vm.UndoCommand.Execute(null); // 取り込みを元に戻す
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction);
        vm.RedoCommand.Execute(null);  // 取り込みをやり直しても、同じ内容に戻れば「取り込んだまま」
        Assert.Equal(GuideNextAction.SubtitlesImported, vm.NextAction);

        var id = vm.Project!.SubtitleSegments[0].Id;
        Assert.True(vm.Editor!.UpdateSubtitle(id, 1, 3, "直した", SubtitlePosition.Bottom, 48).Success);
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction);  // 文章を直したら通常の案内へ
    }

    [Fact]
    public async Task ImportedGuidanceDoesNotSurviveReturningToCuts()
    {
        var (vm, _, _) = await InSubtitlePhaseAsync();
        vm.Editor!.ImportSubtitles([(1.0, 3.0, "取り込み")]);
        vm.NotifySubtitlesImported();
        vm.Editor.DiscardSubtitlesAndReturnToCutEditing();
        Assert.Equal(GuideNextAction.CutEditing, vm.NextAction);
        vm.ConfirmCutsCommand.Execute(null);
        Assert.Equal(GuideNextAction.SubtitleEditing, vm.NextAction); // 字幕が無い（別の内容）ので取り込み直後の案内は出ない
    }

    [Fact]
    public async Task ImageWorkspaceSwitchKeepsDataAndPhase()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full);
        AddRemoveCut(vm, 5, 10);
        var project = vm.Project;
        var path = vm.ProjectPath;
        var dirty = vm.IsDirty;
        var cuts = vm.Project!.CutSegments.Count;

        Assert.True(vm.ShowImageWorkspaceCommand.CanExecute(null));
        Assert.True(vm.ShowWorkspaceSwitch);
        vm.ShowImageWorkspaceCommand.Execute(null);
        Assert.True(vm.IsImageWorkspace);
        Assert.False(vm.IsVideoWorkspace);
        Assert.False(vm.ShowCutTools);
        Assert.Equal(GuideNextAction.ImageCreation, vm.NextAction);
        Assert.Equal(["素材", "画像追加", "キャプション・注釈", "書き出し"], vm.FlowSteps.Select(s => s.Label));
        Assert.Equal(FlowStep.ImageAdd, vm.CurrentFlowStep);
        Assert.True(vm.ShowCapturesEmpty);
        // 作業領域の切り替えでプロジェクトを複製・保存・変更しない。
        Assert.Same(project, vm.Project);
        Assert.Equal(path, vm.ProjectPath);
        Assert.Equal(dirty, vm.IsDirty);
        Assert.Equal(EditPhase.Cut, vm.Project!.EditPhase);
        Assert.Equal(cuts, vm.Project.CutSegments.Count);

        vm.SeekTo(3);
        vm.AddCaptureCommand.Execute(null);
        Assert.False(vm.ShowCapturesEmpty);
        Assert.Equal(FlowStep.CaptionAnnotation, vm.CurrentFlowStep);
        Assert.True(vm.IsImageWorkspace);

        vm.ShowVideoWorkspaceCommand.Execute(null);
        Assert.True(vm.IsVideoWorkspace);
        Assert.True(vm.ShowCutTools);
        Assert.Equal(GuideNextAction.CutEditing, vm.NextAction);
        Assert.Single(vm.Project.CapturePoints);  // 画像は残る
        Assert.Equal(cuts, vm.Project.CutSegments.Count);
        Assert.Equal(0, windows.GeminiCaptionOpened);
    }

    [Fact]
    public async Task ImageWorkspaceIsFullModeOnlyAndKeepsHiddenData()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Simple);
        Assert.False(vm.ShowWorkspaceSwitch);
        Assert.False(vm.ShowImageWorkspaceCommand.CanExecute(null));
        vm.SelectedTabIndex = MainViewModel.TabCaptures;
        Assert.False(vm.IsImageWorkspace);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
        Assert.True(vm.Editor!.AddCapture(10).Result.Success);   // 簡易モードで隠しているデータは消さない
        Assert.Single(vm.Project!.CapturePoints);
        Assert.False(vm.AddCaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task FullModeStartsInVideoWorkspace()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(UiModes.Full);
        Assert.False(vm.IsImageWorkspace);
        Assert.True(vm.IsVideoWorkspace);
        Assert.Equal(MainViewModel.TabCuts, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task CandidateEmptyMessageDistinguishesStates()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.Equal(CandidateEmptyKind.NotAnalyzed, vm.CandidateEmpty);
        Assert.Equal(Text("Empty.CandNotRun"), vm.CandidateEmptyText);
        Assert.True(vm.ShowCandidatesEmpty);

        // 採用済み・却下済みだけ（一覧には出さない）
        vm.Editor!.AddCandidates([new CutCandidate { Start = 5, End = 8, Source = SegmentSource.LocalAnalysis }], "テスト");
        Assert.Equal(CandidateEmptyKind.None, vm.CandidateEmpty);
        var id = vm.Project!.CutCandidates[0].Id;
        vm.Editor.SetCandidateStatus(id, CandidateStatus.Dismissed);
        Assert.Equal(CandidateEmptyKind.OnlyHandled, vm.CandidateEmpty);
        Assert.Equal(Text("Empty.CandHandled"), vm.CandidateEmptyText);
        vm.CandidateStatusFilter = 1; // 表示を［却下］に切り替える
        Assert.Equal(CandidateEmptyKind.None, vm.CandidateEmpty);
    }

    [Fact]
    public async Task ClearMarksOnlyClearsTheRangeNotRegisteredCuts()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        AddRemoveCut(vm, 5, 10);
        vm.SeekTo(20); vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(25); vm.SetMarkOutCommand.Execute(null);
        vm.ClearMarksCommand.Execute(null);
        Assert.Null(vm.MarkIn);
        Assert.Null(vm.MarkOut);
        Assert.Single(vm.Project!.CutSegments);   // 登録済みのカットは消えない
    }

    [Fact]
    public async Task CutChangesAreBlockedInSubtitlePhaseWithTheReviewGuidance()
    {
        var (vm, _, _) = await InSubtitlePhaseAsync();
        Assert.True(vm.ReturnToCutEditingCommand.CanExecute(null));
        vm.SeekTo(5); vm.SetMarkInCommand.Execute(null);
        vm.SeekTo(10); vm.SetMarkOutCommand.Execute(null);
        Assert.False(vm.AddRemoveCutCommand.CanExecute(null));
        vm.DeleteCutsCommand.Execute(new List<CutSegmentItem>());
        Assert.Contains("カットを変更できません", Loc.Get("Status.CutLocked"));
        Assert.Contains("カットを見直す", Loc.Get("Status.CutLocked"));
    }
}

/// <summary>主画面の空の状態（動画未読込）を作る補助。</summary>
internal sealed class MainViewModelPhaseTests_Helper
{
    public MainViewModel NewEmpty()
    {
        var unused = new MainViewModelPhaseTests.Unused();
        var settings = new MainViewModelPhaseTests.FakeSettings();
        settings.Current.UiMode = UiModes.Full;
        var ffmpeg = new ManualVideoStudio.Services.FFmpegStatus(new ManualVideoStudio.Core.FFmpeg.FFmpegLocator(), unused, settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualVideoStudio.Services.FFmpegStatus>.Instance);
        return new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(TestProjects.Create(60)), unused, unused, unused, unused,
            new ManualVideoStudio.Core.Export.ImageExportService(unused), unused, new MainViewModelPhaseTests.FakeDialogs(),
            new MainViewModelPhaseTests.FakeWindows(), unused, ffmpeg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance);
    }
}

/// <summary>選択中の字幕の入力欄（下書き。v1.13）。反映は既存の検証と Undo を通し、未確定の入力を黙って失わない。</summary>
public class SubtitleDraftTests
{
    private static async Task<(MainViewModel Vm, MainViewModelPhaseTests.FakeDialogs Dialogs)> WithTwoSubtitlesAsync()
    {
        var (vm, dialogs, _) = await MainViewModelPhaseTests.OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.Editor!.AddSubtitle(1, 3, "一つ目", SubtitlePosition.Bottom, 48).Result.Success);
        Assert.True(vm.Editor.AddSubtitle(5, 8, "二つ目", SubtitlePosition.Bottom, 48).Result.Success);
        vm.SelectedSubtitleItem = vm.SubtitleItems[0];
        return (vm, dialogs);
    }

    [Fact]
    public async Task SelectionLoadsTheDraftAndNothingIsPending()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        Assert.Equal("一つ目", vm.SubtitleDraftText);
        Assert.Equal("00:00:01.000", vm.SubtitleDraftStart);
        Assert.Equal("00:00:03.000", vm.SubtitleDraftEnd);
        Assert.False(vm.HasSubtitleDraft);
        Assert.True(vm.HasSelectedSubtitle);
    }

    [Fact]
    public async Task ApplyUsesValidationAndOneUndo()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "直した文章";
        vm.SubtitleDraftEnd = "0:04.5";
        Assert.True(vm.HasSubtitleDraft);
        vm.ApplySubtitleDraftCommand.Execute(null);
        var s = vm.Project!.SubtitleSegments[0];
        Assert.Equal("直した文章", s.Text);
        Assert.Equal(4.5, s.End);
        Assert.False(vm.HasSubtitleDraft);
        Assert.Equal("00:00:04.500", vm.SubtitleDraftEnd);   // 正規の表記に整う
        vm.UndoCommand.Execute(null);                          // 1 回で戻る
        Assert.Equal("一つ目", vm.Project.SubtitleSegments[0].Text);
        Assert.Equal(3, vm.Project.SubtitleSegments[0].End);
        Assert.Equal("一つ目", vm.SubtitleDraftText);
        vm.RedoCommand.Execute(null);
        Assert.Equal("直した文章", vm.Project.SubtitleSegments[0].Text);
    }

    [Fact]
    public async Task InvalidInputIsNotAppliedAndShowsTheReason()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftStart = "abc";
        vm.ApplySubtitleDraftCommand.Execute(null);
        Assert.Equal(1, vm.Project!.SubtitleSegments[0].Start);
        Assert.Contains("開始位置", vm.SubtitleDraftError);
        Assert.True(vm.HasSubtitleDraft);   // 入力は残る

        vm.SubtitleDraftStart = "00:00:01.000";
        vm.SubtitleDraftText = "";           // 空の文章は検証で拒否される
        vm.ApplySubtitleDraftCommand.Execute(null);
        Assert.Equal("一つ目", vm.Project.SubtitleSegments[0].Text);
        Assert.NotEmpty(vm.SubtitleDraftError);

        vm.SubtitleDraftText = "二つ目と重なる";
        vm.SubtitleDraftEnd = "00:00:06.000";  // 次の字幕（5～8 秒）と重なる
        vm.ApplySubtitleDraftCommand.Execute(null);
        Assert.Equal(3, vm.Project.SubtitleSegments[0].End);
        Assert.NotEmpty(vm.SubtitleDraftError);
    }

    [Fact]
    public async Task RevertDropsTheDraft()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "捨てる";
        vm.RevertSubtitleDraftCommand.Execute(null);
        Assert.Equal("一つ目", vm.SubtitleDraftText);
        Assert.False(vm.HasSubtitleDraft);
        Assert.Equal("", vm.SubtitleDraftError);
    }

    [Fact]
    public async Task ChangingSelectionAppliesAValidDraftFirst()
    {
        var (vm, dialogs) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "選択前に確定";
        vm.SelectedSubtitleItem = vm.SubtitleItems[1];
        Assert.Equal("選択前に確定", vm.Project!.SubtitleSegments[0].Text);   // 黙って失わない
        Assert.Equal("二つ目", vm.SubtitleDraftText);
        Assert.Empty(dialogs.Warnings);
    }

    [Fact]
    public async Task ChangingSelectionWithInvalidDraftAsksAndCanStay()
    {
        var (vm, dialogs) = await WithTwoSubtitlesAsync();
        var first = vm.SubtitleItems[0];
        vm.SubtitleDraftStart = "abc";
        dialogs.ConfirmResult = false;   // 留まる（キャンセル）
        vm.SelectedSubtitleItem = vm.SubtitleItems[1];
        await Task.Delay(100);   // 一覧の選択を戻す処理は、UI の文脈では少し遅れて行う
        Assert.Contains("入力を反映できません", dialogs.LastConfirm);
        Assert.Same(first, vm.SelectedSubtitleItem);        // 元の字幕に戻る
        Assert.Equal("abc", vm.SubtitleDraftStart);          // 入力は残る
        Assert.True(vm.HasSubtitleDraft);
        Assert.Equal(1, vm.Project!.SubtitleSegments[0].Start);
    }

    [Fact]
    public async Task ChangingSelectionWithInvalidDraftCanDiscardWhenConfirmed()
    {
        var (vm, dialogs) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftStart = "abc";
        dialogs.ConfirmResult = true;    // 破棄して移る（OK）
        vm.SelectedSubtitleItem = vm.SubtitleItems[1];
        Assert.Equal(vm.SubtitleItems[1].Id, vm.SelectedSubtitleItem!.Id);
        Assert.Equal("二つ目", vm.SubtitleDraftText);
        Assert.False(vm.HasSubtitleDraft);
        Assert.Equal(1, vm.Project!.SubtitleSegments[0].Start);
    }

    [Fact]
    public async Task SaveAppliesThePendingDraft()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "保存前に確定";
        Assert.True(vm.SaveProjectCommand.CanExecute(null));
        vm.SaveProjectCommand.Execute(null);
        Assert.Equal("保存前に確定", vm.Project!.SubtitleSegments[0].Text);
        Assert.False(vm.HasSubtitleDraft);
    }

    [Fact]
    public async Task ClosingWithInvalidDraftAsksBeforeDiscarding()
    {
        var (vm, dialogs) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftEnd = "xx";
        dialogs.ConfirmResult = false;
        Assert.False(vm.ConfirmDiscardChanges());           // 破棄しない → 閉じない
        dialogs.ConfirmResult = true;                       // 破棄してよい → 続行（未保存の変更の確認へ進む）
        Assert.True(vm.ConfirmDiscardChanges());
    }

    [Fact]
    public async Task DraftIsKeptWhenPlaybackWouldChangeTheShownSubtitle()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "入力中";
        vm.SeekTo(6);   // 二つ目の字幕が表示される位置
        Assert.Equal("入力中", vm.SubtitleDraftText);          // 入力中は選択を移さない
        Assert.Equal(vm.SubtitleItems[0].Id, vm.SelectedSubtitleItem!.Id);
    }

    [Fact]
    public async Task LayoutControlsStillApplyImmediately()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SetSubtitlePositionCommand.Execute("Top");
        Assert.Equal(SubtitlePosition.Top, vm.Project!.SubtitleSegments[0].Position);
        vm.NudgeSubtitleCommand.Execute("up");
        Assert.True(vm.Project.SubtitleSegments[0].VerticalOffset > 0);
        vm.SelectedSubtitleFontSize = 64;
        Assert.Equal(64, vm.Project.SubtitleSegments[0].FontSize);
        Assert.Equal("一つ目", vm.SubtitleDraftText);   // 位置の変更で文章の下書きは影響されない
    }

    [Fact]
    public async Task LayoutControlsApplyToAllSelectedSubtitlesInOneUndo()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SetSelectedSubtitles(vm.SubtitleItems.Select(i => i.Id));   // Ctrl+A 相当
        Assert.Contains("2 件", vm.SelectedSubtitleText);
        vm.SetSubtitlePositionCommand.Execute("Top");
        Assert.All(vm.Project!.SubtitleSegments, s => Assert.Equal(SubtitlePosition.Top, s.Position));
        vm.NudgeSubtitleCommand.Execute("up");
        Assert.All(vm.Project.SubtitleSegments, s => Assert.Equal(SubtitleStyle.OffsetStep, s.VerticalOffset));
        vm.SelectedSubtitleFontSize = 64;
        Assert.All(vm.Project.SubtitleSegments, s => Assert.Equal(64, s.FontSize));
        vm.UndoCommand.Execute(null);   // 文字サイズだけが全件戻る
        Assert.All(vm.Project.SubtitleSegments, s => Assert.Equal(48, s.FontSize));
        Assert.All(vm.Project.SubtitleSegments, s => Assert.Equal(SubtitlePosition.Top, s.Position));
    }

    [Fact]
    public async Task SingleSelectionStillChangesOnlyThatSubtitle()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SetSelectedSubtitles([vm.SubtitleItems[0].Id]);
        vm.SetSubtitlePositionCommand.Execute("Top");
        Assert.Equal(SubtitlePosition.Top, vm.Project!.SubtitleSegments[0].Position);
        Assert.Equal(SubtitlePosition.Bottom, vm.Project.SubtitleSegments[1].Position);
        Assert.StartsWith("選択中: No.1", vm.SelectedSubtitleText);
    }

    [Fact]
    public async Task ShrinkingTheSelectionBackToOneStopsTheBulkChange()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SetSelectedSubtitles(vm.SubtitleItems.Select(i => i.Id));
        vm.SetSelectedSubtitles([vm.SubtitleItems[0].Id]);
        vm.SetSubtitlePositionCommand.Execute("Middle");
        Assert.Equal(SubtitlePosition.Middle, vm.Project!.SubtitleSegments[0].Position);
        Assert.Equal(SubtitlePosition.Bottom, vm.Project.SubtitleSegments[1].Position);
    }

    [Fact]
    public async Task ClearingTheSelectionResetsTheDraft()
    {
        var (vm, _) = await WithTwoSubtitlesAsync();
        vm.SubtitleDraftText = "別のプロジェクトへ持ち越さない";
        vm.SelectedSubtitleItem = null;
        Assert.Equal("", vm.SubtitleDraftText);
        Assert.False(vm.HasSelectedSubtitle);
        Assert.False(vm.HasSubtitleDraft);
    }
}
