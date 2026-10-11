using System.Collections;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>手動カット・カット候補・ローカル自動解析。</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkInText), nameof(MarkRangeText), nameof(SubtitleMarkInText), nameof(HasMarkIn), nameof(MarkInTip))]
    [NotifyCanExecuteChangedFor(nameof(AddRemoveCutCommand), nameof(ClearMarksCommand), nameof(ClearMarkInCommand), nameof(ClearMarkOutCommand))]
    private double? _markIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkOutText), nameof(MarkRangeText), nameof(SubtitleMarkOutText), nameof(HasMarkOut), nameof(MarkOutTip))]
    [NotifyCanExecuteChangedFor(nameof(AddRemoveCutCommand), nameof(ClearMarksCommand), nameof(ClearMarkInCommand), nameof(ClearMarkOutCommand))]
    private double? _markOut;

    /// <summary>開始・終了を指定中か（ボタンの強調表示に使う。時刻の文字は出さず、ツールチップに出す）。</summary>
    public bool HasMarkIn => MarkIn is not null;
    public bool HasMarkOut => MarkOut is not null;

    public string MarkInTip => Loc.Get(IsSubtitlePhase ? "Sub.Start.Tip" : "Cut.Start.Tip") + (MarkIn is null ? "" : $"（指定中: {(IsSubtitlePhase ? SubtitleMarkInText : MarkInText)}）");
    public string MarkOutTip => Loc.Get(IsSubtitlePhase ? "Sub.End.Tip" : "Cut.End.Tip") + (MarkOut is null ? "" : $"（指定中: {(IsSubtitlePhase ? SubtitleMarkOutText : MarkOutText)}）");

    public string MarkInText => MarkIn is { } t ? TimeFormat.FormatShort(t) : "--:--.---";
    public string MarkOutText => MarkOut is { } t ? TimeFormat.FormatShort(t) : "--:--.---";
    public string MarkRangeText => MarkIn is { } a && MarkOut is { } b && b > a
        ? IsSubtitlePhase
            ? $"{EditTimelineCalculator.MapToOutputClamped(b, KeepRanges) - EditTimelineCalculator.MapToOutputClamped(a, KeepRanges):0.000} 秒"
            : $"{b - a:0.000} 秒"
        : "";
    /// <summary>字幕編集中の開始・終了位置（編集後の時刻）。</summary>
    public string SubtitleMarkInText => MarkIn is { } t ? TimeFormat.FormatShort(EditTimelineCalculator.MapToOutputClamped(t, KeepRanges)) : "--:--.---";
    public string SubtitleMarkOutText => MarkOut is { } t ? TimeFormat.FormatShort(EditTimelineCalculator.MapToOutputClamped(t, KeepRanges)) : "--:--.---";

    private bool CanMark() => HasProject;
    private bool CanAddCut() => HasProject && !IsCutLocked && MarkIn is not null && MarkOut is not null;

    [RelayCommand(CanExecute = nameof(CanMark))]
    private void SetMarkIn()
    {
        MarkIn = CurrentTime;
        if (MarkOut is { } o && o <= CurrentTime) MarkOut = null;
        StatusMessage = IsSubtitlePhase
            ? $"字幕の開始位置: 編集後 {TimeFormat.Format(CurrentOutputTime)}"
            : $"カット開始位置: {TimeFormat.Format(CurrentTime)}";
    }

    [RelayCommand(CanExecute = nameof(CanMark))]
    private void SetMarkOut()
    {
        MarkOut = CurrentTime;
        if (MarkIn is { } i && i >= CurrentTime) MarkIn = null;
        StatusMessage = IsSubtitlePhase
            ? $"字幕の終了位置: 編集後 {TimeFormat.Format(CurrentOutputTime)}"
            : $"カット終了位置: {TimeFormat.Format(CurrentTime)}";
    }

    private bool HasMarks() => MarkIn is not null || MarkOut is not null;

    /// <summary>
    /// 登録前の開始・終了の指定を取り消す（開始だけの指定でも取り消せる）。登録済みのカット（右の［カット区間］）は取り消さない。
    /// Esc（入力欄にフォーカスが無いとき）も同じ。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMarks))]
    private void ClearMarks()
    {
        bool both = MarkIn is not null && MarkOut is not null;
        MarkIn = null;
        MarkOut = null;
        StatusMessage = Loc.Get(both ? "Cut.Status.ClearedBoth" : "Cut.Status.ClearedOne");
    }

    [RelayCommand(CanExecute = nameof(HasMarkIn))]
    private void ClearMarkIn()
    {
        if (MarkIn is null) return;
        MarkIn = null;
        StatusMessage = Loc.Get("Cut.Status.ClearedIn");
    }

    [RelayCommand(CanExecute = nameof(HasMarkOut))]
    private void ClearMarkOut()
    {
        if (MarkOut is null) return;
        MarkOut = null;
        StatusMessage = Loc.Get("Cut.Status.ClearedOut");
    }

    [RelayCommand(CanExecute = nameof(CanAddCut))]
    private void AddRemoveCut()
    {
        if (GuardCutLocked()) return;
        if (Editor is null || MarkIn is not { } start || MarkOut is not { } end) return;
        var (result, seg) = Editor.AddCut(start, end, SegmentAction.Remove, 0);
        if (!result.Success)
        {
            _dialogs.ShowWarning(result.Message, "区間を登録できません");
            return;
        }
        MarkIn = null;
        MarkOut = null;
        SelectedTabIndex = TabCuts;
        StatusMessage = $"カット（削除）区間を登録しました: {TimeFormat.FormatShort(seg!.Start)}～{TimeFormat.FormatShort(seg.End)}" + (result.Message.Length > 0 ? " " + result.Message : "");
    }

    [RelayCommand]
    private void EditCut(CutSegmentItem? item)
    {
        if (Editor is null || item is null || GuardCutLocked()) return;
        var vm = new CutEditViewModel(Editor, item.Model, () => CurrentTime);
        if (_windows.EditCut(vm)) StatusMessage = vm.ResultMessage.Length > 0 ? vm.ResultMessage : "区間を変更しました。";
    }

    [RelayCommand]
    private void DeleteCuts(IList? items)
    {
        if (Editor is null || items is null || items.Count == 0 || GuardCutLocked()) return;
        var targets = items.OfType<CutSegmentItem>().ToList();
        int revived = Editor.Project.CutCandidates.Count(c => c.AcceptedSegmentId is { } id && targets.Any(t => t.Id == id));
        foreach (var item in targets) Editor.RemoveCut(item.Id);
        StatusMessage = Loc.Format("Cut.Status.Undone", targets.Count, revived);
    }

    [RelayCommand]
    private void SeekToCut(CutSegmentItem? item)
    {
        if (item is null) return;
        PauseIfPlaying();
        SeekTo(item.Model.Start);
    }

    // ---------------- 候補 ----------------

    [RelayCommand]
    private void SeekToCandidate(CandidateItem? item)
    {
        if (item is null) return;
        PauseIfPlaying();
        SeekTo(item.Model.Start);
    }

    // ---------------- ローカル自動解析 ----------------

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task RunAnalysisAsync()
    {
        if (Editor is null || Metadata is null || !EnsureFFmpeg()) return;
        var caps = FFmpeg.Capabilities;
        var settings = Settings.Analysis;
        if (caps is not null && ((settings.EnableFreeze && !caps.HasFreezeDetect) || (settings.EnableSilence && !caps.HasSilenceDetect)))
        {
            _dialogs.ShowError("この ffmpeg には freezedetect / silencedetect がありません。別の ffmpeg を指定してください。");
            return;
        }
        PauseIfPlaying();
        Core.Analysis.AnalysisResult? result = null;
        try
        {
            var completed = await _windows.RunWithProgressAsync("ローカル自動解析", async (progress, ct) =>
            {
                var p = new Progress<double>(r => progress.Report(new ProgressInfo(r, $"静止・無音・画面変化を解析しています… {r:0%}")));
                result = await _analysis.AnalyzeAsync(FFmpeg.FfmpegPath!, VideoPath, Metadata, settings, p, ct);
            });
            if (!completed || result is null)
            {
                StatusMessage = "解析をキャンセルしました。";
                return;
            }
        }
        catch (Exception ex)
        {
            _analysisState = Core.Guidance.AnalysisRunState.Failed; // 失敗を「候補なし」と表示しない（v1.13）
            NotifyGuidance();
            _dialogs.ShowError("解析に失敗しました。", ex.Message);
            return;
        }

        _analysisState = Core.Guidance.AnalysisRunState.Done;
        var replaced = Editor.Project.CutCandidates.Count(c => c.Source == SegmentSource.LocalAnalysis && c.Status != CandidateStatus.Accepted);
        Editor.ReplaceCandidates(SegmentSource.LocalAnalysis, result.Candidates, "ローカル解析");
        Editor.Project.SceneChanges = result.SceneChanges.ToList();
        Editor.MarkDirty();
        SelectedTabIndex = TabCandidates;
        var fs = result.Candidates.Count(c => c.Kind == CandidateKind.FreezeAndSilence);
        StatusMessage = $"解析完了（{result.Elapsed.TotalSeconds:0.0} 秒）: 候補 {result.Candidates.Count} 件（静止＋無音 {fs} 件）、静止 {result.Freezes.Count} 区間、無音 {result.Silences.Count} 区間、画面変化 {result.SceneChanges.Count} 箇所。候補は採用するまで編集に反映されません。"
            + (replaced > 0 ? " " + Loc.Format("Cand.Replaced", Loc.Get("Cand.Clear.Local"), replaced) : "");
    }

    public const int TabCaptures = 0;
    public const int TabCuts = 1;
    public const int TabCandidates = 4;
    public const int TabInfo = 5;
}
