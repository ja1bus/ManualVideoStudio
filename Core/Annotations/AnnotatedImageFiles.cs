using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Annotations;

/// <summary>
/// 注釈なしの画像（元動画から切り出したフレーム）に注釈を焼き込み、出力画像へ上書き保存する。
/// 実装は描画ライブラリ側（ManualVideoStudio.Imaging / WPF）。書き込みは一時ファイル経由で、成功時だけ置き換える。
/// </summary>
public interface IAnnotatedImageRenderer
{
    Task RenderToFileAsync(string baseImagePath, IReadOnlyList<Annotation> annotations, int annotationWidth, int annotationHeight,
        string outputPath, ImageFormat format, int jpegQuality, CancellationToken ct);
}

/// <summary>注釈編集の対象ファイル（書き出し済みの手順書画像）に関する規則。</summary>
public static class AnnotatedImageFiles
{
    /// <summary>
    /// CapturePoint の書き出し済み画像のフルパス。未出力（OutputFilename が空）なら null。
    /// 出力フォルダーはプロジェクトの画像出力先（未設定なら元動画と同じフォルダーの「images」。v1.3 までは「元ファイル名_images」）。
    /// </summary>
    public static string? OutputPath(CapturePoint point, string videoPath, ExportSettings settings, string? projectPath = null)
        => string.IsNullOrWhiteSpace(point.OutputFilename) || string.IsNullOrEmpty(videoPath)
            ? null
            : Path.Combine(ImageExportService.DefaultDirectory(videoPath, settings, projectPath), point.OutputFilename);

    /// <summary>拡張子から画像形式を決める（保存時に元の形式を維持するため）。PNG / JPEG 以外は null。</summary>
    public static ImageFormat? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        _ => null,
    };

    /// <summary>ffmpeg の JPEG 品質（-q:v 2=高画質～31）を 0～100 の品質値へ換算する（2 → 95、4 → 90、10 → 75）。</summary>
    public static int JpegQualityLevel(int ffmpegQuality) => Math.Clamp((int)Math.Round(100 - ffmpegQuality * 2.5), 50, 95);

    /// <summary>一時ファイル名。出力フォルダーに置くが、連番画像（001_… .png）の判定には一致しない名前にする。</summary>
    public static string TempPathFor(string outputPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $"~mvs-{Guid.NewGuid():N}.tmp");
}
