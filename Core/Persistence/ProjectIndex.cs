using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Persistence;

/// <summary>
/// プロジェクト一覧の管理ファイル（保存先ルート直下の projects.json。v1.7）。
/// 一覧を開くたびにフォルダーを調べないよう、録画のフォルダーと編集（プロジェクトファイル）を記録しておく。
/// アプリの操作（録画・保存・新しい編集・削除・インポートなど）で自動で書き換え、エクスプローラーで変えた場合は［最新の情報に更新］で作り直す。
/// </summary>
public sealed class ProjectIndex
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<IndexedRecording> Recordings { get; set; } = [];
}

/// <summary>録画 1 つ分のフォルダー（保存先ルートの直下）。</summary>
public sealed class IndexedRecording
{
    /// <summary>フォルダー名（ルートからの相対）。</summary>
    public string Folder { get; set; } = "";
    /// <summary>一覧に出す名前（フォルダー名から先頭の日時を除いたもの）。</summary>
    public string Name { get; set; } = "";
    /// <summary>録画（プロジェクトフォルダーの作成）の日時。フォルダー名の日時、無ければフォルダーの作成日時。</summary>
    public DateTime Date { get; set; }
    /// <summary>元動画のファイル名（フォルダーの直下）。見つからなければ空。</summary>
    public string Video { get; set; } = "";
    /// <summary>注意（「動画が見つかりません」など）。無ければ空。</summary>
    public string Problem { get; set; } = "";
    public List<IndexedEdit> Edits { get; set; } = [];
}

/// <summary>編集 1 つ（プロジェクトファイル）。</summary>
public sealed class IndexedEdit
{
    /// <summary>一覧に出す名前。</summary>
    public string Name { get; set; } = "";
    /// <summary>プロジェクトファイルのパス（録画のフォルダーからの相対）。</summary>
    public string Path { get; set; } = "";
    /// <summary>プロジェクトファイルの更新日時。</summary>
    public DateTime UpdatedAt { get; set; }
    /// <summary>注意（「元の動画が見つかりません」「プロジェクトファイルを読めません」）。無ければ空。</summary>
    public string Problem { get; set; } = "";
}

/// <summary>管理ファイル（projects.json）の読み書きと、フォルダーの調べ直し（v1.7）。</summary>
public sealed class ProjectIndexStore(string root, ILogger? logger = null)
{
    public const string FileName = "projects.json";
    public const string NoVideoProblem = "動画が見つかりません";
    public const string EditVideoMissingProblem = "元の動画が見つかりません";
    public const string EditUnreadableProblem = "プロジェクトファイルを読めません";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Root { get; } = Path.GetFullPath(root);
    public string IndexPath => Path.Combine(Root, FileName);

    /// <summary>管理ファイルを読む。無い・読めない場合はフォルダーを調べて作り直す（作り直した場合 rebuilt = true）。</summary>
    public ProjectIndex LoadOrRebuild(out bool rebuilt)
    {
        rebuilt = false;
        var index = TryLoad();
        if (index is not null) return index;
        rebuilt = true;
        return Rebuild();
    }

    /// <summary>管理ファイルを読む。無い・壊れている・新しい形式なら null。</summary>
    public ProjectIndex? TryLoad()
    {
        if (!File.Exists(IndexPath)) return null;
        try
        {
            var index = JsonSerializer.Deserialize<ProjectIndex>(File.ReadAllText(IndexPath), JsonDefaults.Options);
            if (index is null || index.SchemaVersion > ProjectIndex.CurrentSchemaVersion) return null;
            index.Recordings ??= [];
            index.Recordings.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Folder));
            foreach (var r in index.Recordings) r.Edits ??= [];
            return index;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Project index load failed: {Path}", IndexPath);
            return null;
        }
    }

    /// <summary>フォルダーを調べて管理ファイルを作り直す（［最新の情報に更新］）。</summary>
    public ProjectIndex Rebuild()
    {
        var index = new ProjectIndex();
        if (Directory.Exists(Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var r = ScanRecording(dir);
                if (r is not null) index.Recordings.Add(r);
            }
        }
        Sort(index);
        Save(index);
        _logger.LogInformation("Project index rebuilt: {Count} recordings in {Root}", index.Recordings.Count, Root);
        return index;
    }

    /// <summary>
    /// path（録画のフォルダー・その中のファイル）を含む録画のフォルダーだけを調べ直して管理ファイルに反映する。
    /// フォルダーが無くなっていれば一覧から除く。ルートの外なら何もしない。
    /// </summary>
    public void Touch(string path)
    {
        var folder = RecordingFolderName(path);
        if (folder is null) return;
        var index = TryLoad();
        if (index is null)
        {
            Rebuild();
            return;
        }
        index.Recordings.RemoveAll(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase));
        var scanned = ScanRecording(Path.Combine(Root, folder));
        if (scanned is not null) index.Recordings.Add(scanned);
        Sort(index);
        Save(index);
    }

    /// <summary>録画のフォルダーを一覧から除く（削除した後）。</summary>
    public void Remove(string recordingFolder)
    {
        var index = TryLoad();
        if (index is null) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(recordingFolder));
        if (index.Recordings.RemoveAll(r => string.Equals(r.Folder, name, StringComparison.OrdinalIgnoreCase)) > 0) Save(index);
    }

    /// <summary>開こうとして分かった注意を記録する（空なら消す）。</summary>
    public void SetEditProblem(string projectPath, string problem)
    {
        var folder = RecordingFolderName(projectPath);
        var index = folder is null ? null : TryLoad();
        var rec = index?.Recordings.FirstOrDefault(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (rec is null) return;
        var rel = Path.GetRelativePath(Path.Combine(Root, rec.Folder), projectPath);
        var edit = rec.Edits.FirstOrDefault(e => string.Equals(e.Path, rel, StringComparison.OrdinalIgnoreCase));
        if (edit is null || edit.Problem == problem) return;
        edit.Problem = problem;
        Save(index!);
    }

    public void Save(ProjectIndex index)
    {
        index.SchemaVersion = ProjectIndex.CurrentSchemaVersion;
        index.UpdatedAt = DateTimeOffset.Now;
        AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(index, JsonDefaults.Options));
    }

    /// <summary>録画のフォルダーのフルパス。</summary>
    public string FolderPath(IndexedRecording recording) => Path.Combine(Root, recording.Folder);

    /// <summary>編集のプロジェクトファイルのフルパス。</summary>
    public string EditPath(IndexedRecording recording, IndexedEdit edit) => Path.GetFullPath(Path.Combine(Root, recording.Folder, edit.Path));

    private string? RecordingFolderName(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = ProjectFolders.IsUnder(full, Root) ? Path.GetRelativePath(Root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] : null;
        // ルート直下のファイル（管理ファイル自身など）は録画のフォルダーではない。無くなったフォルダーは名前を返す（一覧から除くため）。
        if (folder is null || File.Exists(Path.Combine(Root, folder))) return null;
        return folder;
    }

    /// <summary>録画のフォルダーを調べる。動画もプロジェクトファイルも無い・作業用（先頭が「.」）のフォルダーは null。</summary>
    public static IndexedRecording? ScanRecording(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name.StartsWith('.') || !Directory.Exists(folder)) return null;
        var video = ProjectFolders.FindVideo(folder);
        var projects = ProjectFolders.ProjectFilesIn(folder);
        if (video is null && projects.Count == 0) return null;
        var rec = new IndexedRecording
        {
            Folder = name,
            Name = ProjectFolders.DisplayName(name),
            Date = ProjectFolders.TryParseStamp(name) ?? Directory.GetCreationTime(folder),
            Video = video is null ? "" : Path.GetFileName(video),
            Problem = video is null ? NoVideoProblem : "",
        };
        foreach (var p in projects)
        {
            var inEdits = Path.GetDirectoryName(p) is { } dir && !string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
            var baseName = inEdits ? Path.GetFileName(Path.GetDirectoryName(p)!) : ProjectFolders.ProjectFileBaseName(p);
            rec.Edits.Add(new IndexedEdit
            {
                Name = ProjectFolders.DisplayName(baseName),
                Path = Path.GetRelativePath(folder, p),
                UpdatedAt = File.GetLastWriteTime(p),
                Problem = CheckProject(p),
            });
        }
        return rec;
    }

    /// <summary>プロジェクトファイルが読めて、元動画が見つかるか（中身は元動画の場所だけ読む）。問題が無ければ空。</summary>
    public static string CheckProject(string projectPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!TryGet(doc.RootElement, "sourceVideo", out var sv)) return EditUnreadableProblem;
            var abs = TryGet(sv, "path", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var rel = TryGet(sv, "relativePath", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (!string.IsNullOrEmpty(abs) && File.Exists(abs)) return "";
            if (!string.IsNullOrEmpty(rel) && File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, rel)))) return "";
            return EditVideoMissingProblem;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return EditUnreadableProblem;
        }
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    private static void Sort(ProjectIndex index)
        => index.Recordings.Sort((a, b) => b.Date != a.Date ? b.Date.CompareTo(a.Date) : string.Compare(b.Folder, a.Folder, StringComparison.OrdinalIgnoreCase));
}
