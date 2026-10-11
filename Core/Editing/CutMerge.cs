using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>
/// 既存の確定カットと重なる（接する）区間を、既存のカットも含めて 1 つのカットにまとめる（v1.17.0）。
/// 手動のカット・カット区間の編集・候補の採用に共通。エラーにせず、操作は 1 回の元に戻す操作で戻せる。
/// </summary>
public sealed partial class ProjectEditor
{
    /// <summary>[start, end] と重なる（接する）確定カット（開始順）。<paramref name="ignoreId"/> は除く。</summary>
    internal static List<CutSegment> FindTouching(IEnumerable<CutSegment> cuts, double start, double end, string? ignoreId = null)
        => cuts.Where(o => o.Id != ignoreId && start <= o.End + CutSegmentValidator.Epsilon && o.Start <= end + CutSegmentValidator.Epsilon)
               .OrderBy(o => o.Start).ToList();

    /// <summary>新しい区間と重なっているカットを合わせた、いちばん外側の区間。</summary>
    internal static (double Start, double End) Union(double start, double end, IReadOnlyList<CutSegment> over)
        => over.Count == 0 ? (start, end) : (Math.Min(start, over.Min(o => o.Start)), Math.Max(end, over.Max(o => o.End)));

    internal static string MergeMessage(int count, double start, double end)
        => $"既存のカット {count} 件と重なって（接して）いたため、1 つにまとめました（{TimeFormat.FormatShort(start)}～{TimeFormat.FormatShort(end)}）。";

    /// <summary>
    /// 重なっているカットを取り除き、まとめた 1 つのカットを追加する（<c>Apply</c> の中で呼ぶ）。
    /// まとめた側に候補の採用記録があれば、まとめたカットに付け替える（取り消すと候補は未判断に戻る）。
    /// </summary>
    /// <param name="keepId">まとめたカットに引き継ぐ Id（編集中のカットなら、その Id。無ければ最初の既存カットの Id）。</param>
    private CutSegment ApplyMerge(double start, double end, IReadOnlyList<CutSegment> over, string? keepId,
        SegmentSource source, string reason, double? confidence)
    {
        var (ms, me) = Union(start, end, over);
        var id = keepId ?? over.FirstOrDefault()?.Id ?? CutSegment.NewId();
        var removeIds = over.Select(o => o.Id).Append(id).ToHashSet();
        var sources = over.Select(o => o.Source).Append(source).Distinct().ToList();
        var seg = new CutSegment
        {
            Id = id, Start = ms, End = me, Action = SegmentAction.Remove, KeepSeconds = 0,
            Source = sources.Count == 1 ? sources[0] : SegmentSource.Manual,
            Reason = reason.Length > 0 ? reason : over.Select(o => o.Reason).FirstOrDefault(r => r.Length > 0) ?? "",
            Confidence = over.Count == 0 ? confidence : null,
        };
        Project.CutSegments.RemoveAll(c => removeIds.Contains(c.Id));
        Project.CutSegments.Add(seg);
        foreach (var c in Project.CutCandidates.Where(c => c.AcceptedSegmentId is { } sid && removeIds.Contains(sid))) c.AcceptedSegmentId = id;
        SortCuts();
        return seg;
    }

    /// <summary>
    /// 古いプロジェクトの「短縮」（先頭を残す）を、「先頭を残した後ろの区間の削除」に変換する（v1.17.0 で短縮を廃止）。
    /// 再生・書き出しの結果は変わらない。確定カットと、候補の提案が対象。元に戻す履歴には入れない。変換した件数を返す。
    /// </summary>
    public int ConvertLegacyShorten()
    {
        int n = 0;
        foreach (var c in Project.CutSegments.Where(c => c.Action == SegmentAction.Shorten))
        {
            var from = Math.Round(c.Start + c.KeepSeconds, 3);
            if (from < c.End - CutSegmentValidator.Epsilon) c.Start = from;
            c.Action = SegmentAction.Remove;
            c.KeepSeconds = 0;
            n++;
        }
        foreach (var c in Project.CutCandidates.Where(c => c.SuggestedAction == SegmentAction.Shorten))
        {
            var from = Math.Round(c.Start + c.KeepSeconds, 3);
            if (from < c.End - CutSegmentValidator.Epsilon) c.Start = from;
            c.SuggestedAction = SegmentAction.Remove;
            c.KeepSeconds = 0;
            if (c.Status != CandidateStatus.Accepted) n++;
        }
        if (n > 0) SortCuts();
        return n;
    }
}
