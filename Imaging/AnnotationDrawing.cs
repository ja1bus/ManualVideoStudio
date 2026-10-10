using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Imaging;

/// <summary>
/// 四角形・矢印・直線・文字を DrawingContext へ描く（座標は画像 px）。編集画面の表示と保存画像の描画で同じ処理を使い、見た目を一致させる。
/// モザイクは画素を書き換えるため AnnotationRenderer.ApplyMosaics で扱う。
/// </summary>
public static class AnnotationDrawing
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly Dictionary<AnnotationColor, SolidColorBrush> Brushes = [];

    public static SolidColorBrush BrushOf(AnnotationColor color)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(color, out var b))
            {
                var (r, g, bl) = AnnotationStyle.Rgb(color);
                b = new SolidColorBrush(Color.FromRgb(r, g, bl));
                b.Freeze();
                Brushes[color] = b;
            }
            return b;
        }
    }

    /// <summary>ベクター注釈をリスト順（後ろほど手前）に描く。モザイクは描かない。</summary>
    public static void DrawVectors(DrawingContext dc, IEnumerable<Annotation> annotations, double styleScale)
    {
        foreach (var a in annotations) Draw(dc, a, styleScale);
    }

    public static void Draw(DrawingContext dc, Annotation a, double styleScale)
    {
        switch (a)
        {
            case RectangleAnnotation r:
            {
                var pen = CreatePen(BrushOf(r.Color), r.StrokeWidth * styleScale);
                pen.LineJoin = PenLineJoin.Miter;
                dc.DrawRectangle(null, pen, new Rect(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height)));
                break;
            }
            case ArrowAnnotation ar:
                DrawArrow(dc, ar, styleScale);
                break;
            case LineAnnotation l:
                // 直線は両端を丸める（矢印の軸と同じペン）。
                dc.DrawLine(CreatePen(BrushOf(l.Color), l.StrokeWidth * styleScale), new Point(l.X1, l.Y1), new Point(l.X2, l.Y2));
                break;
            case TextAnnotation t when t.Text.Length > 0:
                DrawText(dc, t, styleScale);
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, ArrowAnnotation ar, double styleScale)
    {
        var brush = BrushOf(ar.Color);
        var stroke = ar.StrokeWidth * styleScale;
        var shape = AnnotationGeometry.Arrow(ar.X1, ar.Y1, ar.X2, ar.Y2, stroke);
        dc.DrawLine(CreatePen(brush, stroke), P(shape.ShaftStart), P(shape.ShaftEnd));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(P(shape.Tip), isFilled: true, isClosed: true);
            ctx.LineTo(P(shape.BaseLeft), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(P(shape.BaseRight), isStroked: true, isSmoothJoin: false);
        }
        head.Freeze();
        // 細い線でも矢じりの角が欠けないよう、同色の細い線で縁取る。
        var edge = new Pen(brush, Math.Max(1, stroke * 0.25)) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(brush, edge, head);
    }

    /// <summary>文字は縁取り・影などの装飾をせず、指定の色でそのまま描く。</summary>
    private static void DrawText(DrawingContext dc, TextAnnotation t, double styleScale)
        => dc.DrawText(CreateText(t, styleScale), new Point(t.X, t.Y));

    public static FormattedText CreateText(TextAnnotation t, double styleScale)
    {
        var family = string.IsNullOrWhiteSpace(t.FontFamily) ? TextAnnotation.DefaultFontFamily : t.FontFamily;
        return new FormattedText(t.Text.Length == 0 ? " " : t.Text, Culture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            Math.Max(1, t.FontSize * styleScale), BrushOf(t.Color), pixelsPerDip: 1.0);
    }

    /// <summary>文字注釈の大きさ（画像 px）。AnnotationGeometry.TextMeasurer として使う。</summary>
    public static (double Width, double Height) MeasureText(TextAnnotation t, double styleScale)
    {
        var ft = CreateText(t, styleScale);
        return (ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    public static Pen CreatePen(Brush brush, double thickness)
        => new(brush, Math.Max(0.5, thickness)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static Point P(ImagePoint p) => new(p.X, p.Y);
}
