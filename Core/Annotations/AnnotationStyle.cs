using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Annotations;

public enum StrokeWidthLevel
{
    Thin,
    Medium,
    Thick,
}

/// <summary>注釈の色・線幅・文字サイズ・モザイク強度の既定値（手順書用途の固定値）。</summary>
public static class AnnotationStyle
{
    /// <summary>短辺がこの px 以下の画像では、線幅・文字サイズを画像 px そのままで描く。</summary>
    public const double ReferenceShortSide = 1080;

    public const double ThinStroke = 2;
    public const double MediumStroke = 4;
    public const double ThickStroke = 6;

    /// <summary>文字サイズの選択肢（プルダウン）。</summary>
    public static readonly IReadOnlyList<double> FontSizes = [10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72];
    public const double DefaultFontSize = 20;

    /// <summary>ドラッグでこれより小さい四角形・モザイク（基準画像 px）は作らない（クリックの誤操作とみなす）。</summary>
    public const double MinBoxSize = 4;
    /// <summary>これより短い矢印・直線は作らない。</summary>
    public const double MinArrowLength = 8;
    /// <summary>モザイク 1 マスの最小サイズ（px）。</summary>
    public const int MinMosaicBlock = 16;

    /// <summary>"#RRGGBB"。</summary>
    public static string Hex(AnnotationColor color) => color switch
    {
        AnnotationColor.Black => "#000000",
        AnnotationColor.Blue => "#0050D0",
        _ => "#E60012",
    };

    public static (byte R, byte G, byte B) Rgb(AnnotationColor color)
    {
        var hex = Hex(color);
        return (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
    }

    public static string Label(AnnotationColor color) => color switch
    {
        AnnotationColor.Black => "黒",
        AnnotationColor.Blue => "青",
        _ => "赤",
    };

    public static double StrokeWidth(StrokeWidthLevel level) => level switch
    {
        StrokeWidthLevel.Thin => ThinStroke,
        StrokeWidthLevel.Thick => ThickStroke,
        _ => MediumStroke,
    };

    /// <summary>線幅の値に最も近い段階（UI 表示用）。</summary>
    public static StrokeWidthLevel LevelOf(double strokeWidth)
        => strokeWidth <= (ThinStroke + MediumStroke) / 2 ? StrokeWidthLevel.Thin
            : strokeWidth >= (MediumStroke + ThickStroke) / 2 ? StrokeWidthLevel.Thick
            : StrokeWidthLevel.Medium;

    public static string Label(StrokeWidthLevel level) => level switch
    {
        StrokeWidthLevel.Thin => "細",
        StrokeWidthLevel.Thick => "太",
        _ => "中",
    };

    /// <summary>
    /// 線幅・文字サイズの描画倍率。短辺 1080px 以下は 1（画像 px そのまま）、それより大きい画像（高 DPI 画面の録画等）は比例して太く・大きくする。
    /// </summary>
    public static double Scale(int imageWidth, int imageHeight)
    {
        var shortSide = Math.Min(imageWidth, imageHeight);
        return shortSide <= 0 ? 1 : Math.Max(1, shortSide / ReferenceShortSide);
    }

    /// <summary>
    /// モザイク 1 マスの既定サイズ。長辺の 1/80（1280px → 16、1920px → 24、3840px → 48）、最小 16px。
    /// 画面の文字（100% 表示で高さ 12～16px 程度）が 1 文字あたり 1 マス程度になり、元の文字を読めない強さ。
    /// </summary>
    public static int DefaultMosaicBlockSize(int imageWidth, int imageHeight)
        => Math.Max(MinMosaicBlock, (int)Math.Round(Math.Max(imageWidth, imageHeight) / 80.0));
}
