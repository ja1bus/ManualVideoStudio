using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>
/// 動画に重ねる図形（v1.6）の書き出し用の画像を作る。四角形・矢印・直線・文字を、静止画の注釈と同じ描画処理（AnnotationDrawing）で
/// 透明な背景に描き、図形のある範囲だけを PNG にする。モザイクは書き出しで ffmpeg が毎コマかける（VideoShapeLayers.MosaicsOf）。
/// WPF の描画オブジェクトを使うため STA スレッドで呼ぶこと。
/// </summary>
public static class ShapeOverlayRenderer
{
    /// <summary>図形の範囲の外側に加える余白（px。線の端・文字のはみ出し分）。</summary>
    private const double Margin = 4;

    /// <summary>書き出しで重ねる図形（画像とモザイク）。図形が無ければ Empty。</summary>
    public static VideoShapeLayers Build(IEnumerable<ShapeGroup> groups, int videoWidth, int videoHeight)
    {
        var list = groups.ToList();
        if (list.Count == 0 || videoWidth <= 0 || videoHeight <= 0) return VideoShapeLayers.Empty;
        var images = new List<ShapeOverlayImage>();
        foreach (var g in list)
            if (Render(g.Shapes, g.ImageWidth, g.ImageHeight, videoWidth, videoHeight) is { } r)
                images.Add(new ShapeOverlayImage(g.Start, g.End, r.X, r.Y, r.Png));
        return new VideoShapeLayers(images, VideoShapeLayers.MosaicsOf(list, videoWidth, videoHeight));
    }

    /// <summary>
    /// まとまり 1 つの四角形・矢印・直線・文字を描いた PNG と、映像上の左上の位置。描くものが無ければ null。
    /// 図形の座標は基準画像（baseWidth × baseHeight）の px で、映像の大きさへ比例変換する。
    /// </summary>
    public static (int X, int Y, byte[] Png)? Render(IEnumerable<Annotation> shapes, int baseWidth, int baseHeight, int videoWidth, int videoHeight)
    {
        var fitted = AnnotationGeometry.FitToImage(shapes.Where(a => a is not MosaicAnnotation && a is not TextAnnotation { Text.Length: 0 }),
            baseWidth, baseHeight, videoWidth, videoHeight);
        if (fitted.Count == 0) return null;
        var scale = AnnotationStyle.Scale(videoWidth, videoHeight);
        ImageRect? union = null;
        foreach (var a in fitted)
        {
            var b = AnnotationGeometry.Bounds(a, scale, AnnotationDrawing.MeasureText);
            union = union is not { } u ? b
                : ImageRect.FromPoints(new(Math.Min(u.X, b.X), Math.Min(u.Y, b.Y)), new(Math.Max(u.Right, b.Right), Math.Max(u.Bottom, b.Bottom)));
        }
        var area = AnnotationGeometry.ClipToImage(union!.Value.Inflate(Margin), videoWidth, videoHeight);
        int x = (int)Math.Floor(area.X), y = (int)Math.Floor(area.Y);
        int w = (int)Math.Ceiling(area.Right) - x, h = (int)Math.Ceiling(area.Bottom) - y;
        if (w <= 0 || h <= 0) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            AnnotationDrawing.DrawVectors(dc, fitted, scale);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        // PNG は透明度を掛けていない形式（Bgra32）で保存する。
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(straight));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return (x, y, ms.ToArray());
    }
}
