namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// 保存場所。設定（ローミング対象で良い小さなファイル）は %APPDATA%、
/// ログ・キャッシュ（PC 固有で大きくなり得る）は %LOCALAPPDATA%。
/// </summary>
public sealed class AppPaths
{
    public const string AppName = "ManualVideoStudio";

    public AppPaths(string? roamingRoot = null, string? localRoot = null)
    {
        SettingsDirectory = roamingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        LocalDirectory = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    }

    public string SettingsDirectory { get; }
    public string LocalDirectory { get; }
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public string PromptTemplatesFile => Path.Combine(SettingsDirectory, "prompt-templates.json");
    public string LogDirectory => Path.Combine(LocalDirectory, "logs");
    public string CacheDirectory => Path.Combine(LocalDirectory, "cache");
    /// <summary>字幕依頼用動画（Gemini へ添付する字幕なしの動画。v1.12）の保存場所。キャッシュ（cache\）の自動整理の対象外。</summary>
    public string CaptionRequestDirectory => Path.Combine(LocalDirectory, "caption-requests");
    public string CrashLogFile => Path.Combine(LocalDirectory, "error.log");
}

/// <summary>
/// 一時ファイルに書いてから置き換える（書き込み途中の破損を避ける）。
/// 書いた直後のファイルはウイルス対策ソフト等が一時的に開くことがあるため、置き換えが「使用中」で失敗したら
/// 少し待ってやり直す（v1.8.1）。一時ファイルは失敗しても残さないようにし、消せなかった分は次の書き込みで片付ける。
/// </summary>
public static class AtomicFile
{
    /// <summary>「使用中」で失敗したときの待ち時間（ミリ秒、合計 約 2 秒）。</summary>
    public static readonly IReadOnlyList<int> DefaultRetryDelaysMs = [100, 200, 300, 500, 900];

    /// <summary>この時間より古い同じファイルの一時ファイルは、前回までに消せなかった残りとして消す。</summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromSeconds(10);

    public static void WriteAllText(string path, string content) => WriteAllText(path, content, DefaultRetryDelaysMs);

    public static void WriteAllText(string path, string content, IReadOnlyList<int> retryDelaysMs)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        CleanupStaleTemps(dir, Path.GetFileName(path));
        var temp = Path.Combine(dir, $"{TempPrefix(Path.GetFileName(path))}{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    else File.Move(temp, path);
                    return;
                }
                catch (Exception ex) when (IsInUse(ex) && attempt < retryDelaysMs.Count)
                {
                    Thread.Sleep(retryDelaysMs[attempt]);
                }
            }
        }
        finally
        {
            // 後始末の失敗で元の例外（置き換えの失敗）を隠さない。消せなければ次の書き込みで片付ける。
            TryDelete(temp);
        }
    }

    /// <summary>ほかのプロセスがファイルを使用中（共有違反・ロック・置き換え失敗）とみなす例外か。</summary>
    public static bool IsInUse(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
        // ERROR_SHARING_VIOLATION 32 / ERROR_LOCK_VIOLATION 33 / ERROR_UNABLE_TO_REMOVE_REPLACED 1175 /
        // ERROR_UNABLE_TO_MOVE_REPLACEMENT 1176 / ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 1177
        IOException io => (io.HResult & 0xFFFF) is 32 or 33 or 1175 or 1176 or 1177,
        _ => false,
    };

    /// <summary>一時ファイル名の先頭（「.元のファイル名.」）。</summary>
    public static string TempPrefix(string fileName) => $".{fileName}.";

    /// <summary>同じファイルの古い一時ファイル（前回までの失敗の残り）を消せる範囲で消す。</summary>
    public static void CleanupStaleTemps(string directory, string fileName)
    {
        try
        {
            var limit = DateTime.UtcNow - StaleTempAge;
            foreach (var f in Directory.EnumerateFiles(directory, TempPrefix(fileName) + "*.tmp"))
            {
                // 8.3 形式の名前で「*.tmp」が .tmpx 等にも一致するため、末尾を確かめる。
                if (!f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string file)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
