using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Guidance;

/// <summary>開始画面の「最近のプロジェクト」の 1 行。IsVideoOnly なら編集の無い録画（動画を開く）。</summary>
public sealed record RecentProjectEntry(string Name, DateTime Date, string FullPath, bool IsVideoOnly, string Problem);

/// <summary>
/// 開始画面に出す最近のプロジェクトを、既存のプロジェクト索引（projects.json）から作る（v1.13）。
/// 新しい管理ファイルや履歴は作らない。編集は更新日時、編集の無い録画は録画日時の新しい順。
/// </summary>
public static class RecentProjectList
{
    public static IReadOnlyList<RecentProjectEntry> Build(ProjectIndex index, string root, int count)
    {
        var all = new List<RecentProjectEntry>();
        foreach (var r in index.Recordings)
        {
            var folder = Path.Combine(root, r.Folder);
            if (r.Edits.Count == 0)
            {
                if (r.Video.Length > 0) all.Add(new RecentProjectEntry(r.Name, r.Date, Path.GetFullPath(Path.Combine(folder, r.Video)), true, r.Problem));
                continue;
            }
            foreach (var e in r.Edits)
                all.Add(new RecentProjectEntry(e.Name, e.UpdatedAt, Path.GetFullPath(Path.Combine(folder, e.Path)), false, e.Problem.Length > 0 ? e.Problem : r.Problem));
        }
        return all.OrderByDescending(x => x.Date).Take(Math.Max(0, count)).ToList();
    }
}
