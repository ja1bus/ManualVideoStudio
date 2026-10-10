namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// 別の PC から同じ場所を指すためのパス（v1.10）。ユーザーフォルダーの中なら「~/Videos/…」（区切りは /）、外ならフルパスのまま。
/// 手順書用の書き出しを別の PC（ユーザー名だけ違う同じ場所）へ写して使うため、依頼文と画像メタデータ JSON に使う。
/// </summary>
public static class PortablePath
{
    public const string HomePrefix = "~/";

    public static string FromHome(string path, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(home)) return full;
        var rel = Path.GetRelativePath(Path.GetFullPath(home), full);
        if (rel == ".") return "~";
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return full;
        return HomePrefix + rel.Replace('\\', '/');
    }
}
