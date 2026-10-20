using System.Text;
using System.Text.Json;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Join;

/// <summary>結合元の 1 件（再生順）。絶対パスは持たない（フォルダーを移動しても意味を保つため）。</summary>
public sealed class JoinSourceEntry
{
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public double DurationSeconds { get; set; }
}

/// <summary>
/// 結合した動画のフォルダーに置く参照用の記録（join-sources.json）。どの録画をどの順につないだかを残す。
/// 無くても結合 MP4 と編集プロジェクトは使える（結合済みの動画をさらに使うときの引き継ぎにだけ使う）。
/// </summary>
public sealed class JoinSourcesFile
{
    public const string FileName = "join-sources.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>この記録が説明する結合 MP4 のファイル名（同じフォルダー内）。</summary>
    public string OutputFileName { get; set; } = "";
    public List<JoinSourceEntry> Sources { get; set; } = [];

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public void Save(string folder)
    {
        var path = PathIn(folder);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonDefaults.Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>videoPath が結合した動画なら、その結合元の記録。記録が無い・読めない・別の動画の記録なら null。</summary>
    public static JoinSourcesFile? TryReadFor(string videoPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath));
            if (folder is null) return null;
            var path = PathIn(folder);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<JoinSourcesFile>(File.ReadAllText(path), JsonDefaults.Options);
            if (file is null || file.SchemaVersion != CurrentSchemaVersion || file.Sources.Count == 0) return null;
            return string.Equals(file.OutputFileName, Path.GetFileName(videoPath), StringComparison.OrdinalIgnoreCase) ? file : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
