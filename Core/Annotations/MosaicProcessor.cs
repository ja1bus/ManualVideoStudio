using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Annotations;

/// <summary>
/// 画素データへモザイクを焼き込む（BGRA 32bit、行の先頭から stride バイト）。
/// 各マスを平均色 1 色で塗りつぶすため、マス内の元画素は保存画像に残らない。
/// </summary>
public static class MosaicProcessor
{
    /// <summary>モザイクを適用する画素範囲（画像内に切り取り、整数化）。空なら null。</summary>
    public static (int X, int Y, int Width, int Height)? PixelRegion(MosaicAnnotation m, int imageWidth, int imageHeight)
    {
        var r = AnnotationGeometry.ClipToImage(new ImageRect(m.X, m.Y, m.Width, m.Height), imageWidth, imageHeight);
        // 端数を外側へ広げ、指定範囲の画素を 1 つも取りこぼさない。
        int x1 = (int)Math.Floor(r.X), y1 = (int)Math.Floor(r.Y);
        int x2 = (int)Math.Ceiling(r.Right), y2 = (int)Math.Ceiling(r.Bottom);
        x2 = Math.Min(x2, imageWidth);
        y2 = Math.Min(y2, imageHeight);
        return x2 > x1 && y2 > y1 ? (x1, y1, x2 - x1, y2 - y1) : null;
    }

    public static void Apply(byte[] bgra, int width, int height, int stride, MosaicAnnotation mosaic)
    {
        if (PixelRegion(mosaic, width, height) is not { } region) return;
        int block = Math.Max(AnnotationStyle.MinMosaicBlock, mosaic.BlockSize);
        ApplyBlocks(bgra, stride, region.X, region.Y, region.Width, region.Height, block);
    }

    public static void ApplyAll(byte[] bgra, int width, int height, int stride, IEnumerable<Annotation> annotations)
    {
        foreach (var m in annotations.OfType<MosaicAnnotation>()) Apply(bgra, width, height, stride, m);
    }

    /// <summary>範囲の左上からマス目に区切り、各マスを平均色で塗る（端のマスは範囲内で切る）。</summary>
    public static void ApplyBlocks(byte[] bgra, int stride, int x, int y, int w, int h, int block)
    {
        if (block < 1) throw new ArgumentOutOfRangeException(nameof(block));
        for (int by = y; by < y + h; by += block)
        {
            int bh = Math.Min(block, y + h - by);
            for (int bx = x; bx < x + w; bx += block)
            {
                int bw = Math.Min(block, x + w - bx);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (int yy = by; yy < by + bh; yy++)
                {
                    int i = yy * stride + bx * 4;
                    for (int xx = 0; xx < bw; xx++, i += 4)
                    {
                        sb += bgra[i];
                        sg += bgra[i + 1];
                        sr += bgra[i + 2];
                        sa += bgra[i + 3];
                    }
                }
                int n = bw * bh;
                byte b = (byte)(sb / n), g = (byte)(sg / n), r = (byte)(sr / n), a = (byte)(sa / n);
                for (int yy = by; yy < by + bh; yy++)
                {
                    int i = yy * stride + bx * 4;
                    for (int xx = 0; xx < bw; xx++, i += 4)
                    {
                        bgra[i] = b;
                        bgra[i + 1] = g;
                        bgra[i + 2] = r;
                        bgra[i + 3] = a;
                    }
                }
            }
        }
    }
}
