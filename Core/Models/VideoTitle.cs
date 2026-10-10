using System.Text.Json.Serialization;

namespace ManualVideoStudio.Core.Models;

/// <summary>
/// 動画の冒頭に付ける黒背景タイトル（v1.15）。字幕・図形とは独立したプロジェクトごとの設定。
/// 文章が空（または空白だけ）ならタイトルなし。有効／無効の別フラグは持たず、文章の有無で判定する。
/// 文章を消しても秒数・フェードの設定は残す。タイトルの時間は完成動画の先頭に足されるだけで、字幕・図形・カットの保存時刻は変わらない。
/// </summary>
public sealed class VideoTitle
{
    public const int DefaultSeconds = 2;
    public const int MinSeconds = 1;
    public const int MaxSeconds = 5;
    public const string DefaultFontFamily = "Meiryo";
    /// <summary>文字サイズの初期値（動画の高さ 1080px のときの px）。</summary>
    public const int DefaultFontSize = 72;
    /// <summary>選べる文字サイズ（1080p 基準の px。字幕とは別の一覧）。</summary>
    public static readonly IReadOnlyList<int> FontSizes = [48, 56, 64, 72, 88, 104, 120];
    /// <summary>選べる表示秒数。</summary>
    public static readonly IReadOnlyList<int> SecondsChoices = [1, 2, 3, 4, 5];
    /// <summary>本編の冒頭フェード（黒 → 本編）の長さ（秒）。固定。</summary>
    public const double FadeSeconds = 0.3;

    /// <summary>タイトルの文章（改行可・最大 2 行）。空ならタイトルなし。</summary>
    public string Text { get; set; } = "";

    /// <summary>タイトルを表示する秒数（1〜5 秒の整数）。</summary>
    public int Seconds { get; set; } = DefaultSeconds;

    /// <summary>本編の冒頭を黒から徐々に明るくする（0.3 秒）。</summary>
    public bool FadeIn { get; set; } = true;

    /// <summary>フォント（Windows のファミリー名）。太字で描く。</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;

    /// <summary>文字サイズ（動画の高さ 1080px のときの px。実際の大きさは動画の高さに比例する）。</summary>
    public int FontSize { get; set; } = DefaultFontSize;

    /// <summary>タイトルがあるか（文章が空白以外を含む）。</summary>
    [JsonIgnore]
    public bool HasTitle => !string.IsNullOrWhiteSpace(Text);

    /// <summary>書き出しなどで使う文章（改行を LF にそろえ、各行の前後の空白と空行を除く）。</summary>
    [JsonIgnore]
    public string NormalizedText => Normalize(Text);

    public static string Normalize(string? text)
    {
        var lines = (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    public static int NormalizeSeconds(int seconds) => Math.Clamp(seconds, MinSeconds, MaxSeconds);

    public static int NormalizeFontSize(int size)
        => size <= 0 ? DefaultFontSize : FontSizes.MinBy(s => Math.Abs(s - size));

    public static string NormalizeFontFamily(string? family)
        => string.IsNullOrWhiteSpace(family) ? DefaultFontFamily : SubtitleStyle.NormalizeFontFamily(family);

    public VideoTitle Clone() => new() { Text = Text, Seconds = Seconds, FadeIn = FadeIn, FontFamily = FontFamily, FontSize = FontSize };

    /// <summary>保存される内容（正規化後の文章・秒数・フェード）が同じか。変更なしの判定に使う。</summary>
    public bool SameAs(VideoTitle other)
        => string.Equals(Normalize(Text), Normalize(other.Text), StringComparison.Ordinal)
           && NormalizeSeconds(Seconds) == NormalizeSeconds(other.Seconds) && FadeIn == other.FadeIn
           && string.Equals(NormalizeFontFamily(FontFamily), NormalizeFontFamily(other.FontFamily), StringComparison.Ordinal)
           && NormalizeFontSize(FontSize) == NormalizeFontSize(other.FontSize);

    /// <summary>
    /// 本編の冒頭フェードの実際の長さ（秒）。フェードなし・タイトルなしは 0。本編が 0.3 秒より短ければ本編の長さまで縮める。
    /// </summary>
    public double EffectiveFadeSeconds(double mainDuration)
        => HasTitle && FadeIn ? Math.Round(Math.Clamp(mainDuration, 0, FadeSeconds), 3) : 0;

    /// <summary>完成動画の長さ（本編 + タイトル。フェードの有無では変わらない）。タイトルなしは本編のまま。</summary>
    public double TotalDuration(double mainDuration) => mainDuration + (HasTitle ? NormalizeSeconds(Seconds) : 0);

    /// <summary>ツールチップなどに出す要約（文章・秒数・フェードの有無）。</summary>
    public string Summary()
        => $"{NormalizedText}\n{NormalizeSeconds(Seconds)} 秒 / フェード{(FadeIn ? "あり" : "なし")}";
}
