using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Core.Export;

/// <summary>タイトルの文字の配置（動画の大きさに比例）。</summary>
/// <param name="Lines">折り返し後の行（最大 2 行）。収まらないときは 2 行を超えた分も含む。</param>
/// <param name="Fits">最大 2 行に収まったか。</param>
/// <param name="FontSizePx">文字サイズ（px）。</param>
/// <param name="MaxLineWidthPx">文字領域の幅（px。画面幅の 80%）。</param>
/// <param name="TopPx">文字領域の上端（px。画面の高さの 1/3）。</param>
public sealed record TitleLayoutResult(IReadOnlyList<string> Lines, bool Fits, double FontSizePx, double MaxLineWidthPx, double TopPx);

/// <summary>
/// 黒背景タイトルの配置を決める純粋ロジック（v1.15）。プレビューと書き出しの画像は同じ結果から描く。
/// ・文字は白・メイリオ・太字。文字サイズは 1080p で 72px、動画の高さに比例する。
/// ・文字領域の上端は画面の高さの 1/3。横方向は中央揃え。文字領域の幅は画面幅の 80%。
/// ・明示の改行と自動の折り返しを扱い、最大 2 行。収まらない文章は黙って切り捨てず、Fits = false で知らせる。
/// </summary>
public static class TitleLayout
{
    public const string FontFamily = "Meiryo";
    public const double ReferenceHeight = 1080;
    public const double ReferenceFontSize = 72;
    public const double WidthRatio = 0.8;
    public const double TopRatio = 1.0 / 3.0;
    public const int MaxLines = 2;
    /// <summary>行の高さ（文字サイズに対する倍率）。</summary>
    public const double LineHeightRatio = 1.3;

    /// <summary>実際の文字サイズ（px）= 選んだ文字サイズ（1080p 基準）× 動画の高さ ÷ 1080。</summary>
    public static double FontSize(int videoHeight, int fontSize = (int)ReferenceFontSize) => fontSize * videoHeight / ReferenceHeight;

    /// <summary>measure は太字・指定の文字サイズでの文字列の幅（px）。</summary>
    public static TitleLayoutResult Compute(string? text, int videoWidth, int videoHeight, Func<string, double, double> measure,
        int fontSize = (int)ReferenceFontSize)
    {
        var size = FontSize(videoHeight, fontSize);
        var maxWidth = videoWidth * WidthRatio;
        var normalized = VideoTitle.Normalize(text);
        var lines = SubtitleLineBreaker.Break(normalized, maxWidth, s => measure(s, size));
        return new TitleLayoutResult(lines, lines.Count <= MaxLines, size, maxWidth, videoHeight * TopRatio);
    }

    /// <summary>収まらないときの案内（収まるなら null）。</summary>
    public static string? TooLongMessage(TitleLayoutResult layout)
        => layout.Fits ? null : $"タイトルが {MaxLines} 行に収まりません（{layout.Lines.Count} 行になります）。文章を短くするか、改行を減らしてください。";
}
