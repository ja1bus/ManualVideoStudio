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
    [NotifyPropertyChangedFor(nameof(MarkInText), nameof(MarkRangeText), nameof(SubtitleMarkInText))]
    [NotifyCanExecuteChangedFor(nameof(AddRemoveCutCommand), nameof(AddShortenCutCommand))]
    private double? _markIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkOutText), nameof(MarkRangeText), nameof(SubtitleMarkOutText))]
    [NotifyCanExecuteChangedFor(nameof(AddRemoveCutCommand), nameof(AddShortenCutCommand))]
    private double? _markOut;

    [ObservableProperty] private string _shortenKeepText;

    /// <summary>［短縮］の詳細（残す秒数と［短縮を登録］）を開いているか（v1.13）。</summary>
    [ObservableProperty] private bool _isShortenDetailOpen;

    [RelayCommand]
    private void ToggleShortenDetail() => IsShortenDetailOpen = !IsShortenDetailOpen;

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

    [RelayCommand]
    private void ClearMarks()
    {
        MarkIn = null;
        MarkOut = null;
    }

    [RelayCommand(CanExecute = nameof(CanAddCut))]
    private void AddRemoveCut() => AddCut(SegmentAction.Remove);

    [RelayCommand(CanExecute = nameof(CanAddCut))]
    private void AddShortenCut() => AddCut(SegmentAction.Shorten);

    private void AddCut(SegmentAction action)
    {
        if (GuardCutLocked()) return;
        if (Editor is null || MarkIn is not { } start || MarkOut is not { } end) return;
        double keep = 0;
        if (action == SegmentAction.Shorten && !double.TryParse(ShortenKeepText, NumberStyles.Float, CultureInfo.InvariantCulture, out keep))
        {
            _dialogs.ShowWarning("短縮で残す秒数を数値で入力してください。");
            return;
        }
        var (result, seg) = Editor.AddCut(start, end, action, keep);
        if (!result.Success)
        {
            _dialogs.ShowWarning(result.Message, "区間を登録できません");
            return;
        }
        ClearMarks();
        IsShortenDetailOpen = false;
        SelectedTabIndex = TabCuts;
        StatusMessage = $"{(action == SegmentAction.Shorten ? "短縮" : "削除")}区間を登録しました: {TimeFormat.FormatShort(seg!.Start)}～{TimeFormat.FormatShort(seg.End)}";
    }

    [RelayCommand]
    private void EditCut(CutSegmentItem? item)
    {
        if (Editor is null || item is null || GuardCutLocked()) return;
        var vm = new CutEditViewModel(Editor, item.Model, () => CurrentTime);
        if (_windows.EditCut(vm)) StatusMessage = "区間を変更しました。";
    }

    [RelayCommand]
    private void DeleteCuts(IList? items)
    {
        if (Editor is null || items is null || items.Count == 0 || GuardCutLocked()) return;
        foreach (var item in items.OfType<CutSegmentItem>().ToList()) Editor.RemoveCut(item.Id);
        StatusMessage = "区間を削除しました（元に戻す: Ctrl+Z）。";
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

    /// <summary>候補の採用。parameter の mode: "suggested"（提案どおり）/ "remove" / "shorten"。</summary>
    private void AcceptCandidates(IList? items, string mode)
    {
        if (Editor is null || items is null || items.Count == 0 || GuardCutLocked()) return;
        double keep = double.TryParse(ShortenKeepText, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? k : 1.5;
        var failures = new List<string>();
        int ok = 0;
        foreach (var item in items.OfType<CandidateItem>().Where(i => i.IsPending).ToList())
        {
            var r = mode switch
            {
                "remove" => Editor.AcceptCandidate(item.Id, SegmentAction.Remove),
                "shorten" => Editor.AcceptCandidate(item.Id, SegmentAction.Shorten, Math.Min(keep, item.Model.Duration / 2)),
                _ => Editor.AcceptCandidate(item.Id),
            };
            if (r.Success) ok++;
            else failures.Add($"{item.StartText}～{item.EndText}: {r.Message}");
        }
        StatusMessage = $"{ok} 件の候補を確定区間にしました。";
        if (failures.Count > 0) _dialogs.ShowWarning("採用できなかった候補があります。" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [RelayCommand] private void AcceptCandidatesSuggested(IList? items) => AcceptCandidates(items, "suggested");
    [RelayCommand] private void AcceptCandidatesAsRemove(IList? items) => AcceptCandidates(items, "remove");
    [RelayCommand] private void AcceptCandidatesAsShorten(IList? items) => AcceptCandidates(items, "shorten");

    [RelayCommand]
    private void DismissCandidates(IList? items)
    {
        if (Editor is null || items is null) return;
        foreach (var item in items.OfType<CandidateItem>().Where(i => i.IsPending).ToList())
            Editor.SetCandidateStatus(item.Id, CandidateStatus.Dismissed);
    }

    [RelayCommand]
    private void RestoreCandidates(IList? items)
    {
        if (Editor is null || items is null) return;
        foreach (var item in items.OfType<CandidateItem>().Where(i => i.Model.Status == CandidateStatus.Dismissed).ToList())
            Editor.SetCandidateStatus(item.Id, CandidateStatus.Pending);
    }

    [RelayCommand]
    private void ClearCandidates(string? source)
    {
        if (Editor is null) return;
        var src = source == "ai" ? SegmentSource.Ai : SegmentSource.LocalAnalysis;
        if (src == SegmentSource.LocalAnalysis) _analysisState = Core.Guidance.AnalysisRunState.NotRun;
        Editor.ClearCandidates(src);
        NotifyGuidance();
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
        Editor.ReplaceCandidates(SegmentSource.LocalAnalysis, result.Candidates, "ローカル解析");
        Editor.Project.SceneChanges = result.SceneChanges.ToList();
        Editor.MarkDirty();
        SelectedTabIndex = TabCandidates;
        var fs = result.Candidates.Count(c => c.Kind == CandidateKind.FreezeAndSilence);
        StatusMessage = $"解析完了（{result.Elapsed.TotalSeconds:0.0} 秒）: 候補 {result.Candidates.Count} 件（静止＋無音 {fs} 件）、静止 {result.Freezes.Count} 区間、無音 {result.Silences.Count} 区間、画面変化 {result.SceneChanges.Count} 箇所。候補は採用するまで編集に反映されません。";
    }

    public const int TabCaptures = 0;
    public const int TabCuts = 1;
    public const int TabCandidates = 4;
    public const int TabInfo = 5;
}
