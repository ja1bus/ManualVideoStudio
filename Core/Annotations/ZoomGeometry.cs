using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Annotations;

/// <summary>
/// 拡大の範囲（v1.21。<see cref="ZoomAnnotation"/>）の作成・変形の計算（UI 非依存）。
/// 範囲の縦横比は画像（動画の 1 コマ）と同じに固定し、幅は画像の幅の 25%～90%（倍率 ×1.1～×4）に収め、画像の中に置く。
/// </summary>
public static class ZoomGeometry
{
    /// <summary>範囲の最小の幅（倍率 ×4）。</summary>
    public static double MinWidth(int imageWidth) => imageWidth / ZoomAnnotation.MaxFactor;

    /// <summary>範囲の最大の幅（倍率 ×1.1）。</summary>
    public static double MaxWidth(int imageWidth) => imageWidth / ZoomAnnotation.MinFactor;

    /// <summary>倍率の表示用の値（小数第 1 位に丸める）。</summary>
    public static double DisplayFactor(ZoomAnnotation z, int imageWidth) => Math.Round(z.FactorFor(imageWidth), 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// ドラッグ（start から end）で描く範囲。start を固定した角とし、ドラッグの向きに広げる。大きさは幅・高さのうち
    /// 縦横比に直して大きい方に合わせ、制限の幅へ収める。画像の外へ出る分は内側へずらす。ほとんど動かしていなければ null。
    /// </summary>
    public static ZoomAnnotation? FromDrag(ImagePoint start, ImagePoint end, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return null;
        start = AnnotationGeometry.Clamp(start, imageWidth, imageHeight);
        end = AnnotationGeometry.Clamp(end, imageWidth, imageHeight);
        if (Math.Abs(end.X - start.X) < AnnotationStyle.MinBoxSize && Math.Abs(end.Y - start.Y) < AnnotationStyle.MinBoxSize) return null;
        var z = new ZoomAnnotation();
        Place(z, start, end, imageWidth, imageHeight);
        return z;
    }

    /// <summary>つまみのドラッグ（original の対角を固定し、p へ向けて縦横比を保ったまま大きさを変える）。</summary>
    public static void Resize(ZoomAnnotation original, ZoomAnnotation target, AnnotationHandle handle, ImagePoint p, int imageWidth, int imageHeight)
    {
        ImagePoint? anchor = handle switch
        {
            AnnotationHandle.TopLeft => new ImagePoint(original.X + original.Width, original.Y + original.Height),
            AnnotationHandle.TopRight => new ImagePoint(original.X, original.Y + original.Height),
            AnnotationHandle.BottomLeft => new ImagePoint(original.X + original.Width, original.Y),
            AnnotationHandle.BottomRight => new ImagePoint(original.X, original.Y),
            _ => null,
        };
        if (anchor is not { } a || imageWidth <= 0 || imageHeight <= 0) return;
        // つまみが対角を越えても向きは変えない（範囲が裏返ると扱いにくいため。最小の大きさで止まる）。
        double dirX = handle is AnnotationHandle.TopRight or AnnotationHandle.BottomRight ? 1 : -1;
        double dirY = handle is AnnotationHandle.BottomLeft or AnnotationHandle.BottomRight ? 1 : -1;
        p = AnnotationGeometry.Clamp(p, imageWidth, imageHeight);
        var dx = Math.Max(0, (p.X - a.X) * dirX);
        var dy = Math.Max(0, (p.Y - a.Y) * dirY);
        Place(target, a, new ImagePoint(a.X + dirX * Math.Max(dx, 1e-6), a.Y + dirY * Math.Max(dy, 1e-6)), imageWidth, imageHeight);
    }

    /// <summary>範囲を制限（縦横比・幅・画像の中）に収める。位置は左上を基準に、はみ出す分だけ内側へずらす。</summary>
    public static void Normalize(ZoomAnnotation z, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return;
        var w = Math.Clamp(z.Width, MinWidth(imageWidth), MaxWidth(imageWidth));
        var h = w * imageHeight / imageWidth;
        z.Width = w;
        z.Height = h;
        z.X = Math.Clamp(z.X, 0, imageWidth - w);
        z.Y = Math.Clamp(z.Y, 0, imageHeight - h);
    }

    /// <summary>anchor を角として end の向きに範囲を置く（大きさは縦横比に直した大きい方。制限の幅に収め、画像の中へずらす）。</summary>
    private static void Place(ZoomAnnotation z, ImagePoint anchor, ImagePoint end, int imageWidth, int imageHeight)
    {
        double aspect = (double)imageWidth / imageHeight;
        double dx = end.X - anchor.X, dy = end.Y - anchor.Y;
        double w = Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect);
        // 角を固定したまま画像の端まで広げられる幅（それを超える分は、最小の幅を守る場合だけずらして収める）。
        double room = Math.Min(dx >= 0 ? imageWidth - anchor.X : anchor.X, (dy >= 0 ? imageHeight - anchor.Y : anchor.Y) * aspect);
        w = Math.Min(w, Math.Max(room, MinWidth(imageWidth)));
        w = Math.Clamp(w, MinWidth(imageWidth), MaxWidth(imageWidth));
        double h = w / aspect;
        z.Width = w;
        z.Height = h;
        z.X = Math.Clamp(dx >= 0 ? anchor.X : anchor.X - w, 0, imageWidth - w);
        z.Y = Math.Clamp(dy >= 0 ? anchor.Y : anchor.Y - h, 0, imageHeight - h);
    }
}
