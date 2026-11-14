using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 字幕編集中の追加カット（v1.21.0。設計 5.1・5.3）。字幕・図形のツール行の［この区間をカット…］と、字幕編集中の R。
/// 開始・終了の指定は字幕と共用で、片方だけなら、もう片方は再生位置。確認画面に内訳を出し、
/// ［カットする］で確定する（1 回の元に戻す操作で、カットも字幕・図形も戻る）。できるのはカットの追加だけ。
/// </summary>
public sealed partial class MainViewModel
{
    private bool CanCutInSubtitlePhase() => HasProject && IsSubtitlePhase && (MarkIn is not null || MarkOut is not null);

    /// <summary>［この区間をカット…］（字幕編集中）。</summary>
    [RelayCommand(CanExecute = nameof(CanCutInSubtitlePhase))]
    private void CutInSubtitlePhase()
    {
        if (!TryResolveMarkedRange(out var start, out var end, out var error))
        {
            StatusMessage = error;
            return;
        }
        CutInSubtitlePhaseRange(start, end);
    }

    /// <summary>元動画の区間 [start, end] を、字幕編集中の追加カットとして確認して登録する。</summary>
    private void CutInSubtitlePhaseRange(double start, double end)
    {
        if (Editor is null) return;
        PauseIfPlaying();
        // 入力中の字幕・図形の時間は、時刻を写す前に反映しておく（反映できなければ止める）。
        if (!FlushSubtitleDraft()) return;
        var plan = Editor.PlanCutInSubtitlePhase(start, end);
        if (!plan.IsApplicable)
        {
            _dialogs.ShowWarning(Loc.Format("SubCut.NotApplicable", plan.Reason), Loc.Get("SubCut.Title"));
            return;
        }
        if (!_windows.ConfirmDanger(Loc.Get("SubCut.Title"), SubtitlePhaseCutConfirmMessage(plan), Loc.Get("SubCut.Ok"), Loc.Get("SubCut.Cancel")))
        {
            StatusMessage = Loc.Get("SubCut.Canceled");
            return;
        }
        var (result, _) = Editor.AddCutInSubtitlePhase(start, end);
        if (!result.Success)
        {
            _dialogs.ShowWarning(Loc.Format("SubCut.NotApplicable", result.Message), Loc.Get("SubCut.Title"));
            return;
        }
        MarkIn = null;
        MarkOut = null;
        StatusMessage = result.Message;
    }

    /// <summary>確認画面の本文（短くなる時間、ずれる・短くなる・削除される件数、削除される字幕の先頭の文字、字幕依頼用動画の注意）。</summary>
    internal static string SubtitlePhaseCutConfirmMessage(SubtitlePhaseCutPlan plan)
    {
        var nl = Environment.NewLine;
        var lines = new List<string>
        {
            Loc.Format("SubCut.Confirm.Range", TimeFormat.FormatShort(plan.OutputStart), TimeFormat.FormatShort(plan.OutputEnd),
                plan.RemovedSeconds.ToString("0.0##", CultureInfo.InvariantCulture)),
            "",
            Loc.Format("SubCut.Confirm.Shifted", plan.SubtitleCount(PhaseCutOutcome.Shifted), plan.ShapeGroupCount(PhaseCutOutcome.Shifted)),
            Loc.Format("SubCut.Confirm.Shortened", plan.SubtitleCount(PhaseCutOutcome.Shortened), plan.ShapeGroupCount(PhaseCutOutcome.Shortened)),
            Loc.Format("SubCut.Confirm.Removed", plan.SubtitleCount(PhaseCutOutcome.Removed), plan.ShapeGroupCount(PhaseCutOutcome.Removed)),
        };
        if (plan.RemovedSubtitleTexts.Count > 0)
        {
            lines.Add(Loc.Get("SubCut.Confirm.RemovedTexts"));
            lines.AddRange(plan.RemovedSubtitleTexts.Select(t => "・" + (t.Length > 30 ? t[..30] + "…" : t)));
            int more = plan.SubtitleCount(PhaseCutOutcome.Removed) - plan.RemovedSubtitleTexts.Count;
            if (more > 0) lines.Add(Loc.Format("SubCut.Confirm.RemovedMore", more));
        }
        if (plan.MergedCount > 0) lines.Add(Loc.Format("SubCut.Confirm.Merged", plan.MergedCount));
        lines.Add("");
        lines.Add(Loc.Get("SubCut.Confirm.Gemini"));
        lines.Add(Loc.Get("SubCut.Confirm.Undo"));
        return string.Join(nl, lines);
    }
}
