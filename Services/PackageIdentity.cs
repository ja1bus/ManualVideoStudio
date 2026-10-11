using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Services;

/// <summary>
/// Store 版（MSIX パッケージ）の判定と、実際のアプリデータの場所（v1.17.0）。
/// パッケージ版では %APPDATA% / %LOCALAPPDATA% への書き込みが <c>%LOCALAPPDATA%\Packages\&lt;パッケージファミリ名&gt;\LocalCache\{Roaming|Local}</c> へ振り替えられる。
/// アプリ自身には通常のパスで見えるが、既定のブラウザー・エクスプローラーなどパッケージの外のプロセスには見えないため、
/// 実際の場所をそのまま使って、アプリも外部ツールも同じパスで扱えるようにする。
/// パッケージファミリ名は Windows の API で取得し、固定の文字列にしない。通常版（パッケージなし）は従来の保存場所のまま。
/// </summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? familyName);

    /// <summary>パッケージとして起動しているときのパッケージファミリ名。通常の起動なら null。</summary>
    public static string? GetFamilyName()
    {
        try
        {
            uint length = 0;
            var rc = GetCurrentPackageFamilyName(ref length, null);
            if (rc == AppModelErrorNoPackage || length == 0) return null;
            if (rc != ErrorInsufficientBuffer && rc != 0) return null;
            var sb = new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length, sb) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // パッケージの API が無い環境（通常版として動かす）
        }
    }

    /// <summary>
    /// 起動中の保存場所を決める。パッケージ版は実際の保存先（<c>…\Packages\&lt;PFN&gt;\LocalCache\…</c>）、通常版は従来の場所。
    /// </summary>
    public static AppPaths CreatePaths()
    {
        var family = GetFamilyName();
        return family is null ? new AppPaths() : ForPackage(family, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>パッケージ版の保存場所（<paramref name="localAppData"/> は実際の %LOCALAPPDATA%）。</summary>
    public static AppPaths ForPackage(string family, string localAppData)
    {
        var cache = Path.Combine(localAppData, "Packages", family, "LocalCache");
        return new AppPaths(Path.Combine(cache, "Roaming", AppPaths.AppName), Path.Combine(cache, "Local", AppPaths.AppName), family);
    }

    /// <summary>
    /// パッケージ版で保存場所を実際の場所に切り替えたとき、通常のパスから見えていた設定を新しい場所へ引き継ぐ
    /// （新しい場所に無いファイルだけをコピーする。既にあるファイルは上書きしない。元は消さない）。コピーしたファイル名を返す。
    /// 通常版のデータがパッケージ側に読み取りだけ見えていた場合（振り替え前の読み取りの透過）にも設定が消えない。
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacySettings(AppPaths paths, AppPaths? legacyPaths = null)
    {
        var copied = new List<string>();
        if (!paths.IsPackaged) return copied;
        var legacy = legacyPaths ?? new AppPaths(); // 通常のパス（パッケージの内側からは振り替え後も含めて見える）
        foreach (var (from, to) in new[]
                 {
                     (legacy.SettingsFile, paths.SettingsFile),
                     (legacy.PromptTemplatesFile, paths.PromptTemplatesFile),
                 })
        {
            try
            {
                if (File.Exists(to) || !File.Exists(from)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: false);
                copied.Add(Path.GetFileName(to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return copied;
    }
}
