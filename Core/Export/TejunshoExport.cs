using System.Globalization;
using System.Text.Json;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 手順書用の書き出し（v1.8。tejunsho-gui-docx のワークフロー V への受け渡し）の規則。
/// 出力先の下に images（画像）・capture-metadata.json・meta.json（スキルの meta.json の下書き）を置く。
/// </summary>
public static class TejunshoExport
{
    public const string FolderName = "tejunsho";
    public const string ImagesFolderName = "images";
    public const string MetaFileName = "meta.json";

    /// <summary>出力先。未設定なら書き出しの既定のフォルダー（2 つ目以降の編集は編集のフォルダー）の tejunsho。</summary>
    public static string DefaultDirectory(string videoPath, ExportSettings settings, string? projectPath = null)
        => string.IsNullOrWhiteSpace(settings.TejunshoOutputDirectory)
            ? Path.Combine(ProjectFolders.DefaultOutputFolder(videoPath, projectPath), FolderName)
            : settings.TejunshoOutputDirectory;

    /// <summary>画像の出力先（出力先の images）。</summary>
    public static string ImagesDirectory(string directory) => Path.Combine(directory, ImagesFolderName);

    /// <summary>
    /// 手順書の題名にするプロジェクト名。2 つ目以降の編集は編集のフォルダー名、録画のフォルダー（日時_名前）の動画は
    /// フォルダー名から先頭の日時を除いたもの、それ以外（旧形式）は動画のファイル名（拡張子なし）。
    /// </summary>
    public static string ProjectName(string videoPath, string? projectPath)
    {
        if (ProjectFolders.IsEditFolderProject(videoPath, projectPath))
            return ProjectFolders.DisplayName(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(projectPath!)))!);
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(videoPath)));
        if (!string.IsNullOrEmpty(folder) && ProjectFolders.TryParseStamp(folder) is not null) return ProjectFolders.DisplayName(folder);
        return Path.GetFileNameWithoutExtension(videoPath);
    }

    /// <summary>tejunsho-gui-docx の meta.json の下書き（版数 第1.0版・改訂履歴 1 行）。</summary>
    public static string BuildMetaJson(string title, DateTime date)
    {
        var doc = new TejunshoMeta
        {
            Title = title,
            Date = date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture),
            Revisions = [["1.0", date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), "", "初版作成"]],
        };
        return JsonSerializer.Serialize(doc, JsonDefaults.Options);
    }

    /// <summary>meta.json が無ければ下書きを書く（利用者・スキルが直した内容を守るため、既にあれば書かない）。書いたら true。</summary>
    public static bool WriteMetaIfMissing(string directory, string title, DateTime date)
    {
        var path = Path.Combine(directory, MetaFileName);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, BuildMetaJson(title, date), new System.Text.UTF8Encoding(false));
        return true;
    }

    /// <summary>Claude Code へ貼り付ける手順書作成の依頼文（既定のテンプレート）。</summary>
    public static string RequestText(string directory, string projectName)
        => RenderRequest(Ai.DefaultPromptTemplates.TejunshoRequest, directory, projectName, 0).Text;

    /// <summary>手順書作成の依頼文をテンプレート（設定→プロンプト。v1.9）から作る。未知の変数は残して警告を返す。</summary>
    public static Ai.PromptRenderResult RenderRequest(string template, string directory, string projectName, int imageCount)
        => Ai.PromptRenderer.Render(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Ai.PromptVariables.OutputDir] = directory,
            [Ai.PromptVariables.OutputDirHome] = PortablePath.FromHome(directory),
            [Ai.PromptVariables.ProjectName] = projectName,
            [Ai.PromptVariables.ImageCount] = imageCount.ToString(CultureInfo.InvariantCulture),
            [Ai.PromptVariables.Date] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
}

/// <summary>tejunsho-gui-docx の meta.json（キーの並びはスキルの説明と同じ）。</summary>
public sealed class TejunshoMeta
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Version { get; set; } = "第1.0版";
    public string Date { get; set; } = "";
    public string Org { get; set; } = "";
    public List<string[]> Revisions { get; set; } = [];
}
