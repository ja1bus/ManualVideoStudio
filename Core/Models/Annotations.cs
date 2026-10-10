using System.Text.Json.Serialization;

namespace ManualVideoStudio.Core.Models;

/// <summary>注釈の色。手順書用途のため 3 色に限定する（自由色は扱わない）。</summary>
public enum AnnotationColor
{
    Red,
    Black,
    Blue,
}

public enum AnnotationKind
{
    Rectangle,
    Arrow,
    Text,
    Mosaic,
    /// <summary>矢じりの無い直線（v1.6）。</summary>
    Line,
}

/// <summary>
/// 手順書画像の注釈（非破壊）。座標・寸法はすべて「注釈の基準画像」（CapturePoint.AnnotationImageWidth / Height）の
/// ピクセル座標で持つ。表示倍率・DPI・ウィンドウサイズの影響を受けない。
/// 線幅・文字サイズは「短辺 1080px 以下の画像での px」で、大きな画像では描画時に拡大する（AnnotationStyle.Scale）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RectangleAnnotation), "rectangle")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(MosaicAnnotation), "mosaic")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
public abstract class Annotation
{
    // JSON のキー順: type → id → 位置・大きさ → 種類ごとの項目（人が読みやすい順）。
    [JsonPropertyOrder(-10)]
    public string Id { get; set; } = CutSegment.NewId();

    /// <summary>種類（JSON では "type" で表すため保存しない）。</summary>
    [JsonIgnore]
    public AnnotationKind Kind => this switch
    {
        RectangleAnnotation => AnnotationKind.Rectangle,
        ArrowAnnotation => AnnotationKind.Arrow,
        TextAnnotation => AnnotationKind.Text,
        LineAnnotation => AnnotationKind.Line,
        _ => AnnotationKind.Mosaic,
    };

    /// <summary>平行移動する。</summary>
    public abstract void Offset(double dx, double dy);

    /// <summary>座標・寸法を拡大縮小する（基準画像サイズが変わった場合）。</summary>
    public abstract void ScaleBy(double sx, double sy);

    /// <summary>座標を小数第 1 位へ丸める（保存する JSON を読みやすくする。0.1px は見た目に影響しない）。</summary>
    public abstract void RoundCoordinates();

    public Annotation Clone() => (Annotation)MemberwiseClone();

    protected static double R(double v) => Math.Round(v, 1);
}

/// <summary>四角形・モザイク共通（左上 X / Y と幅・高さ）。</summary>
public abstract class BoxAnnotation : Annotation
{
    [JsonPropertyOrder(-4)] public double X { get; set; }
    [JsonPropertyOrder(-3)] public double Y { get; set; }
    [JsonPropertyOrder(-2)] public double Width { get; set; }
    [JsonPropertyOrder(-1)] public double Height { get; set; }

    public override void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }

    public override void ScaleBy(double sx, double sy)
    {
        X *= sx;
        Y *= sy;
        Width *= sx;
        Height *= sy;
    }

    public override void RoundCoordinates()
    {
        X = R(X); Y = R(Y); Width = R(Width); Height = R(Height);
    }
}

/// <summary>枠線のみの四角形（塗りつぶしなし）。</summary>
public sealed class RectangleAnnotation : BoxAnnotation
{
    public AnnotationColor Color { get; set; } = AnnotationColor.Red;
    public double StrokeWidth { get; set; } = 4;
}

/// <summary>始点・終点を持つ線（矢印・直線に共通。色・線幅も共通）。</summary>
public abstract class SegmentAnnotation : Annotation
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public AnnotationColor Color { get; set; } = AnnotationColor.Red;
    public double StrokeWidth { get; set; } = 4;

    public override void Offset(double dx, double dy)
    {
        X1 += dx; Y1 += dy;
        X2 += dx; Y2 += dy;
    }

    public override void ScaleBy(double sx, double sy)
    {
        X1 *= sx; Y1 *= sy;
        X2 *= sx; Y2 *= sy;
    }

    public override void RoundCoordinates()
    {
        X1 = R(X1); Y1 = R(Y1); X2 = R(X2); Y2 = R(Y2);
    }
}

/// <summary>終点側に矢じりを持つ実線。</summary>
public sealed class ArrowAnnotation : SegmentAnnotation;

/// <summary>矢じりの無い実線（v1.6。操作・色・線幅は矢印と同じ）。</summary>
public sealed class LineAnnotation : SegmentAnnotation;

/// <summary>文字。X / Y は文字列の左上。複数行可（改行）。</summary>
public sealed class TextAnnotation : Annotation
{
    public const string DefaultFontFamily = "Meiryo";

    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = DefaultFontFamily;
    public double FontSize { get; set; } = 20;
    /// <summary>太字なら true（既定は普通の太さ）。</summary>
    public bool Bold { get; set; }
    public AnnotationColor Color { get; set; } = AnnotationColor.Red;

    public override void Offset(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }

    /// <summary>位置のみ変える（文字サイズは基準値のまま。描画時の Scale で画像サイズに合わせる）。</summary>
    public override void ScaleBy(double sx, double sy)
    {
        X *= sx;
        Y *= sy;
    }

    public override void RoundCoordinates()
    {
        X = R(X); Y = R(Y);
    }
}

/// <summary>矩形モザイク。保存画像には必ず焼き込む。BlockSize はモザイク 1 マスの大きさ（基準画像 px）。</summary>
public sealed class MosaicAnnotation : BoxAnnotation
{
    public int BlockSize { get; set; } = 16;

    public override void ScaleBy(double sx, double sy)
    {
        base.ScaleBy(sx, sy);
        BlockSize = Math.Max(1, (int)Math.Round(BlockSize * Math.Min(sx, sy)));
    }
}
