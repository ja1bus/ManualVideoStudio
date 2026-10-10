using System.Text.Json;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Persistence;

public sealed record ProjectLoadResult(ProjectDocument Project, bool VideoFound, string? Warning);

public interface IProjectService
{
    void Save(ProjectDocument project, string path);
    ProjectLoadResult Load(string path);
}

/// <summary>プロジェクトファイル（UTF-8 / 整形 JSON / 拡張子 .mvsproj.json）の保存・読み込み。</summary>
public sealed class ProjectService(ILogger<ProjectService>? logger = null) : IProjectService
{
    public const string Extension = ".mvsproj.json";
    public const string FileFilter = "Manual Video Studio プロジェクト (*.mvsproj.json)|*.mvsproj.json|JSON (*.json)|*.json";

    private readonly ILogger _logger = logger ?? NullLogger<ProjectService>.Instance;

    public static string AppVersion { get; set; } = "1.0.0";

    /// <summary>拡張子を補う。</summary>
    public static string NormalizePath(string path)
        => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path
            : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path
            : path + Extension;

    public void Save(ProjectDocument project, string path)
    {
        project.AppVersion = AppVersion;
        project.SchemaVersion = ProjectDocument.CurrentSchemaVersion;
        project.UpdatedAt = DateTimeOffset.Now;
        project.SourceVideo.RelativePath = RelativeOrNull(Path.GetDirectoryName(Path.GetFullPath(path))!, project.SourceVideo.Path);
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(project, JsonDefaults.Options));
            _logger.LogInformation("Project saved: {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Project save failed: {Path}", path);
            var file = $"{Environment.NewLine}ファイル: {Path.GetFullPath(path)}";
            throw new InvalidOperationException(AtomicFile.IsInUse(ex)
                ? $"プロジェクトを保存できませんでした。ほかのアプリ（ウイルス対策ソフトなど）がファイルを使用中です。少し待ってから、もう一度保存してください。{file}"
                : $"プロジェクトを保存できませんでした: {ex.Message}{file}", ex);
        }
    }

    public ProjectLoadResult Load(string path)
    {
        ProjectDocument? project;
        try
        {
            project = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Project load failed: {Path}", path);
            throw new InvalidOperationException(FileReadProblem.Message("プロジェクトを読み込めませんでした。", path), ex);
        }
        if (project is null) throw new InvalidOperationException(FileReadProblem.Message("プロジェクトファイルが空です。", path));
        if (project.SchemaVersion > ProjectDocument.CurrentSchemaVersion)
            throw new InvalidOperationException($"新しいバージョンのアプリで作成されたプロジェクトです（形式 v{project.SchemaVersion}）。");

        // null になり得るコレクションを補う（手編集・旧形式対策）。
        project.CutSegments ??= [];
        project.CutCandidates ??= [];
        project.CapturePoints ??= [];
        project.SceneChanges ??= [];
        project.AiHistory ??= [];
        project.ExportSettings ??= new ExportSettings();
        project.SourceVideo ??= new SourceVideo();
        foreach (var p in project.CapturePoints)
        {
            // schemaVersion 1（注釈機能の追加前）は annotations が無い。null・不正な要素も除いて空として扱う。
            p.Annotations ??= [];
            p.Annotations.RemoveAll(a => a is null);
            if (p.Annotations.Count == 0 && (p.AnnotationImageWidth <= 0 || p.AnnotationImageHeight <= 0))
            {
                p.AnnotationImageWidth = 0;
                p.AnnotationImageHeight = 0;
            }
            // schemaVersion 6 以前は手順書の操作文・補足・章が無い（空として扱う。v1.9）。
            p.StepText = CaptureText.Line(p.StepText);
            p.Note = CaptureText.Note(p.Note);
            p.ChapterTitle = CaptureText.Line(p.ChapterTitle);
        }
        // schemaVersion 2 以前は字幕・編集段階・GIF 設定が無い。null・不正な字幕も除く。
        project.SubtitleSegments ??= [];
        project.SubtitleSegments.RemoveAll(s => s is null || s.End <= s.Start || string.IsNullOrWhiteSpace(s.Text));
        foreach (var s in project.SubtitleSegments)
        {
            s.Id = string.IsNullOrEmpty(s.Id) ? CutSegment.NewId() : s.Id;
            s.FontSize = SubtitleStyle.NormalizeFontSize(s.FontSize);
            if (!Enum.IsDefined(s.Position)) s.Position = SubtitlePosition.Bottom;
            // schemaVersion 4 以前は上下のずらしが無い（0 = ずらさない）。
            s.VerticalOffset = SubtitleStyle.NormalizeOffset(s.VerticalOffset);
        }
        project.SubtitleSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
        // schemaVersion 5 以前は図形が無い。null・図形の無いまとまり・時刻の不正なまとまりは除く。
        project.ShapeGroups ??= [];
        project.ShapeGroups.RemoveAll(g => g is null || g.End <= g.Start);
        foreach (var g in project.ShapeGroups)
        {
            g.Id = string.IsNullOrEmpty(g.Id) ? CutSegment.NewId() : g.Id;
            g.Shapes ??= [];
            g.Shapes.RemoveAll(a => a is null);
        }
        project.ShapeGroups.RemoveAll(g => g.Shapes.Count == 0 || g.ImageWidth <= 0 || g.ImageHeight <= 0);
        project.ShapeGroups.Sort((a, b) => a.Start.CompareTo(b.Start));
        // schemaVersion 4 以前の subtitleStyle（見た目・フォント）は読み飛ばす。v1.5 からアプリ設定の字幕タブの値で描く。
        project.ExportGifSettings ??= new GifExportSettings();
        // schemaVersion 7 以前はタイトルが無い（タイトルなし）。秒数は 1 秒以上の整数へそろえる。
        project.Title ??= new VideoTitle();
        project.Title.Text ??= "";
        project.Title.Seconds = VideoTitle.NormalizeSeconds(project.Title.Seconds);
        project.Title.FontFamily = VideoTitle.NormalizeFontFamily(project.Title.FontFamily);
        project.Title.FontSize = VideoTitle.NormalizeFontSize(project.Title.FontSize);
        if (!Enum.IsDefined(project.EditPhase)) project.EditPhase = EditPhase.Cut;
        // 字幕があるのにカット編集中（手編集など）は、カットと字幕の時間軸がずれないよう字幕編集中として扱う。
        if (project.SubtitleSegments.Count > 0 || project.ShapeGroups.Count > 0) project.EditPhase = EditPhase.Subtitle;

        if (project.CapturePoints.Count > 0)
            project.NextCaptureId = Math.Max(project.NextCaptureId, project.CapturePoints.Max(p => p.Id) + 1);

        var found = ResolveVideo(project, Path.GetDirectoryName(Path.GetFullPath(path))!);
        // v1.3 までの画像の既定の出力先は「元ファイル名_images」。そのフォルダーがある旧プロジェクトは同じ場所を使い続ける。
        if (project.SchemaVersion <= 3 && string.IsNullOrWhiteSpace(project.ExportSettings.ImageOutputDirectory) && found)
        {
            var legacy = LegacyImagesDirectory(project.SourceVideo.Path);
            if (Directory.Exists(legacy)) project.ExportSettings.ImageOutputDirectory = legacy;
        }
        string? warning = found ? CheckVideoIdentity(project, project.SourceVideo.Path) : null;
        return new ProjectLoadResult(project, found, warning);
    }

    /// <summary>絶対パス → プロジェクトからの相対パスの順に元動画を探し、見つかったら Path を更新する。</summary>
    public static bool ResolveVideo(ProjectDocument project, string projectDirectory)
    {
        var sv = project.SourceVideo;
        if (!string.IsNullOrEmpty(sv.Path) && File.Exists(sv.Path)) return true;
        if (!string.IsNullOrEmpty(sv.RelativePath))
        {
            var candidate = Path.GetFullPath(Path.Combine(projectDirectory, sv.RelativePath));
            if (File.Exists(candidate))
            {
                sv.Path = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>動画を再指定する。サイズが記録と違う場合は警告文を返す（別の動画の可能性）。</summary>
    public static string? Relink(ProjectDocument project, string newVideoPath)
    {
        var warning = CheckVideoIdentity(project, newVideoPath);
        project.SourceVideo.Path = Path.GetFullPath(newVideoPath);
        var fi = new FileInfo(newVideoPath);
        project.SourceVideo.FileSize = fi.Length;
        project.SourceVideo.LastWriteTime = fi.LastWriteTime;
        return warning;
    }

    /// <summary>v1.3 までの画像の既定の出力先（元動画の隣の「元ファイル名_images」）。</summary>
    public static string LegacyImagesDirectory(string videoPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(videoPath))!, Path.GetFileNameWithoutExtension(videoPath) + "_images");

    private static string? CheckVideoIdentity(ProjectDocument project, string videoPath)
    {
        var expected = project.SourceVideo.FileSize;
        if (expected <= 0 || !File.Exists(videoPath)) return null;
        var actual = new FileInfo(videoPath).Length;
        return actual == expected ? null
            : $"動画のファイルサイズが保存時と異なります（保存時 {expected:N0} バイト / 現在 {actual:N0} バイト）。別の動画の場合、時刻がずれる可能性があります。";
    }

    private static string? RelativeOrNull(string baseDir, string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var rel = Path.GetRelativePath(baseDir, target);
        return Path.IsPathRooted(rel) ? null : rel;
    }
}
