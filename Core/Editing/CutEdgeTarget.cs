using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>Q / W で再生位置に合わせるカットの端。</summary>
public enum CutEdge
{
    /// <summary>開始（Q）。</summary>
    Start,
    /// <summary>終了（W）。</summary>
    End,
}

/// <summary>
/// <see cref="CutEdgeTarget.Pick"/> の結果。対象が決まれば <see cref="Cut"/> と新しい範囲、決まらなければ <see cref="Error"/>（日本語の案内）。
/// </summary>
public sealed record CutEdgePick(CutSegment? Cut, double Start, double End, string? Error)
{
    public bool Success => Cut is not null && Error is null;

    public static CutEdgePick Fail(string error, CutSegment? cut = null) => new(cut, 0, 0, error);
}

/// <summary>
/// カットの開始 / 終了を再生位置に合わせる対象を決める純粋ロジック（v1.21.0。設計 3.1）。
/// 優先順: ①一覧で 1 件だけ選択中のカット ②再生位置を含むカット
/// ③ 終了（W）は再生位置より前で終わる最も近いカット、開始（Q）は再生位置より後で始まる最も近いカット。
/// </summary>
public static class CutEdgeTarget
{
    /// <summary>これより短いカットにはしない（タイムラインの端のドラッグと同じ）。</summary>
    public const double MinLength = 0.05;

    public static CutEdgePick Pick(IReadOnlyList<CutSegment> cuts, IReadOnlyList<string> selectedIds, double current, CutEdge edge)
    {
        var target = FindTarget(cuts, selectedIds, current, edge);
        if (target is null)
        {
            return CutEdgePick.Fail(edge == CutEdge.Start
                ? "再生位置を含むカットも、再生位置より後で始まるカットもありません。"
                : "再生位置を含むカットも、再生位置より前で終わるカットもありません。");
        }

        double start = edge == CutEdge.Start ? current : target.Start;
        double end = edge == CutEdge.End ? current : target.End;
        start = Math.Round(start, 3);
        end = Math.Round(end, 3);
        if (end <= start)
        {
            return CutEdgePick.Fail(edge == CutEdge.Start
                ? "再生位置がカットの終了以後のため、開始を合わせられません。"
                : "再生位置がカットの開始以前のため、終了を合わせられません。", target);
        }
        if (end - start < MinLength - 1e-9)
            return CutEdgePick.Fail("カットが 0.05 秒未満になるため、合わせられません。", target);
        return new CutEdgePick(target, start, end, null);
    }

    private static CutSegment? FindTarget(IReadOnlyList<CutSegment> cuts, IReadOnlyList<string> selectedIds, double current, CutEdge edge)
    {
        // ① 一覧で 1 件だけ選択中のカット（候補など、カット以外の id は数えない）。
        var selected = cuts.Where(c => selectedIds.Contains(c.Id)).ToList();
        if (selected.Count == 1) return selected[0];

        // ② 再生位置を含むカット（[Start, End)）。
        var containing = cuts.Where(c => c.Start <= current && current < c.End).OrderBy(c => c.Start).FirstOrDefault();
        if (containing is not null) return containing;

        // ③ 再生位置に最も近い、手前で終わる（W）／後で始まる（Q）カット。
        return edge == CutEdge.End
            ? cuts.Where(c => c.End <= current).OrderByDescending(c => c.End).FirstOrDefault()
            : cuts.Where(c => c.Start > current).OrderBy(c => c.Start).FirstOrDefault();
    }
}
