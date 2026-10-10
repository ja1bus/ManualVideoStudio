using System.Text.Json.Serialization;

namespace ManualVideoStudio.Core.Models;

/// <summary>
/// 編集の段階。カット編集（Cut）を確定してから字幕編集（Subtitle）へ進む。
/// Subtitle の間はカット（削除・短縮区間）を変更できない（字幕の時刻と出力動画の時間軸をずらさないため）。
/// </summary>
public enum EditPhase
{
    Cut,
    Subtitle,
}

/// <summary>字幕の表示位置（画面の上・中央・下。横方向は常に中央）。</summary>
public enum SubtitlePosition
{
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// 手動字幕 1 件。時刻は出力動画（カット適用後）の先頭からの秒。
/// MP4 / GIF の書き出しで同じデータを映像へ焼き込む。
/// </summary>
public sealed class SubtitleSegment
{
    public string Id { get; set; } = CutSegment.NewId();
    /// <summary>表示開始（出力動画の秒）。</summary>
    public double Start { get; set; }
    /// <summary>表示終了（出力動画の秒）。</summary>
    public double End { get; set; }
    /// <summary>字幕の文字。改行は LF。</summary>
    public string Text { get; set; } = "";
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Bottom;
    /// <summary>文字サイズ（動画の高さ 1080px 換算の px）。</summary>
    public double FontSize { get; set; } = SubtitleStyle.DefaultFontSize;
    /// <summary>
    /// 上下のずらし（v1.5。動画の高さ 1080px 換算の px。正の値で上へ）。上・中央・下の位置に加えて、この字幕だけを少し動かす
    /// （画面のボタンなどを隠さないように）。1 段 = <see cref="SubtitleStyle.OffsetStep"/>。
    /// </summary>
    public double VerticalOffset { get; set; }

    [JsonIgnore]
    public double Duration => End - Start;
}

/// <summary>字幕の見た目（v1.4。v1.5 からアプリ共通の設定）。</summary>
public enum SubtitlePreset
{
    /// <summary>白文字＋黒の縁取り（v1.3 までの固定の見た目）。</summary>
    Outline,
    /// <summary>白文字＋半透明の黒い帯（v1.9 から既定）。</summary>
    TranslucentBand,
    /// <summary>白文字＋不透明の黒い帯（v1.5～v1.8 の既定）。</summary>
    BlackBox,
    /// <summary>黒文字＋白い帯。</summary>
    WhiteBox,
}

/// <summary>
/// 字幕の共通スタイル（v1.5 からアプリ設定 <c>subtitle</c>。すべてのプロジェクトの書き出し・プレビューで使う）。
/// 見た目（プリセット）・フォント・上下の余白は全字幕で共通。文字サイズ・位置は新しい字幕の初期値で、字幕ごとに持つ。
/// </summary>
public sealed class SubtitleStyle
{
    public const string DefaultFontFamily = "Meiryo";
    public const double DefaultFontSize = 48;
    public const SubtitlePreset DefaultPreset = SubtitlePreset.TranslucentBand;
    /// <summary>上下の余白の既定（1080px 換算。高さの 5%）。</summary>
    public const double DefaultMarginVertical = 54;
    /// <summary>上下のずらしの 1 段（1080px 換算。高さの 2.5%）。</summary>
    public const double OffsetStep = 27;
    /// <summary>上下のずらしの上限（1080px 換算。±18 段 = 高さの 45%）。</summary>
    public const double MaxOffset = OffsetStep * 18;
    /// <summary>文字サイズの選択肢（動画の高さ 1080px 換算の px）。</summary>
    public static readonly IReadOnlyList<double> FontSizes = [24, 32, 40, 48, 56, 64, 72, 96];
    /// <summary>上下の余白の選択肢（1080px 換算。高さの 0 / 2.5 / 5 / 7.5 / 10 / 15 / 20%）。</summary>
    public static readonly IReadOnlyList<double> MarginChoices = [0, 27, 54, 81, 108, 162, 216];
    /// <summary>フォント名に使えない文字（ASS の Style 行は「,」区切り）。</summary>
    private static readonly char[] UnsafeFontChars = [',', '\\', '{', '}', '\n', '\r'];
    /// <summary>縁取りの太さ = 文字サイズ × この比率（最小 1px）。</summary>
    public const double OutlineRatio = 0.08;
    /// <summary>帯の余白（文字の周り）= 文字サイズ × この比率。</summary>
    public const double BoxPaddingRatio = 0.2;

    /// <summary>フォント（Windows の GDI ファミリー名。libass はこの名前で探す）。</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;
    /// <summary>見た目。</summary>
    public SubtitlePreset Preset { get; set; } = DefaultPreset;
    /// <summary>上下の余白（v1.5。1080px 換算。上 / 下の字幕と画面の端の間）。</summary>
    public double MarginVertical { get; set; } = DefaultMarginVertical;
    /// <summary>新しい字幕の文字サイズ。</summary>
    public double FontSize { get; set; } = DefaultFontSize;
    /// <summary>新しい字幕の表示位置。</summary>
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Bottom;

    /// <summary>選択肢の外の値を最も近い選択肢に寄せる。</summary>
    public static double NormalizeFontSize(double size)
        => double.IsNaN(size) || size <= 0 ? DefaultFontSize : FontSizes.MinBy(s => Math.Abs(s - size));

    /// <summary>空・ASS に書けない文字を含むフォント名は既定（メイリオ）にする（PC に入っているかは UI 側で確かめる）。</summary>
    public static string NormalizeFontFamily(string? family)
    {
        var f = family?.Trim() ?? "";
        return f.Length == 0 || f.IndexOfAny(UnsafeFontChars) >= 0 ? DefaultFontFamily : f;
    }

    /// <summary>上下の余白を 0～高さの 25% に収める。</summary>
    public static double NormalizeMargin(double margin)
        => double.IsNaN(margin) ? DefaultMarginVertical : Math.Clamp(Math.Round(margin), 0, 270);

    /// <summary>上下のずらしを段の倍数・上限内に収める。</summary>
    public static double NormalizeOffset(double offset)
        => double.IsNaN(offset) ? 0 : Math.Clamp(Math.Round(offset / OffsetStep) * OffsetStep, -MaxOffset, MaxOffset);

    /// <summary>範囲外・未定義の値を直す（設定ファイルの読み込み時）。</summary>
    public void Normalize()
    {
        FontFamily = NormalizeFontFamily(FontFamily);
        if (!Enum.IsDefined(Preset)) Preset = DefaultPreset;
        if (!Enum.IsDefined(Position)) Position = SubtitlePosition.Bottom;
        FontSize = NormalizeFontSize(FontSize);
        MarginVertical = NormalizeMargin(MarginVertical);
    }

    public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();
}

/// <summary>プリセットごとの色（ASS の &amp;HAABBGGRR。AA は 00 = 不透明、FF = 透明）。</summary>
public sealed record SubtitleColors(bool Box, uint TextColor, uint EdgeColor)
{
    public static SubtitleColors Of(SubtitlePreset preset) => preset switch
    {
        SubtitlePreset.TranslucentBand => new(true, 0x00FFFFFF, 0x60000000),
        SubtitlePreset.BlackBox => new(true, 0x00FFFFFF, 0x00000000),
        SubtitlePreset.WhiteBox => new(true, 0x00000000, 0x00FFFFFF),
        _ => new(false, 0x00FFFFFF, 0x00000000),
    };

    /// <summary>ASS の &amp;HAABBGGRR を (A = 不透明度 0～255, R, G, B) にする（プレビュー用）。</summary>
    public static (byte A, byte R, byte G, byte B) ToArgb(uint ass)
        => ((byte)(255 - (ass >> 24)), (byte)(ass & 0xFF), (byte)((ass >> 8) & 0xFF), (byte)((ass >> 16) & 0xFF));
}
