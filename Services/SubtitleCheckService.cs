using System.IO;
using System.Windows.Media;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.Services;

/// <summary>確認画像に使う動画の場面（設定画面を開いたときの主画面の状態）。VideoPath が null なら灰色の画面に描く。</summary>
public sealed record SubtitleSampleContext(string? VideoPath, double SourceTime, int Width, int Height, SubtitleSegment? Segment)
{
    public static readonly SubtitleSampleContext Empty = new(null, 0, 1280, 720, null);
}

/// <summary>確認画像。Image が null なら作れなかった（Message に理由）。FontWarning は指定と違うフォントで描かれた場合の注意。</summary>
public sealed record SubtitleSampleImage(ImageSource? Image, string Message, string? FontWarning);

/// <summary>
/// 字幕の確認画像（設定画面の字幕タブ）と、書き出し前のフォントの確認（v1.5）。
/// どちらも書き出しと同じ ASS・libass で 1 コマ描き、libass が実際に選んだフォントを確かめる。
/// </summary>
public sealed class SubtitleCheckService(SubtitleSampleRenderer renderer, FFmpegStatus ffmpeg, ILogger<SubtitleCheckService> logger)
{
    public const string SampleText = "字幕の表示例です。IP アドレスと Wi-Fi の設定を確認します。";

    public bool IsAvailable => ffmpeg.FfmpegPath is not null && ffmpeg.Capabilities is not { HasSubtitles: false };

    public async Task<SubtitleSampleImage> RenderAsync(SubtitleSampleContext context, SubtitleSegment segment, SubtitleStyle style, CancellationToken ct)
    {
        if (ffmpeg.FfmpegPath is not { } exe) return new SubtitleSampleImage(null, "FFmpeg が見つからないため確認画像を作れません（［FFmpeg］タブで指定してください）。", null);
        if (ffmpeg.Capabilities is { HasSubtitles: false }) return new SubtitleSampleImage(null, "この ffmpeg には字幕の焼き込み（libass）がありません。", null);
        var video = context.VideoPath is { } v && File.Exists(v) ? v : null;
        var (w, h) = video is null || context.Width <= 0 || context.Height <= 0 ? (1280, 720) : (context.Width, context.Height);
        // 拡張子なしの一時ファイル（中身は PNG。暗号化される拡張子を避ける）。
        var dir = Path.Combine(AppTemp.Root, "work");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, $"subsample-{Guid.NewGuid():N}");
        try
        {
            var r = await renderer.RenderAsync(exe, video, context.SourceTime, w, h, segment, style, WpfSubtitleTextMeasurer.Instance, output, ct);
            if (!r.Succeeded)
            {
                logger.LogWarning("Subtitle sample failed: {Error}", r.Error);
                return new SubtitleSampleImage(null, "確認画像を作れませんでした。" + (r.Error is { Length: > 0 } e ? Environment.NewLine + e : ""), null);
            }
            ImageSource? image;
            try
            {
                image = ImageLoader.Load(output);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Subtitle sample load failed: {Path}", output);
                return new SubtitleSampleImage(null, ex.Message, null);
            }
            var where = video is null ? "灰色の画面に描いた例" : $"動画の {TimeFormat.Format(context.SourceTime)} の場面";
            return new SubtitleSampleImage(image, $"書き出しと同じ方法（libass）で描いた確認画像です（{where}）。", FontWarning(style.FontFamily, r.SelectedFont));
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Failed to delete {Path}", output); }
        }
    }

    /// <summary>書き出し前のフォントの確認。指定と違うフォントで描かれる場合は注意の文、問題が無い・確かめられない場合は null。</summary>
    public async Task<string?> CheckFontAsync(SubtitleStyle style, CancellationToken ct)
    {
        try
        {
            var r = await RenderAsync(SubtitleSampleContext.Empty, new SubtitleSegment { Text = "字幕 ABC" }, style, ct);
            return r.FontWarning;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Subtitle font check failed");
            return null;
        }
    }

    /// <summary>libass が選んだフォント（PostScript 名）が指定と違えば注意の文。</summary>
    public static string? FontWarning(string requested, string? selected)
    {
        if (selected is null) return null;
        var family = SubtitleStyle.NormalizeFontFamily(requested);
        var fonts = SubtitleFonts.Loaded;
        var choice = fonts is null ? null : FontCatalog.Find(fonts, family);
        if (SubtitleSampleRenderer.IsExpectedFont(family, selected, choice?.PostScriptNames)) return null;
        return $"フォント「{family}」が見つからず、「{selected}」で描かれます。この PC に入っているフォントを選び直してください。";
    }
}
