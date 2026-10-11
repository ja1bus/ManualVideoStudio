namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// 外部の FFmpeg と受け渡す一時ファイルの置き場（v1.17.0）。<c>Path.GetTempPath()</c> は Store 版（MSIX）では
/// パッケージ専用の場所へ振り替えられるため、アプリの保存先（<see cref="AppPaths.TempDirectory"/>）の実パスにそろえる。
/// 起動時に <see cref="Configure"/> する。未設定・作れないときは従来どおり OS の一時フォルダー。
/// </summary>
public static class AppTemp
{
    private static string _root = Path.GetTempPath();

    /// <summary>一時ファイルを置くフォルダー（存在するフォルダーのフルパス）。</summary>
    public static string Root => _root;

    /// <summary>置き場を設定する。作れなかったら OS の一時フォルダーのままにして、理由を返す（成功なら null）。</summary>
    public static string? Configure(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _root = Path.GetFullPath(directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _root = Path.GetTempPath();
            return $"{directory}: {ex.Message}";
        }
    }

    /// <summary>テスト用: OS の一時フォルダーへ戻す。</summary>
    public static void Reset() => _root = Path.GetTempPath();

    /// <summary>置き場の中で、指定の期間より古い残り（前回までの異常終了などで消えなかったもの）を消せる範囲で消す。消した数を返す。</summary>
    public static int CleanupOld(TimeSpan age)
    {
        var limit = DateTime.UtcNow - age;
        int removed = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
            {
                try
                {
                    var name = Path.GetFileName(entry);
                    // このアプリが作ったものだけ（接頭辞で判断）。ほかのファイルには触れない。
                    if (!name.StartsWith("manualvideostudio-", StringComparison.Ordinal) && name != "work") continue;
                    if (Directory.Exists(entry))
                    {
                        if (name == "work")
                        {
                            foreach (var f in Directory.EnumerateFileSystemEntries(entry)) removed += TryDeleteOld(f, limit);
                        }
                        else removed += TryDeleteOld(entry, limit);
                    }
                    else removed += TryDeleteOld(entry, limit);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static int TryDeleteOld(string path, DateTime limitUtc)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (Directory.GetLastWriteTimeUtc(path) >= limitUtc) return 0;
                Directory.Delete(path, recursive: true);
                return 1;
            }
            if (File.GetLastWriteTimeUtc(path) >= limitUtc) return 0;
            File.Delete(path);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
