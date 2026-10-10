using System.Globalization;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Naming;

namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// プロジェクトフォルダー（v1.4）。保存先のルート（既定: ビデオ\ManualVideoStudio）の直下に「日時_名前」のフォルダーを作り、
/// 元動画・プロジェクトファイル・書き出した動画 / GIF を置き、手順書画像は配下の images に置く。
/// 元動画はフォルダーへ移動する（コピーしない）。ルート直下のフォルダーにある動画は、すでにプロジェクトフォルダーにあるものとして移動しない。
/// v1.7: 同じ録画から作る 2 つ目以降の編集は、録画のフォルダーの edits\「日時_名前」に置く（元動画は 1 つだけ）。
/// </summary>
public static partial class ProjectFolders
{
    /// <summary>手順書画像のフォルダー名（プロジェクトフォルダーの配下）。</summary>
    public const string ImagesFolderName = "images";
    /// <summary>2 つ目以降の編集を置くフォルダー名（録画のフォルダーの配下。v1.7）。</summary>
    public const string EditsFolderName = "edits";
    /// <summary>動画として扱う拡張子（録画のフォルダーから元動画を探すとき）。</summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv"];
    /// <summary>フォルダー名に使う名前の最大文字数。</summary>
    public const int MaxNameLength = 60;

    /// <summary>「yyyyMMdd_HHmm_名前」。名前が空（サニタイズ後を含む）なら「yyyyMMdd_HHmm」。</summary>
    public static string FolderName(DateTime time, string? name)
    {
        var stamp = time.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        var safe = FilenameSanitizer.Sanitize(FirstLine(name), MaxNameLength);
        return safe.Length == 0 ? stamp : $"{stamp}_{safe}";
    }

    /// <summary>動画がルート直下のフォルダー（＝プロジェクトフォルダー）にあるか。</summary>
    public static bool IsInProjectFolder(string videoPath, string root)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        var parent = dir is null ? null : Path.GetDirectoryName(dir);
        return parent is not null && SamePath(parent, root);
    }

    /// <summary>ルート直下に新しいプロジェクトフォルダーを作る（同名があれば「_2」「_3」… を付ける）。作ったフォルダーのパス。</summary>
    public static string CreateFolder(string root, DateTime time, string? name)
    {
        Directory.CreateDirectory(root);
        var baseName = FolderName(time, name);
        var path = Path.Combine(root, baseName);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
            path = Path.Combine(root, $"{baseName}_{i}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 動画を新しいプロジェクトフォルダーへ移動し、移動後のパスを返す。別ドライブの場合はコピーしてから元を消す（File.Move の動作）。
    /// 失敗したら作ったフォルダーを消し、動画は元の場所に残す。
    /// </summary>
    public static string MoveIntoNewFolder(string videoPath, string root, DateTime time, string? name)
    {
        var source = Path.GetFullPath(videoPath);
        var folder = CreateFolder(root, time, name);
        var dest = Path.Combine(folder, Path.GetFileName(source));
        try
        {
            File.Move(source, dest);
        }
        catch
        {
            TryDeleteEmpty(folder);
            throw;
        }
        return dest;
    }

    /// <summary>
    /// 手順書画像の既定の出力先（書き出しの既定のフォルダーの images）。
    /// 編集のフォルダー（edits\…）のプロジェクトならそのフォルダー、それ以外は動画と同じフォルダー（v1.7）。
    /// </summary>
    public static string ImagesDirectory(string videoPath, string? projectPath = null)
        => Path.Combine(DefaultOutputFolder(videoPath, projectPath), ImagesFolderName);

    /// <summary>
    /// 書き出し（動画・GIF・画像）の既定のフォルダー（v1.7）。プロジェクトファイルが動画のフォルダーの edits の配下にあれば
    /// プロジェクトファイルのフォルダー（編集ごとに分ける）、それ以外は従来どおり動画と同じフォルダー。
    /// </summary>
    public static string DefaultOutputFolder(string videoPath, string? projectPath)
    {
        var videoDir = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        return IsEditFolderProject(videoPath, projectPath) ? Path.GetDirectoryName(Path.GetFullPath(projectPath!))! : videoDir;
    }

    /// <summary>書き出しの既定のファイル名の元（v1.7）。編集のフォルダーのプロジェクトならプロジェクトファイル名、それ以外は動画のファイル名（拡張子なし）。</summary>
    public static string DefaultOutputBaseName(string videoPath, string? projectPath)
        => IsEditFolderProject(videoPath, projectPath) ? ProjectFileBaseName(projectPath!) : Path.GetFileNameWithoutExtension(videoPath);

    /// <summary>プロジェクトファイルが「動画のフォルダー\edits\…」にあるか。</summary>
    public static bool IsEditFolderProject(string videoPath, string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(videoPath)) return false;
        var edits = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, EditsFolderName);
        return IsUnder(projectPath, edits);
    }

    /// <summary>プロジェクトファイル名から拡張子（.mvsproj.json / .json）を除いたもの。</summary>
    public static string ProjectFileBaseName(string projectPath)
    {
        var name = Path.GetFileName(projectPath);
        foreach (var ext in new[] { ProjectService.Extension, ".json" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>フォルダー名・ファイル名（日時_名前）から、一覧に出す名前（先頭の日時を除いたもの）を作る。名前が無ければ日時を読みやすくして返す。</summary>
    public static string DisplayName(string folderOrBaseName)
    {
        var m = StampPattern().Match(folderOrBaseName);
        if (!m.Success) return folderOrBaseName;
        var rest = folderOrBaseName[m.Length..];
        if (rest.Length > 0) return rest;
        return TryParseStamp(folderOrBaseName) is { } t ? t.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture) : folderOrBaseName;
    }

    /// <summary>先頭の「yyyyMMdd_HHmm」を日時として読む。無ければ null。</summary>
    public static DateTime? TryParseStamp(string folderOrBaseName)
    {
        var m = StampPattern().Match(folderOrBaseName);
        if (!m.Success) return null;
        return DateTime.TryParseExact(folderOrBaseName[..13], "yyyyMMdd_HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    [GeneratedRegex(@"^\d{8}_\d{4}(_|$)")]
    private static partial Regex StampPattern();

    /// <summary>path が保存先ルート直下のどのフォルダー（録画のフォルダー）の中にあるか。ルートの外・ルート直下のファイルなら null。</summary>
    public static string? RecordingFolderOf(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!IsUnder(full, rootFull)) return null;
        var rel = Path.GetRelativePath(rootFull, full);
        var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var folder = Path.Combine(rootFull, first);
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>path が folder の中（folder 自身は含まない）にあるか。大文字小文字・末尾の区切りを無視する。</summary>
    public static bool IsUnder(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p.Length > f.Length;
    }

    /// <summary>録画のフォルダーの元動画（直下の動画ファイル。複数あれば名前順の最初）。無ければ null。</summary>
    public static string? FindVideo(string recordingFolder)
    {
        if (!Directory.Exists(recordingFolder)) return null;
        return Directory.EnumerateFiles(recordingFolder)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_edited", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>録画のフォルダーにある編集のプロジェクトファイル（直下 → edits\各フォルダー直下の順）。</summary>
    public static IReadOnlyList<string> ProjectFilesIn(string recordingFolder)
    {
        var list = new List<string>();
        if (!Directory.Exists(recordingFolder)) return list;
        list.AddRange(Directory.EnumerateFiles(recordingFolder, "*" + ProjectService.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        var edits = Path.Combine(recordingFolder, EditsFolderName);
        if (Directory.Exists(edits))
        {
            foreach (var dir in Directory.EnumerateDirectories(edits).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                list.AddRange(Directory.EnumerateFiles(dir, "*" + ProjectService.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        return list;
    }

    /// <summary>
    /// 未保存の編集を尋ねずに保存できる場所（v1.10。カットを確定して字幕編集へ進むとき）。
    /// 動画がプロジェクトフォルダーにあり、そこに編集のプロジェクトファイルがまだ無ければ「フォルダー名.mvsproj.json」
    /// （録画から新しい編集を作るときと同じ場所）。それ以外は null（保存先を尋ねる）。
    /// </summary>
    public static string? FirstSavePath(string videoPath, string root)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !IsInProjectFolder(videoPath, root)) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        if (ProjectFilesIn(folder).Count > 0) return null;
        var path = Path.Combine(folder, Path.GetFileName(folder) + ProjectService.Extension);
        return File.Exists(path) ? null : path;
    }

    /// <summary>録画のフォルダーの edits に、新しい編集のフォルダー（日時_名前）を作る（v1.7）。</summary>
    public static string CreateEditFolder(string recordingFolder, DateTime time, string? name)
        => CreateFolder(Path.Combine(recordingFolder, EditsFolderName), time, name);

    private static string FirstLine(string? text)
        => (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static bool SamePath(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteEmpty(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
