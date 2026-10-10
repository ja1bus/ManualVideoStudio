using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Core.Export;

/// <summary>
/// 書き出しで ffmpeg に渡すファイル（焼き込み用の字幕 ASS・図形の画像）を置く一時フォルダー。ffmpeg の作業フォルダーにして、
/// ファイル名だけで渡す（subtitles フィルタに C: の「:」や「\」を含むパスを渡すとエスケープが必要になるため。開発メモ参照）。
/// 図形の画像は拡張子を付けない（.png は作成と同時に暗号化される環境があるため。-f png_pipe で形式を明示して読む。v1.6）。
/// 字幕も図形も無い場合は Directory = null で、作業フォルダーも作らない。Dispose で削除する。
/// </summary>
public sealed class ExportWorkspace : IDisposable
{
    public const string SubtitleFileName = "subtitles.ass";
    public const string ShapeFilePrefix = "shape-";
    /// <summary>タイトルの画像のファイル名（拡張子なし。v1.15）。</summary>
    public const string TitleFileName = "title-image";

    private ExportWorkspace(string? directory, string? subtitleFile, ShapeFilterPlan? shapes, TitleFilterPlan? title = null)
    {
        Directory = directory;
        SubtitleFile = subtitleFile;
        Shapes = shapes;
        Title = title;
    }

    /// <summary>ffmpeg の作業フォルダー（字幕・図形が無ければ null）。</summary>
    public string? Directory { get; }
    /// <summary>作業フォルダー内の字幕ファイル名（字幕が無ければ null）。</summary>
    public string? SubtitleFile { get; }
    /// <summary>図形の重ね合わせ（図形が無ければ null）。画像は作業フォルダー内のファイル名。</summary>
    public ShapeFilterPlan? Shapes { get; }
    /// <summary>先頭に付ける黒背景タイトル（無ければ null。v1.15）。画像は作業フォルダー内のファイル名。</summary>
    public TitleFilterPlan? Title { get; }

    public static async Task<ExportWorkspace> CreateAsync(IReadOnlyList<SubtitleSegment>? subtitles, SubtitleStyle? style,
        int videoWidth, int videoHeight, ISubtitleTextMeasurer? measurer, CancellationToken ct, VideoShapeLayers? shapes = null, VideoTitleImage? title = null, double fps = 0)
    {
        bool hasSubtitles = subtitles is { Count: > 0 };
        bool hasShapes = shapes is { IsEmpty: false };
        if (!hasSubtitles && !hasShapes && title is null) return new ExportWorkspace(null, null, null);
        var dir = Path.Combine(Path.GetTempPath(), $"manualvideostudio-sub-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string? subtitleFile = null;
            if (hasSubtitles)
            {
                var ass = AssSubtitleBuilder.Build(subtitles!, style ?? new SubtitleStyle(), videoWidth, videoHeight, measurer);
                await File.WriteAllTextAsync(Path.Combine(dir, SubtitleFileName), ass, new UTF8Encoding(true), ct);
                subtitleFile = SubtitleFileName;
            }
            ShapeFilterPlan? plan = null;
            if (hasShapes)
            {
                var inputs = new List<ShapeOverlayInput>();
                for (int i = 0; i < shapes!.Images.Count; i++)
                {
                    var img = shapes.Images[i];
                    var name = ShapeFilePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await File.WriteAllBytesAsync(Path.Combine(dir, name), img.Png, ct);
                    inputs.Add(new ShapeOverlayInput(name, img.Start, img.End, img.X, img.Y));
                }
                plan = new ShapeFilterPlan(inputs, shapes.Mosaics);
            }
            TitleFilterPlan? titlePlan = null;
            if (title is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, TitleFileName), title.Png, ct);
                titlePlan = new TitleFilterPlan(TitleFileName, title.Seconds, title.FadeSeconds, fps);
            }
            return new ExportWorkspace(dir, subtitleFile, plan, titlePlan);
        }
        catch
        {
            TryDeleteDirectory(dir);
            throw;
        }
    }

    public void Dispose()
    {
        if (Directory is not null) TryDeleteDirectory(Directory);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
