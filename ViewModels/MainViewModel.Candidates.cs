using System.Collections;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>どの一覧の行を選ぶか（タイムラインと一覧の連動。v1.17.0）。</summary>
public enum ListKind
{
    Cuts,
    Candidates,
    Subtitles,
    Shapes,
}

/// <summary>
/// 解析候補の確認・採用（v1.17.0）。絞り込み、複数選択の概要、まとめて採用・却下（1 回の元に戻す操作で戻せる）、
/// 採用して次へ／却下して次へ、候補だけを消す操作、タイムラインと一覧の選択の連動。
/// 全件の自動採用・無条件の自動カットはしない（信頼度は目安で、削除してよい確率ではない）。
/// </summary>
public sealed partial class MainViewModel
{
    // ---------------- 絞り込み ----------------

    public static IReadOnlyList<string> CandidateSourceFilters { get; } =
        [Loc.Get("Cand.Filter.AllSources"), Loc.Get("Cand.Filter.Local"), Loc.Get("Cand.Filter.Ai")];

    public static IReadOnlyList<string> CandidateKindFilters { get; } =
        [Loc.Get("Cand.Filter.AllKinds"), Loc.Get("Cand.Filter.FreezeSilence"), Loc.Get("Cand.Filter.Freeze"), Loc.Get("Cand.Filter.Silence"), Loc.Get("Cand.Filter.AiKind")];

    public static IReadOnlyList<string> CandidateStatusFilters { get; } = [Loc.Get("Cand.View.Pending"), Loc.Get("Cand.View.Dismissed"), Loc.Get("Cand.View.All")];

    /// <summary>表示する状態（0=未判断 / 1=却下 / 2=すべて（採用済みの記録も））。</summary>
    [ObservableProperty] private int _candidateStatusFilter;

    partial void OnCandidateStatusFilterChanged(int value) => RefreshAll();

    /// <summary>出所の絞り込み（0=すべて / 1=ローカル解析 / 2=AI）。</summary>
    [ObservableProperty] private int _candidateSourceFilter;

    /// <summary>種類の絞り込み（0=すべて / 1=静止＋無音 / 2=静止のみ / 3=無音のみ / 4=AI）。</summary>
    [ObservableProperty] private int _candidateKindFilter;

    partial void OnCandidateSourceFilterChanged(int value) => RefreshAll();
    partial void OnCandidateKindFilterChanged(int value) => RefreshAll();

    /// <summary>絞り込みが効いているか（一覧が空のときの案内・表示件数に使う）。</summary>
    public bool IsCandidateFiltered => CandidateSourceFilter != 0 || CandidateKindFilter != 0 || CandidateStatusFilter != 0;

    /// <summary>一覧に出す候補（未判断を既定とし、設定で却下・採用済みも含める。出所・種類の絞り込みを掛ける）。</summary>
    private IReadOnlyList<CutCandidate> FilteredCandidates(ProjectDocument? p)
    {
        if (p is null) return [];
        return p.CutCandidates.Where(c => CandidateStatusFilter switch { 1 => c.Status == CandidateStatus.Dismissed, 2 => true, _ => c.Status == CandidateStatus.Pending }
            && CandidateSourceFilter switch { 1 => c.Source == SegmentSource.LocalAnalysis, 2 => c.Source == SegmentSource.Ai, _ => true }
            && CandidateKindFilter switch
            {
                1 => c.Kind == CandidateKind.FreezeAndSilence,
                2 => c.Kind == CandidateKind.Freeze,
                3 => c.Kind == CandidateKind.Silence,
                4 => c.Kind == CandidateKind.Ai,
                _ => true,
            }).ToList();
    }

    /// <summary>「N 件を表示（全 M 件）」の表示。</summary>
    public string CandidateCountText
    {
        get
        {
            var total = Project?.CutCandidates.Count ?? 0;
            var pending = PendingCandidateCount;
            return Loc.Format("Cand.CountText", CandidateItems.Count, total, pending);
        }
    }

    // ---------------- 選択と概要 ----------------

    private IReadOnlyList<string> _selectedCandidateIdList = [];

    /// <summary>候補一覧で選択中の候補の Id（タイムラインの強調に使う）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedCandidateIds = [];

    /// <summary>カット一覧で選択中のカットの Id（タイムラインの強調に使う）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedCutIds = [];

    /// <summary>選択中の候補の概要（件数・採用すると短くなる時間・採用できない件数）。選択が無いときは選び方の案内。</summary>
    [ObservableProperty] private string _candidateSelectionText = Loc.Get("Cand.SelectionHelp");

    /// <summary>一覧の選択が変わったとき（View から）。</summary>
    public void SetSelectedCandidates(IEnumerable<string> ids)
    {
        _selectedCandidateIdList = ids.ToList();
        SelectedCandidateIds = _selectedCandidateIdList;
        UpdateCandidateSelectionText();
        UpdateDismissButtonText();
    }

    /// <summary>［却下］ボタンの表示。選択が却下済みの候補だけなら［未判断に戻す］になる。</summary>
    [ObservableProperty] private string _dismissButtonText = Loc.Get("Cand.Dismiss");

    private void UpdateDismissButtonText()
    {
        var sel = Project?.CutCandidates.Where(c => _selectedCandidateIdList.Contains(c.Id)).ToList() ?? [];
        DismissButtonText = Loc.Get(sel.Count > 0 && sel.All(c => c.Status == CandidateStatus.Dismissed) ? "Cand.Restore" : "Cand.Dismiss");
    }

    /// <summary>選択が却下済みの候補だけなら未判断に戻し、そうでなければ却下する。</summary>
    [RelayCommand]
    private void DismissOrRestore(IList? items)
    {
        var list = items?.OfType<CandidateItem>().ToList() ?? [];
        if (list.Count > 0 && list.All(i => i.Model.Status == CandidateStatus.Dismissed)) RestoreCandidates(items);
        else DismissCandidates(items);
    }

    public void SetSelectedCuts(IEnumerable<string> ids) => SelectedCutIds = ids.ToList();

    /// <summary>字幕一覧で選択中の字幕の Id（タイムラインの強調に使う）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedSubtitleIds = [];

    /// <summary>図形一覧で選択中の図形の Id（タイムラインの強調に使う）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _selectedShapeIds = [];


    private void UpdateCandidateSelectionText()
    {
        if (Editor is null || _selectedCandidateIdList.Count == 0)
        {
            CandidateSelectionText = Loc.Get("Cand.SelectionHelp");
            return;
        }
        var pendingIds = Editor.Project.CutCandidates.Where(c => c.Status != CandidateStatus.Accepted && _selectedCandidateIdList.Contains(c.Id)).Select(c => c.Id).ToList();
        if (pendingIds.Count == 0)
        {
            CandidateSelectionText = Loc.Format("Cand.SelectionNoPending", _selectedCandidateIdList.Count);
            return;
        }
        var plan = Editor.PlanAccept(pendingIds, CandidateAcceptMode.Suggested, 0);
        CandidateSelectionText = plan.SkippedCount > 0
            ? Loc.Format("Cand.SelectionSummarySkipped", _selectedCandidateIdList.Count, pendingIds.Count, plan.ApplicableCount, plan.RemovedSeconds.ToString("0.0", CultureInfo.InvariantCulture), plan.SkippedCount)
            : Loc.Format("Cand.SelectionSummary", _selectedCandidateIdList.Count, pendingIds.Count, plan.ApplicableCount, plan.RemovedSeconds.ToString("0.0", CultureInfo.InvariantCulture));
    }

    // ---------------- カットする・却下（処理のあと、次の未判断へ進む） ----------------

    /// <summary>
    /// 選択した未判断の候補を、確定カットとして登録する（候補の区間を削除する。短縮は廃止。v1.17.0）。複数のときは件数・短くなる時間を示して確認し、
    /// 1 回の元に戻す操作で全件を戻せる。登録できない候補は理由と件数を示す。成功したら、次の未判断の候補を選ぶ。
    /// </summary>
    [RelayCommand]
    private void AcceptCandidates(IList? items)
    {
        if (Editor is null || items is null || GuardCutLocked()) return;
        // 未判断だけでなく、却下した候補も、そのままカットにできる。
        var pending = items.OfType<CandidateItem>().Where(i => i.Model.Status != CandidateStatus.Accepted).ToList();
        if (pending.Count == 0)
        {
            StatusMessage = Loc.Get("Cand.NothingSelected");
            return;
        }
        var ids = pending.Select(i => i.Id).ToList();
        var plan = Editor.PlanAccept(ids, CandidateAcceptMode.Suggested, 0);
        var nl = Environment.NewLine;
        if (plan.ApplicableCount == 0)
        {
            _dialogs.ShowWarning(Loc.Get("Cand.NoneApplicable") + nl + SkippedReasons(plan, pending));
            return;
        }
        if (pending.Count > 1)
        {
            var message = Loc.Format("Cand.BatchConfirm", pending.Count, plan.ApplicableCount, plan.RemovedSeconds.ToString("0.0", CultureInfo.InvariantCulture), nl);
            if (plan.SkippedCount > 0) message += nl + nl + Loc.Format("Cand.BatchConfirmSkipped", plan.SkippedCount) + nl + SkippedReasons(plan, pending);
            if (!_dialogs.Confirm(message, Loc.Get("Cand.BatchConfirmTitle"))) return;
        }

        var firstStart = pending.Min(i => i.Model.Start);
        var result = Editor.AcceptCandidates(ids, CandidateAcceptMode.Suggested, 0);
        var status = Loc.Format("Cand.Accepted", result.Applied, result.Plan.RemovedSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        if (result.Plan.MergedCount > 0) status += " " + Loc.Format("Cand.AcceptedMerged", result.Plan.MergedCount);
        if (result.Plan.SkippedCount > 0)
        {
            status += " " + Loc.Format("Cand.AcceptedSkipped", result.Plan.SkippedCount);
            _dialogs.ShowWarning(Loc.Format("Cand.SkippedHeader", result.Plan.SkippedCount) + nl + SkippedReasons(result.Plan, pending));
        }
        StatusMessage = status;
        if (result.Changed) SelectNextPending(firstStart, ids);
    }

    private static string SkippedReasons(CandidateAcceptPlan plan, IReadOnlyList<CandidateItem> items)
    {
        var lines = plan.Skipped.Take(10).Select(s =>
        {
            var it = items.FirstOrDefault(i => i.Id == s.CandidateId);
            return $"・{(it is null ? s.CandidateId : $"{it.StartText}～{it.EndText}")}: {s.Reason}";
        }).ToList();
        if (plan.SkippedCount > 10) lines.Add(Loc.Format("Cand.SkippedMore", plan.SkippedCount - 10));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>選択した未判断の候補を却下する（書き出しには影響しない）。処理のあと、次の未判断の候補を選ぶ。</summary>
    [RelayCommand]
    private void DismissCandidates(IList? items)
    {
        if (Editor is null || items is null) return;
        var targets = items.OfType<CandidateItem>().Where(i => i.IsPending).ToList();
        if (targets.Count == 0) { StatusMessage = Loc.Get("Cand.DismissNothing"); return; }
        var firstStart = targets.Min(i => i.Model.Start);
        var ids = targets.Select(i => i.Id).ToList();
        var n = Editor.SetCandidatesStatus(ids, CandidateStatus.Dismissed);
        StatusMessage = Loc.Format("Cand.Dismissed", n);
        if (n > 0) SelectNextPending(firstStart, ids);
    }

    [RelayCommand]
    private void RestoreCandidates(IList? items)
    {
        if (Editor is null || items is null) return;
        var ids = items.OfType<CandidateItem>().Where(i => i.Model.Status == CandidateStatus.Dismissed).Select(i => i.Id).ToList();
        if (ids.Count == 0) { StatusMessage = Loc.Get("Cand.RestoreNothing"); return; }
        StatusMessage = Loc.Format("Cand.Restored", Editor.SetCandidatesStatus(ids, CandidateStatus.Pending));
    }

    /// <summary>処理した候補の後ろにある、一覧に出ている未判断の候補を選んで、開始の少し前へ移動する（再生はしない）。</summary>
    private void SelectNextPending(double firstStart, IReadOnlyCollection<string> handledIds)
    {
        var next = CandidateItems.Where(i => i.IsPending && !handledIds.Contains(i.Id) && i.Model.Start >= firstStart - 0.0005)
            .OrderBy(i => i.Model.Start).FirstOrDefault();
        if (next is null) { StatusMessage += " " + Loc.Get("Cand.NoNext"); return; }
        SelectInList(ListKind.Candidates, [next.Id], seek: true);
    }

    // ---------------- 候補だけを消す ----------------

    /// <summary>
    /// 指定出所の<b>未採用</b>の候補（未判断・却下）だけを削除する。採用済みの記録と確定カットは残す。
    /// 削除件数と、残った採用済みの件数を示す。元に戻す（Ctrl+Z）で戻せる。
    /// </summary>
    [RelayCommand]
    private void ClearCandidates(string? source)
    {
        if (Editor is null) return;
        var src = source == "ai" ? SegmentSource.Ai : SegmentSource.LocalAnalysis;
        if (src == SegmentSource.LocalAnalysis && Editor.Project.CutCandidates.Any(c => c.Source == src && c.Status != CandidateStatus.Accepted))
            _analysisState = Core.Guidance.AnalysisRunState.NotRun;
        var (removed, kept) = Editor.ClearUnacceptedCandidates(src);
        var what = Loc.Get(src == SegmentSource.Ai ? "Cand.Clear.Ai" : "Cand.Clear.Local");
        StatusMessage = removed == 0
            ? Loc.Format("Cand.Clear.None", what, kept)
            : Loc.Format("Cand.Clear.Done", what, removed, kept);
        NotifyGuidance();
    }

    // ---------------- タイムラインと一覧の連動 ----------------

    /// <summary>View へ、一覧の行を選ぶ要求（タイムラインのクリック・次へ進む操作）。</summary>
    public event Action<ListKind, IReadOnlyList<string>>? ListSelectionRequested;

    /// <summary>
    /// 一覧の行を選ぶ（View が選択・スクロールする）。必要なら一覧の絞り込みを外して、対象が一覧に出るようにする。
    /// seek なら、その項目の少し前へ移動する（再生はしない）。
    /// </summary>
    public void SelectInList(ListKind kind, IReadOnlyList<string> ids, bool seek = false)
    {
        if (Project is null || ids.Count == 0) return;
        if (kind == ListKind.Candidates)
        {
            var target = Project.CutCandidates.FirstOrDefault(c => c.Id == ids[0]);
            if (target is not null && CandidateItems.All(i => i.Id != target.Id))
            {
                // 一覧に出ていない（却下済み・絞り込み）。出るように条件を戻す。
                if (target.Status != CandidateStatus.Pending && CandidateStatusFilter != 2) CandidateStatusFilter = target.Status == CandidateStatus.Dismissed ? 1 : 2;
                CandidateSourceFilter = 0;
                CandidateKindFilter = 0;
            }
            SelectedTabIndex = TabCandidates;
            if (seek && target is not null && !IsPlaying) SeekTo(Math.Max(0, target.Start - LeadSeconds));
        }
        else
        {
            SelectedTabIndex = TabCuts;
            if (seek && Project.CutSegments.FirstOrDefault(c => c.Id == ids[0]) is { } cut && !IsPlaying) SeekTo(Math.Max(0, cut.Start - LeadSeconds));
        }
        ListSelectionRequested?.Invoke(kind, ids);
    }

    /// <summary>項目へ移動するとき、開始の何秒前から見せるか。</summary>
    private const double LeadSeconds = 2.0;

    /// <summary>
    /// タイムラインで確定カット・候補をクリックしたとき。確定カット（赤・橙）は［カット区間］、候補（青・紫）は［解析候補］の対応行を選ぶ。
    /// additive（Ctrl+クリック）なら選択に加える。再生位置の移動は、タイムラインのクリック本来のシークだけが行う。
    /// </summary>
    public void OnTimelineItemClicked(ListKind kind, string id, bool additive)
    {
        // カット・候補はカット編集の画面だけ（字幕編集中はカットを固定しているため、一覧へ移らない）。字幕・図形は字幕編集の画面だけ。
        if (kind is ListKind.Subtitles or ListKind.Shapes)
        {
            if (!ShowSubtitleTools) return;
            SelectedTabIndex = kind == ListKind.Subtitles ? TabSubtitles : TabShapes;
            var cur = kind == ListKind.Shapes ? SelectedShapeIds : SelectedSubtitleIds;
            var picked = additive && kind == ListKind.Shapes ? (cur.Contains(id) ? cur.Where(x => x != id).ToList() : cur.Append(id).ToList()) : [id];
            ListSelectionRequested?.Invoke(kind, picked.Count == 0 ? [id] : picked);
            return;
        }
        if (!ShowCutTools) return;
        var current = kind == ListKind.Cuts ? SelectedCutIds : SelectedCandidateIds;
        var ids = additive ? current.Contains(id) ? current.Where(x => x != id).ToList() : current.Append(id).ToList() : [id];
        if (ids.Count == 0) ids = [id];
        SelectInList(kind, ids);
    }
}
