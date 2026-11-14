using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>
/// 1.21.0 の主画面（波 2-a）: 再生速度（J / K / L）、R の片方だけの指定、Q / W、候補のキー操作、M のマーカー、
/// 字幕編集中の追加カット、［字幕に合わせる］、簡易解析への入力なしの範囲の受け渡し、候補の「入力なし」の絞り込み。
/// </summary>
public class Release121MainKeysTests
{
    /// <summary>MediaElement の代わり。再生中かどうかと速度だけを持つ。</summary>
    internal sealed class FakePlayer : IVideoPlayer
    {
        public bool IsOpened { get; set; } = true;
        public bool IsPlaying { get; private set; }
        public double Position { get; set; }
        public double SpeedRatio { get; set; } = 1;
        public double SpeedAtLastPlay { get; private set; }
        public void Open(string path) { }
        public void Close() { IsPlaying = false; }
        public void Play() { IsPlaying = true; SpeedAtLastPlay = SpeedRatio; }
        public void Pause() => IsPlaying = false;
        public event EventHandler? Opened { add { } remove { } }
        public event EventHandler<string>? Failed { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler<double>? PositionChanged { add { } remove { } }
    }

    private static async Task<(MainViewModel Vm, FakePlayer Player, MainViewModelPhaseTests.FakeDialogs Dialogs, MainViewModelPhaseTests.FakeWindows Windows)> OpenAsync()
    {
        var (vm, dialogs, windows) = await MainViewModelPhaseTests.OpenAsync();
        var player = new FakePlayer();
        vm.AttachPlayer(player);
        return (vm, player, dialogs, windows);
    }

    private static void Mark(MainViewModel vm, double? markIn, double? markOut)
    {
        vm.MarkIn = markIn;
        vm.MarkOut = markOut;
    }

    // ---------------- 再生速度 ----------------

    [Fact]
    public async Task RateButtonCyclesForwardRates()
    {
        var (vm, player, _, _) = await OpenAsync();
        Assert.Equal("×1", vm.PlaybackRateText);
        vm.CyclePlaybackRateCommand.Execute(null);
        Assert.Equal("×2", vm.PlaybackRateText);
        Assert.Equal(2, player.SpeedRatio);
        vm.CyclePlaybackRateCommand.Execute(null);
        Assert.Equal("×4", vm.PlaybackRateText);
        vm.CyclePlaybackRateCommand.Execute(null);
        Assert.Equal("×1", vm.PlaybackRateText);
        Assert.Equal("◀×2", MainViewModel.FormatRate(-2));
    }

    [Fact]
    public async Task LSpeedsUpKStopsAndSpaceAlwaysPlaysAtOne()
    {
        var (vm, player, _, _) = await OpenAsync();
        vm.PlayForwardCommand.Execute(null);
        Assert.True(player.IsPlaying);
        Assert.Equal(1, player.SpeedAtLastPlay);
        vm.PlayForwardCommand.Execute(null);
        Assert.Equal(2, vm.PlaybackRate);
        vm.PlayForwardCommand.Execute(null);
        vm.PlayForwardCommand.Execute(null);
        Assert.Equal(4, vm.PlaybackRate); // ×4 のまま
        Assert.Equal(4, player.SpeedRatio);

        vm.StopPlaybackCommand.Execute(null);
        Assert.False(player.IsPlaying);
        Assert.Equal(1, vm.PlaybackRate);

        vm.CyclePlaybackRateCommand.Execute(null); // ×2 を選んでから Space
        vm.PlayPauseCommand.Execute(null);
        Assert.True(player.IsPlaying);
        Assert.Equal(1, player.SpeedAtLastPlay);
        Assert.Equal("×1", vm.PlaybackRateText);
    }

    [Fact]
    public async Task JStepsBackwardsAndStopsAtStartOrOnSeek()
    {
        var (vm, _, _, _) = await OpenAsync();
        vm.SeekTo(10);
        vm.PlayReverseCommand.Execute(null);
        Assert.True(vm.IsReversePlaying);
        Assert.Equal("◀×1", vm.PlaybackRateText);
        vm.ReverseStep();
        Assert.Equal(9.9, vm.CurrentTime, 3);
        vm.PlayReverseCommand.Execute(null);
        Assert.Equal(-2, vm.PlaybackRate);
        vm.ReverseStep();
        Assert.Equal(9.7, vm.CurrentTime, 3);

        vm.SeekTo(20); // 自分で移動したら止まる
        Assert.False(vm.IsReversePlaying);
        Assert.Equal(1, vm.PlaybackRate);

        vm.SeekTo(0.05);
        vm.PlayReverseCommand.Execute(null);
        vm.ReverseStep();
        Assert.Equal(0, vm.CurrentTime, 3);
        Assert.False(vm.IsReversePlaying); // 先頭で止まる
    }

    [Fact]
    public async Task ReverseSkipsRemovedPartUsingPrevKeepEnd()
    {
        var (vm, _, _, _) = await OpenAsync();
        Assert.True(vm.Editor!.AddCut(5, 10, SegmentAction.Remove, 0).Result.Success);
        vm.ApplyCutsInPreview = true;
        vm.SeekTo(10.05);
        vm.PlayReverseCommand.Execute(null);
        vm.ReverseStep(); // 9.95 は削除部分 → 手前の残る区間の終端（5）の少し前へ
        Assert.True(vm.CurrentTime < 5 && vm.CurrentTime > 4.9, vm.CurrentTime.ToString());
        Assert.True(vm.IsReversePlaying);

        // カットを反映しないで再生するときは、削除部分もそのまま戻る。
        vm.ApplyCutsInPreview = false;
        vm.SeekTo(10.05);
        vm.PlayReverseCommand.Execute(null);
        vm.ReverseStep();
        Assert.Equal(9.95, vm.CurrentTime, 3);
    }

    [Fact]
    public async Task TrialPreviewResetsRate()
    {
        var (vm, player, _, _) = await OpenAsync();
        vm.Editor!.ReplaceCandidates(SegmentSource.LocalAnalysis,
            [new CutCandidate { Start = 20, End = 25, Kind = CandidateKind.Freeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove }], "test");
        vm.CyclePlaybackRateCommand.Execute(null);
        vm.CyclePlaybackRateCommand.Execute(null);
        Assert.Equal(4, vm.PlaybackRate);
        vm.SetSelectedCandidates([vm.CandidateItems[0].Id]);
        vm.TrialSelectedCandidateCommand.Execute(null);
        Assert.True(vm.HasPreviewBanner);
        Assert.Equal(1, vm.PlaybackRate);
        Assert.Equal(1, player.SpeedAtLastPlay);
    }

    // ---------------- R ----------------

    [Fact]
    public async Task RWithOnlyOneMarkUsesPlayheadInCutPhase()
    {
        var (vm, _, _, _) = await OpenAsync();
        Mark(vm, 5, null);
        vm.SeekTo(8);
        vm.QuickCutCommand.Execute(null);
        var cut = Assert.Single(vm.Project!.CutSegments);
        Assert.Equal((5.0, 8.0), (cut.Start, cut.End));
        Assert.Null(vm.MarkIn);

        Mark(vm, null, 30); // 終了だけ → 再生位置（20）から
        vm.SeekTo(20);
        vm.QuickCutCommand.Execute(null);
        Assert.Contains(vm.Project.CutSegments, c => c.Start == 20 && c.End == 30);

        vm.QuickCutCommand.Execute(null); // 指定なし
        Assert.Equal(2, vm.Project.CutSegments.Count);
        Assert.Contains("開始（I）か終了（O）", vm.StatusMessage);
    }

    [Fact]
    public async Task RInSubtitlePhaseConfirmsThenShiftsSubtitles()
    {
        var (vm, _, dialogs, windows) = await OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        Assert.True(vm.Editor!.AddSubtitle(20, 23, "後ろの字幕").Result.Success);
        Assert.True(vm.Editor.AddSubtitle(11, 12, "消える字幕").Result.Success);

        Assert.False(vm.CutInSubtitlePhaseCommand.CanExecute(null));
        Mark(vm, 10, null);
        Assert.True(vm.CutInSubtitlePhaseCommand.CanExecute(null));
        vm.SeekTo(15);

        windows.DangerResult = false; // キャンセル → 変わらない
        vm.QuickCutCommand.Execute(null);
        Assert.Empty(vm.Project!.CutSegments);
        var message = Assert.Single(windows.DangerMessages);
        Assert.Contains("Gemini の字幕依頼用動画は作り直しが必要です", message);
        Assert.Contains("前へずれる: 字幕 1 件", message);
        Assert.Contains("削除される: 字幕 1 件", message);
        Assert.Contains("消える字幕", message);
        Assert.Contains("|カットする|", message);

        windows.DangerResult = true;
        vm.CutInSubtitlePhaseCommand.Execute(null);
        var cut = Assert.Single(vm.Project.CutSegments);
        Assert.Equal((10.0, 15.0), (cut.Start, cut.End));
        var sub = Assert.Single(vm.Project.SubtitleSegments);
        Assert.Equal(15, sub.Start, 3);
        Assert.Null(vm.MarkIn);
        Assert.Contains("短くしました", vm.StatusMessage);

        vm.UndoCommand.Execute(null); // 1 回でカットも字幕も戻る
        Assert.Empty(vm.Project.CutSegments);
        Assert.Equal(2, vm.Project.SubtitleSegments.Count);
        Assert.Empty(dialogs.Warnings);
    }

    [Fact]
    public async Task SubtitlePhaseCutOfAlreadyCutRangeWarns()
    {
        var (vm, _, dialogs, windows) = await OpenAsync();
        Assert.True(vm.Editor!.AddCut(10, 20, SegmentAction.Remove, 0).Result.Success);
        vm.ConfirmCutsCommand.Execute(null);
        Mark(vm, 12, 18);
        vm.QuickCutCommand.Execute(null);
        Assert.Empty(windows.DangerMessages);
        Assert.Contains(dialogs.Warnings, w => w.Contains(SubtitlePhaseCut.AlreadyCutMessage));
    }

    // ---------------- Q / W ----------------

    [Fact]
    public async Task QAndWMoveCutEdgesToPlayhead()
    {
        var (vm, _, _, _) = await OpenAsync();
        Assert.True(vm.Editor!.AddCut(10, 20, SegmentAction.Remove, 0).Result.Success);
        vm.SeekTo(12);
        vm.AlignCutStartCommand.Execute(null); // 再生位置を含むカット
        Assert.Equal((12.0, 20.0), (vm.Project!.CutSegments[0].Start, vm.Project.CutSegments[0].End));
        vm.SeekTo(25);
        vm.AlignCutEndCommand.Execute(null); // 手前で終わる最も近いカット
        Assert.Equal((12.0, 25.0), (vm.Project.CutSegments[0].Start, vm.Project.CutSegments[0].End));

        vm.SeekTo(40);
        vm.AlignCutStartCommand.Execute(null); // 後ろで始まるカットが無い
        Assert.Contains("カットの端を合わせられません", vm.StatusMessage);
        Assert.Equal(25, vm.Project.CutSegments[0].End);
    }

    [Fact]
    public async Task QIsRefusedInSubtitlePhase()
    {
        var (vm, _, _, _) = await OpenAsync();
        Assert.True(vm.Editor!.AddCut(10, 20, SegmentAction.Remove, 0).Result.Success);
        vm.ConfirmCutsCommand.Execute(null);
        vm.SeekTo(12);
        vm.AlignCutStartCommand.Execute(null);
        Assert.Equal(10, vm.Project!.CutSegments[0].Start);
        Assert.Contains("カットを変更できません", vm.StatusMessage);
    }

    // ---------------- 候補のキー ----------------

    private static void AddCandidates(MainViewModel vm, params (double S, double E)[] ranges)
        => vm.Editor!.ReplaceCandidates(SegmentSource.LocalAnalysis,
            ranges.Select(r => new CutCandidate { Start = r.S, End = r.E, Kind = CandidateKind.Freeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove }).ToList(), "test");

    [Fact]
    public async Task CandidateKeysAcceptDismissAndStep()
    {
        var (vm, _, _, _) = await OpenAsync();
        AddCandidates(vm, (10, 15), (20, 25), (30, 35));

        vm.AcceptSelectedCandidatesCommand.Execute(null); // 選択なし
        Assert.Contains("候補を選んでください", vm.StatusMessage);

        vm.SeekTo(0);
        vm.SelectNextCandidateCommand.Execute(null); // N → 10～15、2 秒前へ
        Assert.Equal(8, vm.CurrentTime, 3);
        Assert.Equal(vm.CandidateItems[0].Id, Assert.Single(vm.SelectedCandidateIds));

        vm.AcceptSelectedCandidatesCommand.Execute(null); // A → カット、次（20～25）へ
        Assert.Single(vm.Project!.CutSegments);
        var second = vm.CandidateItems.First(i => i.Model.Start == 20);
        Assert.Equal(second.Id, Assert.Single(vm.SelectedCandidateIds));

        vm.DismissSelectedCandidatesCommand.Execute(null); // D → 却下、次（30～35）へ
        Assert.Equal(CandidateStatus.Dismissed, vm.Project.CutCandidates.First(c => c.Start == 20).Status);
        var third = vm.CandidateItems.First(i => i.Model.Start == 30);
        Assert.Equal(third.Id, Assert.Single(vm.SelectedCandidateIds));

        vm.SelectPrevCandidateCommand.Execute(null); // Shift+N: 前の未判断は無い（10 は採用、20 は却下）
        Assert.Contains("前に未判断の候補はありません", vm.StatusMessage);
        vm.SelectNextCandidateCommand.Execute(null);
        Assert.Contains("後に未判断の候補はありません", vm.StatusMessage);
    }

    [Fact]
    public async Task AutoTrialRunsAfterMovingToNextCandidate()
    {
        var (vm, _, _, _) = await OpenAsync();
        AddCandidates(vm, (10, 15), (20, 25));
        Assert.False(vm.AutoTrialNextCandidate);
        vm.AutoTrialNextCandidate = true;
        Assert.True(vm.Settings.Ui.AutoTrialNextCandidate);
        vm.SetSelectedCandidates([vm.CandidateItems[0].Id]);
        vm.DismissSelectedCandidatesCommand.Execute(null);
        Assert.True(vm.HasPreviewBanner); // 次（20～25）を試し再生している
        Assert.Contains("00:20", vm.PreviewBannerText);
    }

    [Fact]
    public async Task CandidateKeysAreRefusedInSubtitlePhase()
    {
        var (vm, _, _, _) = await OpenAsync();
        AddCandidates(vm, (10, 15));
        vm.ConfirmCutsCommand.Execute(null);
        vm.SetSelectedCandidates([vm.Project!.CutCandidates[0].Id]);
        vm.DismissSelectedCandidatesCommand.Execute(null);
        Assert.Equal(CandidateStatus.Pending, vm.Project.CutCandidates[0].Status);
        Assert.Contains("カットを変更できません", vm.StatusMessage);
    }

    // ---------------- M ----------------

    [Fact]
    public async Task MMovesBetweenMarkers()
    {
        var (vm, _, _, _) = await OpenAsync();
        vm.SeekNextMarkerCommand.Execute(null);
        Assert.Contains("操作の記録がありません", vm.StatusMessage);

        vm.RecordingEvents = new RecordingEvents();
        vm.SeekNextMarkerCommand.Execute(null);
        Assert.Contains("マーカーがありません", vm.StatusMessage);

        vm.RecordingEvents = new RecordingEvents { Markers = [30, 10] };
        vm.SeekTo(0);
        vm.SeekNextMarkerCommand.Execute(null);
        Assert.Equal(10, vm.CurrentTime, 3);
        Assert.Contains("マーカー 1 / 2", vm.StatusMessage);
        vm.SeekNextMarkerCommand.Execute(null);
        Assert.Equal(30, vm.CurrentTime, 3);
        vm.SeekNextMarkerCommand.Execute(null);
        Assert.Contains("後にマーカーはありません", vm.StatusMessage);
        vm.SeekPrevMarkerCommand.Execute(null);
        Assert.Equal(10, vm.CurrentTime, 3);
        vm.SeekPrevMarkerCommand.Execute(null);
        Assert.Contains("前にマーカーはありません", vm.StatusMessage);
    }

    // ---------------- ［字幕に合わせる］ ----------------

    [Fact]
    public async Task AlignButtonsFillShapeDraftOnly()
    {
        var (vm, _, _, _) = await OpenAsync();
        vm.ConfirmCutsCommand.Execute(null);
        var (r, g) = vm.Editor!.AddShapeGroup(30, 35, [new RectangleAnnotation { Width = 10, Height = 10 }], 1920, 1080);
        Assert.True(r.Success);
        vm.SetSelectedShapes([g!.Id]);
        Assert.True(vm.HasSelectedShape);
        Assert.False(vm.ShapeAlignRangeCommand.CanExecute(null)); // 字幕が無い

        Assert.True(vm.Editor.AddSubtitle(10, 14, "設定を開きます").Result.Success);
        Assert.True(vm.ShapeAlignRangeCommand.CanExecute(null));
        vm.SeekTo(50); // 再生位置に字幕は無い → 開始がいちばん近い字幕
        vm.ShapeAlignRangeCommand.Execute(null);
        Assert.Equal(TimeFormat.Format(10), vm.ShapeDraftStart);
        Assert.Equal(TimeFormat.Format(14), vm.ShapeDraftEnd);
        Assert.Contains("設定を開きます", vm.ShapeAlignInfo);
        Assert.Equal(30, vm.Project!.ShapeGroups[0].Start); // 反映はまだ
        Assert.True(vm.HasShapeDraft);

        vm.RevertShapeDraftCommand.Execute(null);
        Assert.Equal("", vm.ShapeAlignInfo);
        vm.ShapeAlignStartCommand.Execute(null); // 開始が終了（35）より前なので終了はそのまま
        Assert.Equal(TimeFormat.Format(10), vm.ShapeDraftStart);
        Assert.Equal(TimeFormat.Format(35), vm.ShapeDraftEnd);

        vm.ApplyShapeDraftCommand.Execute(null);
        Assert.Equal(10, vm.Project.ShapeGroups[0].Start);
    }

    // ---------------- 簡易解析と絞り込み ----------------

    private sealed class CapturingAnalysis : ILocalAnalysisService
    {
        public IReadOnlyList<TimeRange>? Idle { get; private set; }
        public bool Called { get; private set; }
        public Task<AnalysisResult> AnalyzeAsync(string f, string v, VideoMetadata m, AnalysisSettings s, IProgress<double>? p, CancellationToken ct, IReadOnlyList<TimeRange>? inputIdle = null)
        {
            Called = true;
            Idle = inputIdle;
            IReadOnlyList<CutCandidate> candidates =
            [
                new CutCandidate { Start = 10, End = 15, Kind = CandidateKind.IdleAndFreeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
                new CutCandidate { Start = 20, End = 30, Kind = CandidateKind.Idle, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
                new CutCandidate { Start = 40, End = 45, Kind = CandidateKind.Freeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
            ];
            return Task.FromResult(new AnalysisResult(candidates, [], [], [], TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>進捗画面の代わりに、その場で処理を実行する画面（そのほかは FakeWindows に任せる）。</summary>
    private sealed class RunningWindows(MainViewModelPhaseTests.FakeWindows inner) : IWindowService
    {
        public Task<SettingsDialogResult> ShowSettingsAsync(SettingsTab tab = SettingsTab.FFmpeg, SubtitleSampleContext? sample = null) => inner.ShowSettingsAsync(tab, sample);
        public void ShowAiAssist() => inner.ShowAiAssist();
        public void CloseAiAssist() => inner.CloseAiAssist();
        public double? ChooseFrame(FrameCandidatesViewModel vm) => inner.ChooseFrame(vm);
        public bool EditCut(CutEditViewModel vm) => inner.EditCut(vm);
        public void EditImage(ImageEditorViewModel vm) => inner.EditImage(vm);
        public string? PromptText(string title, string message, string initial, bool singleLine = false) => inner.PromptText(title, message, initial, singleLine);
        public async Task<bool> RunWithProgressAsync(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
        {
            await work(new Progress<ProgressInfo>(), CancellationToken.None);
            return true;
        }
        public bool EditSubtitle(SubtitleEditViewModel vm) => inner.EditSubtitle(vm);
        public bool ChooseGifQuality(GifExportViewModel vm) => inner.ChooseGifQuality(vm);
        public bool EditTitle(TitleEditViewModel vm) => inner.EditTitle(vm);
        public void ShowAbout(AboutViewModel vm) => inner.ShowAbout(vm);
        public bool ConfirmDanger(string title, string message, string okLabel, string cancelLabel) => inner.ConfirmDanger(title, message, okLabel, cancelLabel);
        public Task<RecordingOutcome?> ShowRecorderAsync(AppendRecordingContext? append = null) => inner.ShowRecorderAsync(append);
        public bool IsRecorderOpen => inner.IsRecorderOpen;
        public void ActivateRecorder() => inner.ActivateRecorder();
        public ProjectListAction? ShowProjectList(string? openProjectPath, string? openVideoPath) => inner.ShowProjectList(openProjectPath, openVideoPath);
        public void ShowTejunshoExportDone(TejunshoExportDone result) => inner.ShowTejunshoExportDone(result);
        public void ShowGeminiCaption(GeminiCaptionViewModel vm) => inner.ShowGeminiCaption(vm);
        public bool? ConfirmDeleteRequestVideo(string message, string path) => inner.ConfirmDeleteRequestVideo(message, path);
    }

    private static async Task<(MainViewModel Vm, CapturingAnalysis Analysis)> OpenForAnalysisAsync()
    {
        var settings = new MainViewModelPhaseTests.FakeSettings();
        var unused = new MainViewModelPhaseTests.Unused();
        var analysis = new CapturingAnalysis();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance)
        {
            Location = new FFmpegLocation(new ToolLocation(@"C:\ffmpeg\ffmpeg.exe", ToolSource.UserSetting, ""), new ToolLocation(@"C:\ffmpeg\ffprobe.exe", ToolSource.UserSetting, "")),
            IsAvailable = true,
        };
        var vm = new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(TestProjects.Create(60)), unused, unused, unused, unused,
            new ImageExportService(unused), analysis, new MainViewModelPhaseTests.FakeDialogs(), new RunningWindows(new MainViewModelPhaseTests.FakeWindows()),
            unused, ffmpeg, NullLogger<MainViewModel>.Instance);
        await vm.OpenProjectFileAsync(Path.Combine(Path.GetTempPath(), "keys-test.mvsproj.json"));
        return (vm, analysis);
    }

    [Fact]
    public async Task AnalysisPassesInputIdleRangesAndReportsIdleCounts()
    {
        var (vm, analysis) = await OpenForAnalysisAsync();
        await vm.RunAnalysisCommand.ExecuteAsync(null);
        Assert.True(analysis.Called);
        Assert.NotNull(analysis.Idle);
        Assert.Empty(analysis.Idle!); // 記録が無ければ空（従来と同じ結果）

        // 0～60 秒のうち 10.25～49.75 秒にクリックが無い（キー・移動も無い）記録。
        vm.RecordingEvents = new RecordingEvents { Clicks = [new RecordedClick(10, 1, 1, RecordedClick.Left), new RecordedClick(50, 1, 1, RecordedClick.Left)] };
        await vm.RunAnalysisCommand.ExecuteAsync(null);
        var expected = vm.InputIdleRanges(vm.Settings.Analysis.FreezeMinSeconds);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, analysis.Idle);
        Assert.Contains("入力の無い候補 2 件", vm.StatusMessage);
    }

    [Fact]
    public async Task KindFilterIdleShowsIdleCandidatesOnly()
    {
        var (vm, _, _, _) = await OpenAsync();
        Assert.Equal("入力なし", MainViewModel.CandidateKindFilters[5]);
        vm.Editor!.ReplaceCandidates(SegmentSource.LocalAnalysis,
        [
            new CutCandidate { Start = 10, End = 15, Kind = CandidateKind.IdleAndFreeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
            new CutCandidate { Start = 20, End = 30, Kind = CandidateKind.Idle, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
            new CutCandidate { Start = 40, End = 45, Kind = CandidateKind.Freeze, Source = SegmentSource.LocalAnalysis, SuggestedAction = SegmentAction.Remove },
        ], "test");
        Assert.Equal(3, vm.CandidateItems.Count);
        vm.CandidateKindFilter = 5;
        Assert.Equal(2, vm.CandidateItems.Count);
        Assert.All(vm.CandidateItems, i => Assert.True(i.Model.Kind is CandidateKind.Idle or CandidateKind.IdleAndFreeze));
        Assert.True(vm.IsCandidateFiltered);
    }
}
