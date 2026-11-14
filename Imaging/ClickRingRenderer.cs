using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ManualVideoStudio.Imaging;

/// <summary>
/// クリック位置の強調（v1.21）の輪。黄色の半透明の輪（線は不透明度 70%、中は 20% の淡い塗り）で、見た目は固定。
/// 書き出し用の PNG（<see cref="RenderPng"/>）と、プレビュー用の描画（<see cref="CreateDrawing"/> / <see cref="Draw"/>）は
/// 同じ図形から作り、見た目をそろえる。WPF の描画オブジェクトを使うため、PNG は STA スレッドで作ること。
/// </summary>
public static class ClickRingRenderer
{
    /// <summary>輪の色（#FFD60A）。</summary>
    public static readonly Color RingColor = Color.FromRgb(0xFF, 0xD6, 0x0A);
    /// <summary>線の不透明度（約 70%）。</summary>
    public const byte StrokeAlpha = 179;
    /// <summary>中の塗りの不透明度（約 20%）。</summary>
    public const byte FillAlpha = 51;

    /// <summary>線の太さ（半径の 0.18 倍。最低 1.5px）。</summary>
    public static double StrokeFor(double radius) => Math.Max(1.5, radius * 0.18);

    /// <summary>
    /// PNG の 1 辺（px）。輪の外側（半径＋線の太さ）に 1px ずつの余白を足し、中心が画素の境目に来るよう偶数にする。
    /// 半径が 0 以下なら 0。
    /// </summary>
    public static int SizeFor(double radius)
    {
        if (!(radius > 0)) return 0;
        var side = (int)Math.Ceiling(2 * (radius + StrokeFor(radius)) + 2);
        return side % 2 == 0 ? side : side + 1;
    }

    /// <summary>
    /// 中心 (0, 0) の輪の描画（Freeze 済み）。プレビューでは TranslateTransform でクリックの位置へ動かして描く。
    /// 線の中心を半径の位置に置く（輪の外側は 半径＋線の太さの半分）。
    /// </summary>
    public static Drawing CreateDrawing(double radius)
    {
        var fill = new SolidColorBrush(Color.FromArgb(FillAlpha, RingColor.R, RingColor.G, RingColor.B));
        var stroke = new SolidColorBrush(Color.FromArgb(StrokeAlpha, RingColor.R, RingColor.G, RingColor.B));
        var pen = new Pen(stroke, StrokeFor(Math.Max(0, radius)));
        var geometry = new EllipseGeometry(new Point(0, 0), Math.Max(0, radius), Math.Max(0, radius));
        var drawing = new GeometryDrawing(fill, pen, geometry);
        drawing.Freeze();
        return drawing;
    }

    /// <summary>center を中心に輪を描く（プレビュー用。座標は書き出す映像の px にそろえて使う）。</summary>
    public static void Draw(DrawingContext dc, Point center, double radius)
    {
        if (!(radius > 0)) return;
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.DrawDrawing(CreateDrawing(radius));
        dc.Pop();
    }

    /// <summary>
    /// 書き出し用の輪の画像（PNG。正方形・1 辺 <see cref="SizeFor"/>・透明な背景・輪は画像の中心）。
    /// 図形の画像と同じく、透明度を掛けていない形式（Bgra32）で保存する。半径が 0 以下なら例外。
    /// </summary>
    public static byte[] RenderPng(double radius)
    {
        var side = SizeFor(radius);
        if (side <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "輪の半径が不正です。");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            Draw(dc, new Point(side / 2.0, side / 2.0), radius);
        var rtb = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(straight));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
