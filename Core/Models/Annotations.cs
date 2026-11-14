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
    /// <summary>拡大する範囲（v1.21。動画に重ねる図形だけで使う。手順書画像の注釈には使わない）。</summary>
    Zoom,
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
[JsonDerivedType(typeof(ZoomAnnotation), "zoom")]
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
        ZoomAnnotation => AnnotationKind.Zoom,
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

/// <summary>文字の背後に敷く帯（v1.20.0。保存は帯ありのときだけ "band" を書く）。色は文字色に関わらず白（暗い背景でも文字を読めるようにする）。</summary>
public enum TextBandStyle
{
    /// <summary>帯なし（従来の見た目）。</summary>
    None,
    /// <summary>半透明（字幕の「半透明の帯」と同じ濃さ）。</summary>
    Translucent,
    /// <summary>不透明。</summary>
    Opaque,
}

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
    /// <summary>文字の背後の帯（既定は帯なし。v1.20.0 より前のファイルには無く、読み込むと帯なしになる）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextBandStyle Band { get; set; } = TextBandStyle.None;

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

/// <summary>
/// 拡大する範囲（v1.21。動画に重ねる図形のまとまりの中に 0～1 個）。表示時間の間、この範囲を画面いっぱいに拡大する。
/// 範囲の縦横比は動画と同じにする（編集画面で固定）。倍率 = 基準画像の幅 / Width。
/// 手順書画像の注釈には使わない（読み込み時に除く）。画像には描かない（枠は編集画面だけ）。
/// </summary>
public sealed class ZoomAnnotation : BoxAnnotation
{
    /// <summary>最小の倍率（範囲の幅が画像の幅の 90%）。</summary>
    public const double MinFactor = 1.0 / 0.9;
    /// <summary>最大の倍率（範囲の幅が画像の幅の 25%）。</summary>
    public const double MaxFactor = 4.0;

    /// <summary>基準画像の幅に対する倍率（幅が 0 以下なら 1）。</summary>
    public double FactorFor(int imageWidth) => Width > 0 && imageWidth > 0 ? imageWidth / Width : 1;
}
