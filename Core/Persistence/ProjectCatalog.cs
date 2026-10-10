using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;

namespace ManualVideoStudio.Core.Persistence;

/// <summary>ファイル・フォルダーをごみ箱へ移す（アプリ側で Windows のごみ箱を使う。テストでは差し替える）。</summary>
public interface IRecycleBin
{
    /// <summary>ファイルまたはフォルダーをごみ箱へ移す。失敗したら例外。</summary>
    void MoveToRecycleBin(string path);
}

/// <summary>
/// プロジェクト一覧（v1.7）の操作のうち、ファイル・フォルダーを扱うもの:
/// 編集の複製の準備、編集の名前の変更、削除の対象の決定、フォルダーを移した後のプロジェクトファイルのパスの書き換え。
/// </summary>
public static class ProjectCatalog
{
    /// <summary>
    /// 複製する編集の内容を作る（元は変更しない）。書き出し先・書き出したファイル名は新しい編集の既定に戻す。
    /// restartFromCuts なら字幕・図形を除き、カット編集の段階から始める（字幕を付けた後にカットを直したい場合）。
    /// </summary>
    public static ProjectDocument PrepareCopy(ProjectDocument source, bool restartFromCuts, ExportSettings defaults)
    {
        var copy = JsonDefaults.Clone(source);
        copy.CreatedAt = DateTimeOffset.Now;
        copy.UpdatedAt = DateTimeOffset.Now;
        copy.ExportSettings.VideoOutputDirectory = defaults.VideoOutputDirectory;
        copy.ExportSettings.VideoOutputFileName = "";
        copy.ExportSettings.ImageOutputDirectory = defaults.ImageOutputDirectory;
        copy.ExportSettings.TejunshoOutputDirectory = "";
        copy.ExportGifSettings.OutputDirectory = "";
        copy.ExportGifSettings.OutputFileName = "";
        // 画像はまだ新しい編集のフォルダーに書き出していない。
        foreach (var p in copy.CapturePoints) p.OutputFilename = "";
        if (restartFromCuts)
        {
            copy.SubtitleSegments.Clear();
            copy.ShapeGroups.Clear();
            copy.EditPhase = EditPhase.Cut;
        }
        return copy;
    }

    /// <summary>名前の変更後のフォルダー名・ファイル名（先頭の日時は元のものを残す）。</summary>
    public static string RenamedBase(string oldBase, string newName)
    {
        var safe = FilenameSanitizer.Sanitize(FirstLine(newName), ProjectFolders.MaxNameLength);
        var stamp = ProjectFolders.TryParseStamp(oldBase) is not null ? oldBase[..13] : "";
        return stamp.Length == 0 ? safe : safe.Length == 0 ? stamp : $"{stamp}_{safe}";
    }

    /// <summary>
    /// 編集の名前を変える。edits\「日時_名前」の編集はフォルダー名とプロジェクトファイル名を、
    /// 録画のフォルダーの直下の編集はプロジェクトファイル名を変える。新しいプロジェクトファイルのパスを返す。
    /// 同じ名前がある・名前が空なら例外（InvalidOperationException）。
    /// </summary>
    public static string RenameEdit(string projectPath, string newName)
    {
        projectPath = Path.GetFullPath(projectPath);
        var dir = Path.GetDirectoryName(projectPath)!;
        var parent = Path.GetDirectoryName(dir);
        bool inEdits = parent is not null && string.Equals(Path.GetFileName(parent), ProjectFolders.EditsFolderName, StringComparison.OrdinalIgnoreCase);
        var fileBase = ProjectFolders.ProjectFileBaseName(projectPath);
        if (!inEdits)
        {
            var newBase = RenamedBase(fileBase, newName);
            if (newBase.Length == 0) throw new InvalidOperationException("名前を入力してください。");
            var dest = Path.Combine(dir, newBase + ProjectService.Extension);
            if (string.Equals(dest, projectPath, StringComparison.OrdinalIgnoreCase)) return projectPath;
            if (File.Exists(dest)) throw new InvalidOperationException($"同じ名前の編集があります。{Environment.NewLine}{dest}");
            File.Move(projectPath, dest);
            return dest;
        }

        var folderName = Path.GetFileName(dir);
        var newFolderName = RenamedBase(folderName, newName);
        if (newFolderName.Length == 0) throw new InvalidOperationException("名前を入力してください。");
        var newDir = Path.Combine(parent!, newFolderName);
        if (string.Equals(newDir, dir, StringComparison.OrdinalIgnoreCase)) return projectPath;
        if (Directory.Exists(newDir) || File.Exists(newDir)) throw new InvalidOperationException($"同じ名前の編集があります。{Environment.NewLine}{newDir}");
        Directory.Move(dir, newDir);
        var file = Path.Combine(newDir, Path.GetFileName(projectPath));
        // プロジェクトファイル名がフォルダー名と同じ（作ったときのまま）なら、ファイル名も合わせる。
        if (string.Equals(fileBase, folderName, StringComparison.OrdinalIgnoreCase))
        {
            var renamed = Path.Combine(newDir, newFolderName + ProjectService.Extension);
            File.Move(file, renamed);
            file = renamed;
        }
        Relocate(file, dir, newDir);
        return file;
    }

    /// <summary>
    /// 編集を削除するときにごみ箱へ移すもの。edits\ の編集はそのフォルダー（書き出したファイルを含む）、
    /// 録画のフォルダーの直下の編集はプロジェクトファイルだけ（元動画・書き出したファイルは録画のフォルダーに残る）。
    /// </summary>
    public static string EditDeletionTarget(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        var dir = Path.GetDirectoryName(projectPath)!;
        var parent = Path.GetDirectoryName(dir);
        bool inEdits = parent is not null && string.Equals(Path.GetFileName(parent), ProjectFolders.EditsFolderName, StringComparison.OrdinalIgnoreCase);
        return inEdits ? dir : projectPath;
    }

    /// <summary>
    /// 削除してよい場所か（保存先ルートの中で、ルートそのものではない）。
    /// 一覧の管理ファイルが壊れていても、ルートの外やルート自体を消さないための確認。
    /// </summary>
    public static bool IsSafeToDelete(string path, string root) => ProjectFolders.IsUnder(path, root);

    /// <summary>
    /// フォルダーを移した・名前を変えた後に、プロジェクトファイルのパスを書き換える（ほかの項目はそのまま残す）。
    /// ・元動画: 相対パスで見つかれば、絶対パスをその場所にする（元の場所に同じ動画が残っていても、移した先の動画を使うため）。
    /// ・書き出し先（動画・画像・GIF）: oldFolder の中を指していれば newFolder の中に置き換える。
    /// 書き換えたら true。
    /// </summary>
    public static bool Relocate(string projectFile, string? oldFolder, string newFolder)
    {
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        var docOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        if (JsonNode.Parse(File.ReadAllText(projectFile), options, docOptions) is not JsonObject root) return false;
        bool changed = false;
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        if (root["sourceVideo"] is JsonObject sv && Text(sv, "relativePath") is { Length: > 0 } rel)
        {
            var candidate = Path.GetFullPath(Path.Combine(projectDir, rel));
            if (File.Exists(candidate) && !string.Equals(Text(sv, "path"), candidate, StringComparison.OrdinalIgnoreCase))
            {
                Set(sv, "path", candidate);
                changed = true;
            }
        }
        if (!string.IsNullOrEmpty(oldFolder))
        {
            changed |= MoveDir(root["exportSettings"] as JsonObject, "videoOutputDirectory", oldFolder, newFolder);
            changed |= MoveDir(root["exportSettings"] as JsonObject, "imageOutputDirectory", oldFolder, newFolder);
            changed |= MoveDir(root["exportSettings"] as JsonObject, "tejunshoOutputDirectory", oldFolder, newFolder);
            changed |= MoveDir(root["exportGifSettings"] as JsonObject, "outputDirectory", oldFolder, newFolder);
        }
        if (changed)
        {
            var write = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            AtomicFile.WriteAllText(projectFile, root.ToJsonString(write));
        }
        return changed;
    }

    private static bool MoveDir(JsonObject? obj, string name, string oldFolder, string newFolder)
    {
        if (obj is null || Text(obj, name) is not { Length: > 0 } value) return false;
        var oldFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldFolder));
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        string? moved = null;
        if (string.Equals(full, oldFull, StringComparison.OrdinalIgnoreCase)) moved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newFolder));
        else if (ProjectFolders.IsUnder(full, oldFull)) moved = Path.Combine(Path.GetFullPath(newFolder), Path.GetRelativePath(oldFull, full));
        if (moved is null) return false;
        Set(obj, name, moved);
        return true;
    }

    private static string? Text(JsonObject obj, string name)
        => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>既存のキー（大文字小文字違いを含む）を置き換える。</summary>
    private static void Set(JsonObject obj, string name, string value)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        obj[key] = value;
    }

    private static string FirstLine(string? text)
        => (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}
