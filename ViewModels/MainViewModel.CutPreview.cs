using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// カット編集中の再生の確認（v1.17.0）。［元動画を再生］と［カットを反映して再生］の切り替え、つなぎ目の前後の再生、
/// 未採用の候補を採用した場合の試し再生（プロジェクトにも元に戻す履歴にも触れない）。
/// <para>
/// 限界: 削除される部分をシーク（位置の移動）で飛ばす再生で、書き出し時のクロスフェード・音声のつなぎ目の処理は再現しない。
/// 飛ばす位置で映像が一瞬止まる・音が途切れることがある。つなぎ目を厳密に確認するには、書き出した動画を再生する。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>つなぎ目の前後を何秒ずつ再生するか。</summary>
    public const double PreviewSeconds = 3.0;

    /// <summary>カット編集中の再生でカットを反映するか（false=元動画をそのまま再生）。字幕編集中は常に反映する。</summary>
    [ObservableProperty]
    private bool _applyCutsInPreview;

    partial void OnApplyCutsInPreviewChanged(bool value)
    {
        if (!value) ClearPreviewState(pause: false);
        StatusMessage = Loc.Get(value ? "Preview.AppliedOn" : "Preview.OriginalOn");
    }

    // 試し再生・つなぎ目の再生の状態。
    private IReadOnlyList<TimeRange>? _previewKeep;
    private double? _previewStopSource;

    /// <summary>試し再生・つなぎ目の再生中の案内（それ以外は空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewBanner))]
    private string _previewBannerText = "";

    public bool HasPreviewBanner => PreviewBannerText.Length > 0;

    /// <summary>試し再生・つなぎ目の再生を終える（編集・移動・停止などで呼ぶ）。</summary>
    private void ClearPreviewState(bool pause)
    {
        if (_previewKeep is null && _previewStopSource is null && PreviewBannerText.Length == 0) return;
        _previewKeep = null;
        _previewStopSource = null;
        PreviewBannerText = "";
        if (pause) PauseIfPlaying();
    }

    [RelayCommand]
    private void StopPreview() => ClearPreviewState(pause: true);

    /// <summary>再生中の位置の扱い: 試し再生の終わりに達したら止める。止めたら true。</summary>
    private bool StopPreviewIfReached(double sourceTime)
    {
        if (_previewStopSource is not { } stop || _player is not { IsPlaying: true } player || sourceTime < stop) return false;
        player.Pause();
        IsPlaying = false;
        player.Position = stop;
        CurrentTime = Math.Round(stop, 3);
        ClearPreviewState(pause: false);
        StatusMessage = Loc.Get("Preview.Finished");
        return true;
    }

    /// <summary>再生で削除部分を飛ばすか、飛ばすときの「残す区間」。試し再生中はそちらを優先する。</summary>
    private (bool Active, IReadOnlyList<TimeRange> Keep) PlaybackSkipPlan()
    {
        if (_previewKeep is not null) return (true, _previewKeep);
        return (IsSubtitlePhase || ApplyCutsInPreview, KeepRanges);
    }

    /// <summary>元動画の時刻から再生を始める（プレーヤーが無い・開いていないときは移動だけ）。</summary>
    private void StartPlaybackAt(double sourceTime)
    {
        SeekToForPreview(sourceTime);
        if (_player is not { IsOpened: true } player) return;
        player.Play();
        IsPlaying = true;
    }

    private void SeekToForPreview(double sourceTime)
    {
        if (!HasProject) return;
        sourceTime = Math.Clamp(sourceTime, 0, Duration > 0 ? Duration : double.MaxValue);
        CurrentTime = Math.Round(sourceTime, 3);
        if (_player is { IsOpened: true } p) p.Position = CurrentTime;
    }

    private void BeginPreview(IReadOnlyList<TimeRange> keep, double startSource, double stopSource, string banner)
    {
        PauseIfPlaying();
        _previewKeep = keep;
        _previewStopSource = stopSource;
        PreviewBannerText = banner;
        StartPlaybackAt(startSource);
    }

    /// <summary>つなぎ目の出力時刻（削除・短縮でつながる位置）を中心に、前後 <see cref="PreviewSeconds"/> 秒の元動画の時刻を求める。</summary>
    private static (double StartSource, double StopSource) JoinWindow(double joinSourceBeforeCut, IReadOnlyList<TimeRange> keep)
    {
        var join = EditTimelineCalculator.MapToOutputClamped(joinSourceBeforeCut, keep);
        var total = EditTimelineCalculator.OutputDuration(keep);
        var startOut = Math.Max(0, join - PreviewSeconds);
        var stopOut = Math.Min(total, join + PreviewSeconds);
        return (EditTimelineCalculator.MapToSource(startOut, keep), EditTimelineCalculator.MapToSource(stopOut, keep));
    }

    // ---------------- 未採用の候補の試し再生 ----------------

    /// <summary>
    /// 選んだ未判断の候補を<b>提案どおりに採用した場合</b>のつなぎ目の前後を、仮に試して再生する。
    /// 仮のカット一覧は再生にだけ使い、プロジェクトにも元に戻す履歴にも触れない（保存・採用はされない）。
    /// </summary>
    [RelayCommand]
    private void PreviewCandidateTrial(CandidateItem? item)
    {
        if (Editor is null || item is null) return;
        if (!item.IsPending) { StatusMessage = Loc.Get("Preview.TrialPendingOnly"); return; }
        var plan = Editor.PlanAccept([item.Id], CandidateAcceptMode.Suggested, 0).Items[0];
        if (!plan.Applicable)
        {
            StatusMessage = Loc.Format("Preview.TrialNotApplicable", plan.Reason);
            return;
        }
        var trialCuts = Editor.Project.CutSegments.Append(new CutSegment { Start = plan.Start, End = plan.End, Action = plan.Action, KeepSeconds = plan.KeepSeconds }).ToList();
        var keep = EditTimelineCalculator.ComputeKeepRanges(Duration, trialCuts);
        if (keep.Count == 0) { StatusMessage = Loc.Get("Preview.NothingKept"); return; }
        double before = plan.Action == SegmentAction.Shorten ? plan.Start + plan.KeepSeconds : plan.Start;
        var (start, stop) = JoinWindow(before, keep);
        BeginPreview(keep, start, stop, Loc.Format("Preview.TrialBanner", item.StartText, item.EndText, item.SuggestionText));
    }
}
