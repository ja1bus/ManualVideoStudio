using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>注釈なしの画像に注釈を焼き込んだ画像を作る。WPF の描画オブジェクトを使うため STA スレッドで呼ぶこと。</summary>
public static class AnnotationRenderer
{
    /// <summary>
    /// モザイクを画素へ焼き込んだ画像（Bgra32・96dpi・Freeze 済み）。モザイクが無くても形式をそろえた複製を返す。
    /// 元画像の画素はモザイク範囲内に残らない（MosaicProcessor が平均色で塗りつぶす）。
    /// </summary>
    public static BitmapSource ApplyMosaics(BitmapSource source, IEnumerable<Annotation> annotations)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        MosaicProcessor.ApplyAll(pixels, w, h, stride, annotations);
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// 保存用の最終画像（Bgr24・96dpi・Freeze 済み）。annotationWidth / Height は注釈座標の基準サイズで、
    /// 実際の画像サイズと違う場合は座標を比例変換する。描画順はモザイク → 四角形・矢印・直線・文字（リスト順）。
    /// </summary>
    public static BitmapSource Render(BitmapSource baseImage, IReadOnlyList<Annotation> annotations, int annotationWidth, int annotationHeight)
    {
        int w = baseImage.PixelWidth, h = baseImage.PixelHeight;
        var fitted = AnnotationGeometry.FitToImage(annotations, annotationWidth, annotationHeight, w, h);
        var mosaicked = ApplyMosaics(baseImage, fitted);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(mosaicked, new Rect(0, 0, w, h));
            AnnotationDrawing.DrawVectors(dc, fitted.Where(a => a is not MosaicAnnotation), AnnotationStyle.Scale(w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        // 元画像（ffmpeg の出力）と同じ不透明 24bit にそろえる。
        var opaque = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }
}
