using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Annotations;

public readonly record struct ImagePoint(double X, double Y);

public readonly record struct ImageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>2 点から正規化した矩形（幅・高さは非負）。</summary>
    public static ImageRect FromPoints(ImagePoint a, ImagePoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(ImagePoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public ImageRect Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>表示座標 ⇔ 画像ピクセル座標の変換（拡大率とオフセット）。永続化するのは画像座標だけ。</summary>
public readonly record struct ViewTransform(double Scale, double OffsetX = 0, double OffsetY = 0)
{
    public ImagePoint ToImage(double viewX, double viewY) => new((viewX - OffsetX) / Scale, (viewY - OffsetY) / Scale);
    public (double X, double Y) ToView(ImagePoint p) => (p.X * Scale + OffsetX, p.Y * Scale + OffsetY);
    public double ToImageLength(double viewLength) => viewLength / Scale;

    /// <summary>表示領域に画像全体が収まる拡大率（maxScale 以下）で中央に置く。</summary>
    public static ViewTransform Fit(int imageWidth, int imageHeight, double viewWidth, double viewHeight, double maxScale = 1)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return new ViewTransform(1);
        var s = Math.Min(maxScale, Math.Min(viewWidth / imageWidth, viewHeight / imageHeight));
        return new ViewTransform(s, (viewWidth - imageWidth * s) / 2, (viewHeight - imageHeight * s) / 2);
    }
}

/// <summary>選択中の注釈をドラッグで変形するつまみ。</summary>
public enum AnnotationHandle
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Start,
    End,
}

/// <summary>矢印の描画形状（矢じりの三角形と、矢じりに隠れる位置まで短くした軸）。</summary>
public readonly record struct ArrowShape(ImagePoint ShaftStart, ImagePoint ShaftEnd, ImagePoint Tip, ImagePoint BaseLeft, ImagePoint BaseRight);

/// <summary>注釈の当たり判定・移動・変形・画像サイズ変換（UI 非依存）。文字の大きさは描画側の計測関数で受け取る。</summary>
public static class AnnotationGeometry
{
    /// <summary>文字注釈の描画サイズ（基準画像 px）を返す関数。</summary>
    public delegate (double Width, double Height) TextMeasurer(TextAnnotation text, double styleScale);

    /// <summary>矢じりの長さ（描画上の線幅から。細 18 / 中 24 / 太 30px）。幅は長さの 0.5 倍。</summary>
    public static double ArrowHeadLength(double strokePx) => 12 + strokePx * 3;

    public static ArrowShape Arrow(double x1, double y1, double x2, double y2, double strokePx)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var tip = new ImagePoint(x2, y2);
        if (len < 1e-6) return new ArrowShape(new(x1, y1), tip, tip, tip, tip);
        var ux = dx / len;
        var uy = dy / len;
        // 短い矢印では矢じりが全長を超えないようにする。
        var head = Math.Min(ArrowHeadLength(strokePx), len * 0.6);
        var half = head * 0.5;
        var bx = x2 - ux * head;
        var by = y2 - uy * head;
        // 軸は矢じりの付け根より少し先まで（線端が矢じりの外にはみ出さない位置）。
        var shaftEnd = new ImagePoint(x2 - ux * head * 0.8, y2 - uy * head * 0.8);
        return new ArrowShape(new(x1, y1), shaftEnd, tip, new(bx - uy * half, by + ux * half), new(bx + uy * half, by - ux * half));
    }

    /// <summary>当たり判定用の外接矩形（線幅を含む）。</summary>
    public static ImageRect Bounds(Annotation a, double styleScale, TextMeasurer measure) => a switch
    {
        RectangleAnnotation r => new ImageRect(r.X, r.Y, r.Width, r.Height).Inflate(r.StrokeWidth * styleScale / 2),
        MosaicAnnotation m => new ImageRect(m.X, m.Y, m.Width, m.Height),
        ArrowAnnotation ar => ImageRect.FromPoints(new(ar.X1, ar.Y1), new(ar.X2, ar.Y2)).Inflate(ArrowHeadLength(ar.StrokeWidth * styleScale) / 2),
        LineAnnotation l => ImageRect.FromPoints(new(l.X1, l.Y1), new(l.X2, l.Y2)).Inflate(l.StrokeWidth * styleScale / 2),
        TextAnnotation t => TextBounds(t, styleScale, measure),
        _ => default,
    };

    public static ImageRect TextBounds(TextAnnotation t, double styleScale, TextMeasurer measure)
    {
        var (w, h) = measure(t, styleScale);
        return new ImageRect(t.X, t.Y, w, h);
    }

    /// <summary>点が注釈に当たるか。四角形は枠線付近、矢印・直線は線の近く、文字・モザイクは範囲内。tolerance は画像 px。</summary>
    public static bool HitTest(Annotation a, ImagePoint p, double tolerance, double styleScale, TextMeasurer measure)
    {
        switch (a)
        {
            case RectangleAnnotation r:
            {
                var outer = new ImageRect(r.X, r.Y, r.Width, r.Height).Inflate(r.StrokeWidth * styleScale / 2 + tolerance);
                var innerInset = r.StrokeWidth * styleScale / 2 + tolerance;
                var inner = new ImageRect(r.X + innerInset, r.Y + innerInset, r.Width - 2 * innerInset, r.Height - 2 * innerInset);
                return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
            }
            case ArrowAnnotation ar:
                return DistanceToSegment(p, new(ar.X1, ar.Y1), new(ar.X2, ar.Y2)) <= ar.StrokeWidth * styleScale / 2 + tolerance
                       || DistanceToPoint(p, new(ar.X2, ar.Y2)) <= ArrowHeadLength(ar.StrokeWidth * styleScale) * 0.6 + tolerance;
            case LineAnnotation l:
                return DistanceToSegment(p, new(l.X1, l.Y1), new(l.X2, l.Y2)) <= l.StrokeWidth * styleScale / 2 + tolerance;
            case MosaicAnnotation m:
                return new ImageRect(m.X, m.Y, m.Width, m.Height).Inflate(tolerance).Contains(p);
            case TextAnnotation t:
                return TextBounds(t, styleScale, measure).Inflate(tolerance).Contains(p);
            default:
                return false;
        }
    }

    /// <summary>最前面（リストの後ろ）から順に当たりを探す。</summary>
    public static Annotation? HitTestTopmost(IReadOnlyList<Annotation> items, ImagePoint p, double tolerance, double styleScale, TextMeasurer measure)
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (HitTest(items[i], p, tolerance, styleScale, measure)) return items[i];
        return null;
    }

    /// <summary>つまみの位置（文字注釈はつまみなし）。</summary>
    public static IReadOnlyList<(AnnotationHandle Handle, ImagePoint Point)> Handles(Annotation a) => a switch
    {
        BoxAnnotation b =>
        [
            (AnnotationHandle.TopLeft, new(b.X, b.Y)),
            (AnnotationHandle.TopRight, new(b.X + b.Width, b.Y)),
            (AnnotationHandle.BottomLeft, new(b.X, b.Y + b.Height)),
            (AnnotationHandle.BottomRight, new(b.X + b.Width, b.Y + b.Height)),
        ],
        SegmentAnnotation ar => [(AnnotationHandle.Start, new(ar.X1, ar.Y1)), (AnnotationHandle.End, new(ar.X2, ar.Y2))],
        _ => [],
    };

    public static AnnotationHandle HitHandle(Annotation a, ImagePoint p, double radius)
    {
        foreach (var (h, pt) in Handles(a))
            if (Math.Abs(p.X - pt.X) <= radius && Math.Abs(p.Y - pt.Y) <= radius) return h;
        return AnnotationHandle.None;
    }

    /// <summary>
    /// つまみを p（画像座標）へ動かした形に target を変更する。original はドラッグ開始時の状態。
    /// 四角形・モザイクは対角を固定して正規化し、画像範囲に収める。
    /// </summary>
    public static void DragHandle(Annotation original, Annotation target, AnnotationHandle handle, ImagePoint p, int imageWidth, int imageHeight)
    {
        p = Clamp(p, imageWidth, imageHeight);
        switch (original, target)
        {
            case (BoxAnnotation o, BoxAnnotation t):
            {
                var anchor = handle switch
                {
                    AnnotationHandle.TopLeft => new ImagePoint(o.X + o.Width, o.Y + o.Height),
                    AnnotationHandle.TopRight => new ImagePoint(o.X, o.Y + o.Height),
                    AnnotationHandle.BottomLeft => new ImagePoint(o.X + o.Width, o.Y),
                    AnnotationHandle.BottomRight => new ImagePoint(o.X, o.Y),
                    _ => (ImagePoint?)null,
                };
                if (anchor is null) return;
                var r = ImageRect.FromPoints(anchor.Value, p);
                t.X = r.X; t.Y = r.Y; t.Width = r.Width; t.Height = r.Height;
                break;
            }
            case (SegmentAnnotation, SegmentAnnotation t):
                if (handle == AnnotationHandle.Start) { t.X1 = p.X; t.Y1 = p.Y; }
                else if (handle == AnnotationHandle.End) { t.X2 = p.X; t.Y2 = p.Y; }
                break;
        }
    }

    /// <summary>
    /// 注釈全体を (dx, dy) 動かしたときに画像からはみ出さないよう移動量を詰める。
    /// bounds は移動前の外接矩形（線幅を除いた形状の範囲）。画像より大きい場合は左上を優先して合わせる。
    /// </summary>
    public static (double Dx, double Dy) ClampOffset(ImageRect bounds, double dx, double dy, int imageWidth, int imageHeight)
    {
        static double Axis(double start, double size, double d, int limit)
        {
            if (limit <= 0) return d;
            var min = -start;
            var max = limit - (start + size);
            if (max < min) return min; // 画像より大きい
            return Math.Clamp(d, min, max);
        }
        return (Axis(bounds.X, bounds.Width, dx, imageWidth), Axis(bounds.Y, bounds.Height, dy, imageHeight));
    }

    /// <summary>形状の範囲（線幅・矢じりを含まない。移動の範囲制限に使う）。</summary>
    public static ImageRect ShapeBounds(Annotation a, double styleScale, TextMeasurer measure) => a switch
    {
        BoxAnnotation b => new ImageRect(b.X, b.Y, b.Width, b.Height),
        SegmentAnnotation ar => ImageRect.FromPoints(new(ar.X1, ar.Y1), new(ar.X2, ar.Y2)),
        TextAnnotation t => TextBounds(t, styleScale, measure),
        _ => default,
    };

    public static ImagePoint Clamp(ImagePoint p, int imageWidth, int imageHeight)
        => imageWidth <= 0 || imageHeight <= 0 ? p : new(Math.Clamp(p.X, 0, imageWidth), Math.Clamp(p.Y, 0, imageHeight));

    /// <summary>矩形を画像範囲で切り取る（はみ出た部分を除く）。</summary>
    public static ImageRect ClipToImage(ImageRect r, int imageWidth, int imageHeight)
    {
        var x1 = Math.Clamp(r.X, 0, imageWidth);
        var y1 = Math.Clamp(r.Y, 0, imageHeight);
        var x2 = Math.Clamp(r.Right, 0, imageWidth);
        var y2 = Math.Clamp(r.Bottom, 0, imageHeight);
        return new ImageRect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    /// <summary>
    /// 基準画像サイズ (fromW × fromH) の注釈を、実際の画像サイズ (toW × toH) の座標へ変換した複製を返す。
    /// 同じサイズ・基準不明（0）なら単純な複製。
    /// </summary>
    public static List<Annotation> FitToImage(IEnumerable<Annotation> items, int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        var list = items.Select(a => a.Clone()).ToList();
        if (fromWidth <= 0 || fromHeight <= 0 || toWidth <= 0 || toHeight <= 0 || (fromWidth == toWidth && fromHeight == toHeight))
            return list;
        double sx = (double)toWidth / fromWidth, sy = (double)toHeight / fromHeight;
        foreach (var a in list) a.ScaleBy(sx, sy);
        return list;
    }

    public static double DistanceToPoint(ImagePoint a, ImagePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double DistanceToSegment(ImagePoint p, ImagePoint a, ImagePoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return DistanceToPoint(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return DistanceToPoint(p, new(a.X + t * dx, a.Y + t * dy));
    }
}
