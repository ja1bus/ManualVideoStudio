using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>
/// 黒背景タイトル（v1.15）の描画。黒い背景に白・メイリオ・太字の文字を、<see cref="TitleLayout"/> の配置で描く。
/// 編集画面の静止プレビューと書き出し用の画像は、どちらもこの 1 つの処理で作る（見た目を合わせるため）。
/// WPF の描画オブジェクトを使うため STA スレッドで呼ぶこと。
/// </summary>
public static class TitleRenderer
{
    /// <summary>指定のフォント（太字）。この PC に無ければメイリオで描く（編集画面と書き出しで同じ結果になる）。</summary>
    private static Typeface FaceOf(string? family)
        => new(new FontFamily((string.IsNullOrWhiteSpace(family) ? VideoTitle.DefaultFontFamily : family) + ", " + VideoTitle.DefaultFontFamily),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>太字・指定の文字サイズでの文字列の幅（px）。</summary>
    public static double Measure(string text, double fontSizePx, string? family = null)
        => text.Length == 0 ? 0 : Format(text, fontSizePx, family).WidthIncludingTrailingWhitespace;

    /// <summary>width × height の映像に対するタイトルの配置。</summary>
    public static TitleLayoutResult Layout(string? text, int width, int height, string? family = null, int fontSize = VideoTitle.DefaultFontSize)
        => TitleLayout.Compute(text, width, height, (s, size) => Measure(s, size, family), fontSize);

    /// <summary>タイトルの画像（不透明な黒背景）。文章が空でも黒一色を返す。</summary>
    public static BitmapSource Render(string? text, int width, int height, string? family = null, int fontSize = VideoTitle.DefaultFontSize)
    {
        var layout = Layout(text, width, height, family, fontSize);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            var lineHeight = layout.FontSizePx * TitleLayout.LineHeightRatio;
            for (int i = 0; i < layout.Lines.Count; i++)
            {
                var line = layout.Lines[i];
                if (line.Length == 0) continue;
                var ft = Format(line, layout.FontSizePx, family);
                dc.DrawText(ft, new Point((width - ft.WidthIncludingTrailingWhitespace) / 2, layout.TopPx + i * lineHeight));
            }
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>書き出し用の PNG（動画と同じ大きさ）。</summary>
    public static byte[] RenderPng(string? text, int width, int height, string? family = null, int fontSize = VideoTitle.DefaultFontSize)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(Render(text, width, height, family, fontSize), PixelFormats.Bgr24, null, 0)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 書き出しに渡すタイトルの画像。文章が収まらない・動画の大きさが不明ならエラーメッセージ（image は null）。
    /// 本編が短いときのフェードの長さの調整もここで行う。
    /// </summary>
    public static (VideoTitleImage? Image, string? Error) BuildExportImage(VideoTitle title, int width, int height, double mainDuration)
    {
        if (!title.HasTitle) return (null, null);
        if (width <= 0 || height <= 0) return (null, "動画の大きさが分からないため、タイトルを作れません。");
        var layout = Layout(title.NormalizedText, width, height, title.FontFamily, title.FontSize);
        if (TitleLayout.TooLongMessage(layout) is { } message) return (null, message);
        return (new VideoTitleImage(VideoTitle.NormalizeSeconds(title.Seconds), title.EffectiveFadeSeconds(mainDuration),
            RenderPng(title.NormalizedText, width, height, title.FontFamily, title.FontSize)), null);
    }

    private static FormattedText Format(string text, double fontSizePx, string? family)
        => new(text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, FaceOf(family), fontSizePx, Brushes.White, 1.0);
}
