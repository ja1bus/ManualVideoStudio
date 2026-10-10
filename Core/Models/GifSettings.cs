namespace ManualVideoStudio.Core.Models;

/// <summary>Animated GIF の品質プリセット。FFmpeg の細かい値は利用者に見せない。</summary>
public enum GifQuality
{
    Light,
    Standard,
    High,
}

/// <summary>プロジェクトごとの GIF 書き出し設定。</summary>
public sealed class GifExportSettings
{
    public GifQuality Quality { get; set; } = GifQuality.Standard;
    /// <summary>出力先フォルダ。空なら元動画と同じフォルダ。</summary>
    public string OutputDirectory { get; set; } = "";
    /// <summary>出力ファイル名。空なら「元ファイル名_edited.gif」。</summary>
    public string OutputFileName { get; set; } = "";
}

/// <summary>
/// GIF プリセットの内部値。録画の FPS とは別に、GIF 用に FPS と大きさを下げる（縮小のみ。拡大はしない）。
/// </summary>
/// <param name="Fps">GIF のフレームレート。</param>
/// <param name="MaxWidth">幅の上限（px）。</param>
/// <param name="MaxHeight">高さの上限（px）。</param>
/// <param name="MaxColors">パレットの色数（palettegen max_colors）。</param>
/// <param name="Dither">paletteuse の dither 指定。</param>
public sealed record GifPreset(GifQuality Quality, string Label, string Description, int Fps, int MaxWidth, int MaxHeight, int MaxColors, string Dither)
{
    public static readonly GifPreset Light = new(GifQuality.Light, "軽量", "約 8fps・幅 800px まで。ファイルを小さくしたい場合", 8, 800, 600, 128, "bayer:bayer_scale=5");
    public static readonly GifPreset Standard = new(GifQuality.Standard, "標準", "約 12fps・幅 1280px まで。通常はこちら", 12, 1280, 960, 256, "bayer:bayer_scale=4");
    public static readonly GifPreset High = new(GifQuality.High, "高品質", "約 15fps・幅 1920px まで。細かい文字を読ませたい場合", 15, 1920, 1440, 256, "sierra2_4a");

    public static IReadOnlyList<GifPreset> All { get; } = [Light, Standard, High];

    public static GifPreset Of(GifQuality quality) => quality switch
    {
        GifQuality.Light => Light,
        GifQuality.High => High,
        _ => Standard,
    };
}
