using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Export;

public sealed record ImageExportItem(CapturePoint Point, string FileName, string FullPath, bool Exists);

public sealed record ImageExportPlan(
    string Directory,
    ImageFormat Format,
    IReadOnlyList<ImageExportItem> Items,
    /// <summary>出力先にある、今回の出力に含まれない連番画像（前回出力の残り等）。</summary>
    IReadOnlyList<string> StaleFiles)
{
    public int ExistingCount => Items.Count(i => i.Exists);
}

public sealed record ImageExportResult(IReadOnlyDictionary<int, string> FileNames, int Written, int Skipped, string? MetadataPath);

/// <summary>
/// 画像メタデータ JSON（形式 2、v1.8）に書く、画像以外の情報。
/// Layout は <see cref="ImageMetadataLayout"/>、MetadataDirectory はメタデータ JSON を置くフォルダー（null なら画像の出力先）。
/// </summary>
public sealed record ImageMetadataOptions(string Layout, string Project, string? MetadataDirectory = null, IReadOnlyList<SubtitleSegment>? Subtitles = null);

public static class ImageMetadataLayout
{
    /// <summary>通常の画像の書き出し（画像と同じフォルダーにメタデータ）。</summary>
    public const string Images = "images";
    /// <summary>手順書用の書き出し（メタデータの下の images に画像）。</summary>
    public const string Tejunsho = "tejunsho";
}

/// <summary>
/// 手順書画像（連番_キャプション）とメタデータ JSON を書き出す。
/// 注釈のある画像は、切り出したフレームに注釈（モザイク含む）を焼き込んでから保存する（renderer が無い場合は焼き込めないため失敗させる）。
/// </summary>
public sealed partial class ImageExportService(IFrameExtractionService frames, IAnnotatedImageRenderer? renderer = null)
{
    public const string MetadataFileName = "capture-metadata.json";
    /// <summary>画像メタデータ JSON の形式（v1.8 から 2。キーの追加のみで、形式 1 のキーは変えない）。</summary>
    public const int MetadataFormatVersion = 2;

    /// <summary>画像の出力先。未設定なら書き出しの既定のフォルダー（編集のフォルダーのプロジェクトはそのフォルダー。v1.7）の images。</summary>
    public static string DefaultDirectory(string videoPath, ExportSettings settings, string? projectPath = null)
        => string.IsNullOrWhiteSpace(settings.ImageOutputDirectory)
            ? Persistence.ProjectFolders.ImagesDirectory(videoPath, projectPath)
            : settings.ImageOutputDirectory;

    /// <summary>出力ファイル名と既存ファイルの有無を計算する（ファイルは書かない）。</summary>
    public static ImageExportPlan Plan(IReadOnlyList<CapturePoint> points, string directory, ImageFormat format)
    {
        var ordered = points.OrderBy(p => p.Sequence).ToList();
        int digits = OutputFilenameBuilder.DigitsFor(ordered.Count);
        var items = ordered.Select(p =>
        {
            var name = OutputFilenameBuilder.Build(p.Sequence, p.Caption, format, digits);
            var full = Path.Combine(directory, name);
            return new ImageExportItem(p, name, full, File.Exists(full));
        }).ToList();

        var names = new HashSet<string>(items.Select(i => i.FileName), StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        if (System.IO.Directory.Exists(directory))
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(directory))
            {
                var n = Path.GetFileName(f);
                if (SequencedImagePattern().IsMatch(n) && !names.Contains(n)) stale.Add(n);
            }
        }
        stale.Sort(StringComparer.OrdinalIgnoreCase);
        return new ImageExportPlan(directory, format, items, stale);
    }

    public async Task<ImageExportResult> ExportAsync(string ffmpegPath, string videoPath, ImageExportPlan plan, bool overwrite,
        int jpegQuality, bool writeMetadata, IReadOnlyList<TimeRange>? keepRanges, IProgress<(int Done, int Total)>? progress,
        CancellationToken ct, ImageMetadataOptions? metadata = null)
    {
        System.IO.Directory.CreateDirectory(plan.Directory);
        var names = new Dictionary<int, string>();
        int written = 0, skipped = 0, done = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Exists && !overwrite)
            {
                skipped++;
            }
            else
            {
                if (item.Point.Annotations.Count == 0)
                    await frames.ExtractAsync(ffmpegPath, videoPath, item.Point.SelectedFrameTime, item.FullPath, plan.Format, jpegQuality, 0, ct);
                else
                    await ExportAnnotatedAsync(ffmpegPath, videoPath, item, plan.Format, jpegQuality, ct);
                written++;
            }
            names[item.Point.Id] = item.FileName;
            progress?.Report((++done, plan.Items.Count));
        }

        string? metaPath = null;
        if (writeMetadata)
        {
            metaPath = Path.Combine(metadata?.MetadataDirectory ?? plan.Directory, MetadataFileName);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var json = BuildMetadataJson(videoPath, plan, keepRanges, metadata);
            await File.WriteAllTextAsync(metaPath, json, new System.Text.UTF8Encoding(false), ct);
        }
        return new ImageExportResult(names, written, skipped, metaPath);
    }

    /// <summary>フレームを可逆 PNG で一時フォルダーへ切り出し、注釈を焼き込んで出力形式で保存する。</summary>
    private async Task ExportAnnotatedAsync(string ffmpegPath, string videoPath, ImageExportItem item, ImageFormat format, int jpegQuality, CancellationToken ct)
    {
        // モザイクを焼き込まずに出力すると機微情報が残るため、描画手段が無い場合は書き出しを止める。
        if (renderer is null) throw new InvalidOperationException($"No.{item.Point.Sequence} の注釈を画像へ反映できません（描画機能がありません）。");
        var dir = Path.Combine(Path.GetTempPath(), "ManualVideoStudio");
        System.IO.Directory.CreateDirectory(dir);
        // 切り出した元画像は読み直すため拡張子を付けない（v1.4.2。.png は置いた瞬間に暗号化される環境がある）。
        var basePath = Path.Combine(dir, $"base-{Guid.NewGuid():N}");
        try
        {
            await frames.ExtractAsync(ffmpegPath, videoPath, item.Point.SelectedFrameTime, basePath, ImageFormat.Png, 2, 0, ct);
            await renderer.RenderToFileAsync(basePath, item.Point.Annotations, item.Point.AnnotationImageWidth, item.Point.AnnotationImageHeight,
                item.FullPath, format, jpegQuality, ct);
        }
        finally
        {
            try { if (File.Exists(basePath)) File.Delete(basePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 画像メタデータ JSON（形式 2）。tejunsho-gui-docx のワークフロー V が操作メモの正として読む（通常の書き出しでは追跡用）。
    /// 操作文・補足・章（v1.9）は画像ポイントの入力から書く（空なら null / false）。
    /// </summary>
    public static string BuildMetadataJson(string videoPath, ImageExportPlan plan, IReadOnlyList<TimeRange>? keepRanges,
        ImageMetadataOptions? options = null)
    {
        var metaDir = options?.MetadataDirectory ?? plan.Directory;
        var subtitles = options?.Subtitles ?? [];
        var chapters = CaptureChapters.Resolve(plan.Items.Select(i => i.Point));
        var doc = new ImageMetadataDocument
        {
            FormatVersion = MetadataFormatVersion,
            Generator = "ManualVideoStudio " + ProjectService.AppVersion,
            Layout = options?.Layout ?? ImageMetadataLayout.Images,
            Project = options?.Project ?? "",
            Source = Path.GetFileName(videoPath),
            SourcePath = PortablePath.FromHome(videoPath),
            ExportedAt = DateTimeOffset.Now,
            Format = plan.Format == ImageFormat.Jpeg ? "jpeg" : "png",
            Captures = plan.Items.Select(i =>
            {
                var edited = keepRanges is null ? null : EditTimelineCalculator.MapToOutput(i.Point.SelectedFrameTime, keepRanges);
                return new ImageMetadataCapture
                {
                    Id = i.Point.Id,
                    Sequence = i.Point.Sequence,
                    Timestamp = Math.Round(i.Point.Timestamp, 3),
                    SelectedFrameTime = Math.Round(i.Point.SelectedFrameTime, 3),
                    EditedVideoTime = edited,
                    Caption = i.Point.Caption,
                    CaptionSource = i.Point.CaptionSource.ToString().ToLowerInvariant(),
                    File = i.FileName,
                    AnnotationCount = i.Point.Annotations.Count,
                    Path = RelativeImagePath(metaDir, i.FullPath),
                    StepText = NullIfEmpty(i.Point.StepText),
                    Note = NullIfEmpty(i.Point.Note),
                    Chapter = chapters[i.Point.Id].Chapter,
                    ChapterStart = chapters[i.Point.Id].Start,
                    Subtitles = SubtitlesAt(edited, subtitles),
                };
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, JsonDefaults.Options);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>メタデータ JSON から見た画像の相対パス（区切りは「/」。スキルの content.json にそのまま使えるように）。</summary>
    public static string RelativeImagePath(string metadataDirectory, string imagePath)
        => Path.GetRelativePath(Path.GetFullPath(metadataDirectory), Path.GetFullPath(imagePath)).Replace('\\', '/');

    /// <summary>
    /// 編集後の動画の時刻 t を含む字幕（Start ≤ t &lt; End）の文を開始の早い順に返す（v1.8。手順書の補足の候補）。
    /// t が null（カットされる位置）なら空。
    /// </summary>
    public static List<string> SubtitlesAt(double? editedTime, IReadOnlyList<SubtitleSegment> subtitles)
    {
        if (editedTime is not { } t) return [];
        return subtitles.Where(s => s.Start <= t && t < s.End && !string.IsNullOrWhiteSpace(s.Text))
            .OrderBy(s => s.Start).Select(s => s.Text.Replace("\r\n", "\n")).ToList();
    }

    /// <summary>"001_xxx.png" / "001.jpg" 形式（本アプリの出力命名）。</summary>
    [GeneratedRegex(@"^\d{3,}(_.*)?\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase)]
    private static partial Regex SequencedImagePattern();
}

public sealed class ImageMetadataDocument
{
    /// <summary>形式（v1.8 から 2。無いファイルは形式 1）。</summary>
    public int FormatVersion { get; set; } = ImageExportService.MetadataFormatVersion;
    public string Generator { get; set; } = "";
    /// <summary>"images"（通常の書き出し）/ "tejunsho"（手順書用の書き出し）。</summary>
    public string Layout { get; set; } = ImageMetadataLayout.Images;
    public string Project { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>元動画の場所（v1.10 からユーザーフォルダーの中なら「~/…」。PC ごとのユーザー名を残さない。参考情報）。</summary>
    public string SourcePath { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
    public string Format { get; set; } = "png";
    public List<ImageMetadataCapture> Captures { get; set; } = [];
}

public sealed class ImageMetadataCapture
{
    public int Id { get; set; }
    public int Sequence { get; set; }
    public double Timestamp { get; set; }
    public double SelectedFrameTime { get; set; }
    /// <summary>編集済み動画上の時刻。カットで削除される位置なら null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? EditedVideoTime { get; set; }
    public string Caption { get; set; } = "";
    public string CaptionSource { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>画像に焼き込んだ注釈の数。</summary>
    public int AnnotationCount { get; set; }
    /// <summary>メタデータ JSON から見た画像の相対パス（v1.8）。</summary>
    public string Path { get; set; } = "";
    /// <summary>手順書の操作文（v1.9。空なら null。スキルはキャプションを使う）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? StepText { get; set; }
    /// <summary>補足（確認ポイント・入力値。v1.9。空なら null）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Note { get; set; }
    /// <summary>この画像が属する章の名前（v1.9。最初の章の前の画像は null）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Chapter { get; set; }
    /// <summary>この画像から新しい章が始まるか（v1.9）。</summary>
    public bool ChapterStart { get; set; }
    /// <summary>画像の時刻に重なる字幕の文（手順書の補足の候補）。</summary>
    public List<string> Subtitles { get; set; } = [];
}
