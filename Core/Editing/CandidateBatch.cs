using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>候補を確定カットにするときの動作（v1.17.0）。</summary>
public enum CandidateAcceptMode
{
    /// <summary>候補に記録された提案どおり（ローカル解析は先頭を残す短縮、AI は削除または短縮）。</summary>
    Suggested,
    /// <summary>候補の区間を丸ごと削除。</summary>
    Remove,
    /// <summary>区間の先頭から指定の秒数だけ残して短縮（指定値が使えない候補は黙って丸めず、採用しない）。</summary>
    Shorten,
}

/// <summary>候補 1 件の採用の見込み（<see cref="ProjectEditor.PlanAccept"/> の結果。何も変更しない）。</summary>
/// <param name="CandidateId">候補の Id。</param>
/// <param name="Applicable">採用できるか。</param>
/// <param name="Reason">採用できない理由（採用できるなら空）。</param>
/// <param name="Action">採用したときの動作（採用できないときは候補の提案）。</param>
/// <param name="KeepSeconds">短縮のとき先頭に残す秒数（削除は 0）。</param>
/// <param name="RemovedSeconds">採用すると短くなる秒数（採用できないときは 0）。</param>
/// <param name="Start">区間の開始（元動画の時刻）。</param>
/// <param name="End">区間の終了（元動画の時刻）。</param>
/// <param name="Merges">重なって（接して）いたため 1 つにまとめる既存のカットの件数。</param>
public sealed record CandidateAcceptPlanItem(string CandidateId, bool Applicable, string Reason, SegmentAction Action, double KeepSeconds,
    double RemovedSeconds, double Start, double End, int Merges = 0);

/// <summary>複数の候補を採用するときの見込み。画面の確認と、採用できない候補の件数・理由の表示に使う。</summary>
public sealed record CandidateAcceptPlan(IReadOnlyList<CandidateAcceptPlanItem> Items)
{
    public int ApplicableCount => Items.Count(i => i.Applicable);
    public int SkippedCount => Items.Count(i => !i.Applicable);
    public double RemovedSeconds => Items.Where(i => i.Applicable).Sum(i => i.RemovedSeconds);
    public int MergedCount => Items.Where(i => i.Applicable).Sum(i => i.Merges);
    public IEnumerable<CandidateAcceptPlanItem> Skipped => Items.Where(i => !i.Applicable);
}

/// <summary>一括操作の結果。</summary>
/// <param name="Plan">実行した（または実行できなかった）見込み。</param>
/// <param name="Changed">プロジェクトを変更したか（1 回の元に戻す操作で戻せる）。</param>
public sealed record CandidateBatchResult(CandidateAcceptPlan Plan, bool Changed)
{
    public int Applied => Plan.ApplicableCount;
}

public sealed partial class ProjectEditor
{
    /// <summary>
    /// 候補を採用した場合の見込みを、何も変更せずに計算する。複数の候補が互いに重なる場合は、時刻順に先に採用できるものを数え、
    /// 後の候補は「重なり」で採用できないものとして扱う。採用済み・見つからない候補、字幕編集中（カット固定）も採用できない。
    /// </summary>
    public CandidateAcceptPlan PlanAccept(IEnumerable<string> candidateIds, CandidateAcceptMode mode, double keepSeconds)
    {
        var items = new List<CandidateAcceptPlanItem>();
        var ids = candidateIds.Distinct().ToList();
        var ordered = ids.Select(id => Project.CutCandidates.FirstOrDefault(c => c.Id == id)).ToList();
        // 時刻順に評価する（一覧の並び・選択の順に左右されない）。
        var cuts = Project.CutSegments.ToList();
        var results = new Dictionary<string, CandidateAcceptPlanItem>();
        foreach (var c in ordered.Where(c => c is not null).Cast<CutCandidate>().OrderBy(c => c.Start).ThenBy(c => c.End))
        {
            var item = PlanOne(c, mode, keepSeconds, cuts);
            results[c.Id] = item;
            if (item.Applicable)
            {
                var over = FindTouching(cuts, item.Start, item.End);
                var (ms, me) = Union(item.Start, item.End, over);
                cuts.RemoveAll(over.Contains);
                cuts.Add(new CutSegment { Start = over.Count > 0 ? ms : item.Start, End = over.Count > 0 ? me : item.End, Action = over.Count > 0 ? SegmentAction.Remove : item.Action, KeepSeconds = over.Count > 0 ? 0 : item.KeepSeconds });
            }
        }
        for (int i = 0; i < ids.Count; i++)
        {
            if (results.TryGetValue(ids[i], out var r)) items.Add(r);
            else items.Add(new CandidateAcceptPlanItem(ids[i], false, "候補が見つかりません。", SegmentAction.Remove, 0, 0, 0, 0));
        }
        return new CandidateAcceptPlan(items);
    }

    private CandidateAcceptPlanItem PlanOne(CutCandidate c, CandidateAcceptMode mode, double keepSeconds, List<CutSegment> cuts)
    {
        var start = Math.Round(c.Start, 3);
        var end = Math.Round(Math.Min(c.End, VideoDuration > 0 ? VideoDuration : c.End), 3);
        // 短縮は廃止（v1.17.0）。明示的に指定されたときと、古い候補の提案だけで残る。
        var action = mode switch
        {
            CandidateAcceptMode.Remove => SegmentAction.Remove,
            CandidateAcceptMode.Shorten => SegmentAction.Shorten,
            _ => c.SuggestedAction,
        };
        var keep = action == SegmentAction.Shorten
            ? Math.Round(mode == CandidateAcceptMode.Shorten ? keepSeconds : c.KeepSeconds, 3)
            : 0;
        CandidateAcceptPlanItem Fail(string reason) => new(c.Id, false, reason, action, keep, 0, start, end);

        if (IsCutLocked) return Fail("字幕編集中のため、カットを変更できません。");
        if (c.Status == CandidateStatus.Accepted) return Fail("採用済みです。");
        // 既存のカットと重なる（接する）ときは、既存のカットも含めて 1 つにまとめる（採用できる）。
        var touching = FindTouching(cuts, start, end);
        if (touching.Count > 0)
        {
            var (ms, me) = Union(start, end, touching);
            var vm = CutSegmentValidator.Validate(ms, me, SegmentAction.Remove, 0, VideoDuration, cuts.Where(x => !touching.Contains(x)));
            if (!vm.IsValid) return Fail(vm.Errors[0]);
            var added = (me - ms) - touching.Sum(t => t.RemovedSeconds);
            return new CandidateAcceptPlanItem(c.Id, true, "", SegmentAction.Remove, 0, Math.Max(0, added), start, end, touching.Count);
        }
        var v = CutSegmentValidator.Validate(start, end, action, keep, VideoDuration, cuts);
        if (!v.IsValid)
        {
            var reason = v.Errors[0];
            if (action == SegmentAction.Shorten && keep >= end - start - CutSegmentValidator.Epsilon && keep > 0)
                reason = $"残す秒数 {keep:0.0#} 秒が、候補の長さ {end - start:0.0#} 秒以上のため短縮できません。";
            else if (reason.StartsWith("既存の区間"))
                reason = "既にあるカットと重なっています（" + reason["既存の区間 ".Length..].TrimEnd('。') + "）。";
            return Fail(reason);
        }
        var removed = action == SegmentAction.Shorten ? end - start - keep : end - start;
        return new CandidateAcceptPlanItem(c.Id, true, "", action, keep, removed, start, end);
    }

    /// <summary>
    /// 候補をまとめて採用する（<b>1 回の元に戻す操作</b>で全件を戻せる）。採用できない候補（重なり・短縮の秒数が合わない・採用済みなど）は
    /// 採用せず、結果の見込みに理由が入る。1 件も採用できなければ何も記録しない。
    /// </summary>
    public CandidateBatchResult AcceptCandidates(IEnumerable<string> candidateIds, CandidateAcceptMode mode, double keepSeconds)
    {
        var plan = PlanAccept(candidateIds, mode, keepSeconds);
        var applicable = plan.Items.Where(i => i.Applicable).OrderBy(i => i.Start).ToList();
        if (applicable.Count == 0) return new CandidateBatchResult(plan, false);
        Apply(applicable.Count == 1 ? "候補の採用" : $"候補の一括採用（{applicable.Count} 件）", () =>
        {
            foreach (var item in applicable)
            {
                var c = Project.CutCandidates.First(x => x.Id == item.CandidateId);
                var over = FindTouching(Project.CutSegments, item.Start, item.End);
                CutSegment seg;
                if (over.Count > 0) seg = ApplyMerge(item.Start, item.End, over, null, c.Source, c.Reason, c.Confidence);
                else
                {
                    seg = new CutSegment
                    {
                        Start = item.Start, End = item.End, Action = item.Action, KeepSeconds = item.KeepSeconds,
                        Source = c.Source, Confidence = c.Confidence, Reason = c.Reason,
                    };
                    Project.CutSegments.Add(seg);
                }
                c.Status = CandidateStatus.Accepted;
                c.AcceptedSegmentId = seg.Id;
            }
            SortCuts();
        });
        return new CandidateBatchResult(plan, true);
    }

    /// <summary>
    /// 未判断の候補をまとめて却下する、または却下した候補を未判断に戻す（1 回の元に戻す操作で戻せる）。
    /// 採用済みの候補は対象外。変更した件数を返す（0 なら何も記録しない）。
    /// </summary>
    public int SetCandidatesStatus(IEnumerable<string> candidateIds, CandidateStatus status)
    {
        if (status == CandidateStatus.Accepted) throw new ArgumentException("採用は AcceptCandidates を使ってください。", nameof(status));
        var from = status == CandidateStatus.Dismissed ? CandidateStatus.Pending : CandidateStatus.Dismissed;
        var targets = candidateIds.Distinct()
            .Select(id => Project.CutCandidates.FirstOrDefault(c => c.Id == id))
            .Where(c => c is not null && c.Status == from).Select(c => c!.Id).ToList();
        if (targets.Count == 0) return 0;
        var label = status == CandidateStatus.Dismissed ? "候補の却下" : "候補を未判断に戻す";
        Apply(targets.Count > 1 ? $"{label}（{targets.Count} 件）" : label, () =>
        {
            foreach (var id in targets) Project.CutCandidates.First(c => c.Id == id).Status = status;
        });
        return targets.Count;
    }

    /// <summary>
    /// 指定出所の未採用候補（未判断・却下）を削除する。採用済みの記録と確定カットは残す。
    /// 削除した件数と、残った採用済みの件数を返す（対象が 0 件なら何も記録しない）。
    /// </summary>
    public (int Removed, int KeptAccepted) ClearUnacceptedCandidates(SegmentSource source)
    {
        int accepted = Project.CutCandidates.Count(c => c.Source == source && c.Status == CandidateStatus.Accepted);
        int removed = Project.CutCandidates.Count(c => c.Source == source && c.Status != CandidateStatus.Accepted);
        if (removed == 0) return (0, accepted);
        Apply("候補のクリア", () => Project.CutCandidates.RemoveAll(c => c.Source == source && c.Status != CandidateStatus.Accepted));
        return (removed, accepted);
    }
}
