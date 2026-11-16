using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Ai;

/// <summary>
/// AI 履歴の上限（v1.22、仕様書 §44.2）。各履歴はプロンプトと回答の全文を持つため、プロジェクトファイルが増え続けないよう件数を抑える。
/// 新しい履歴を追加したときだけ呼ぶ（読み込み・既存の履歴の更新では削らない）。
/// </summary>
public static class AiHistoryLimit
{
    public const int MaxEntries = 30;

    /// <summary>
    /// 件数が <paramref name="max"/> を超えていれば、守る履歴（<paramref name="keepId"/> と、候補の <see cref="CutCandidate.AiHistoryId"/> が指す履歴）を除き、
    /// 古い順（<see cref="AiHistoryEntry.PromptCreatedAt"/>、無ければ <see cref="AiHistoryEntry.ParsedAt"/>、どちらも無ければ最も古い。同じなら一覧の前の方）に削る。
    /// 守る履歴だけが残れば上限を超えたままにする。削った件数を返す。
    /// </summary>
    public static int Prune(List<AiHistoryEntry> history, IEnumerable<CutCandidate> candidates, string? keepId, int max = MaxEntries)
    {
        if (history.Count <= max) return 0;
        var protectedIds = candidates.Select(c => c.AiHistoryId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (keepId is not null) protectedIds.Add(keepId);

        var removable = history
            .Select((h, index) => (Entry: h, Index: index))
            .Where(x => !protectedIds.Contains(x.Entry.Id))
            .OrderBy(x => x.Entry.PromptCreatedAt ?? x.Entry.ParsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Index)
            .Take(history.Count - max)
            .Select(x => x.Entry)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        return history.RemoveAll(h => removable.Contains(h));
    }
}
