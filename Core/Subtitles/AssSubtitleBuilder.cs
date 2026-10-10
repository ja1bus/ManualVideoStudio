using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Subtitles;

/// <summary>
/// 字幕 1 件の画面上の配置（プレビュー表示と焼き込みで共通に使う値。単位は動画の px）。
/// 上・下の字幕は MarginVerticalPx（画面の端から字幕の端まで）、中央の字幕は CenterY（字幕の中心の高さ）で置く。
/// </summary>
public sealed record SubtitleLayout(double FontSizePx, double OutlinePx, double MarginVerticalPx, double MarginHorizontalPx, int AssAlignment,
    double CenterY)
{
    /// <summary>1 行の最大幅（左右の余白の内側。フォントの測り方の違いで libass がさらに折り返さないよう少し狭くする）。</summary>
    public double MaxLineWidth(int videoWidth) => Math.Max(1, (videoWidth - MarginHorizontalPx * 2) * 0.96);
}

/// <summary>
/// 字幕（SubtitleSegment）を ASS 形式へ変換する。MP4 / GIF の焼き込みはどちらもこの ASS を FFmpeg の subtitles フィルタ（libass）で描く。
/// 見た目（プリセット: 縁取り / 半透明の帯 / 黒い帯 / 白い帯）・フォント・上下の余白は全字幕で共通（アプリ設定）、
/// 文字サイズ・上 / 中央 / 下の位置・上下のずらしを字幕ごとに持つ。
/// 縁取りは BorderStyle 1。帯は BorderStyle 4（v1.5。字幕全体を 1 枚の箱で囲む。色は BackColour、Outline の値が文字の周りの余白）。
/// v1.4 までの BorderStyle 3 は行ごとに帯を描くため、半透明の帯では 2 行の重なりが濃くなった。影なし・横方向は中央。
/// 文字サイズ・余白・ずらしは動画の高さ 1080px 換算の値で、実際の px = 値 × 高さ / 1080。
/// 改行位置はアプリで決めて \N で渡す（<see cref="SubtitleLineBreaker"/>）。
/// </summary>
public static class AssSubtitleBuilder
{
    /// <summary>左右の余白 = 動画の幅 × この比率（長い字幕の折り返し位置）。</summary>
    public const double MarginHorizontalRatio = 0.05;
    public const double ReferenceHeight = 1080;

    /// <summary>字幕 1 件の配置。OutlinePx は縁取りの太さ（帯のプリセットでは文字の周りの余白）。</summary>
    public static SubtitleLayout Layout(SubtitleSegment segment, int videoWidth, int videoHeight, SubtitleStyle? style = null)
    {
        style ??= new SubtitleStyle();
        double scale = videoHeight / ReferenceHeight;
        double size = Math.Max(8, SubtitleStyle.NormalizeFontSize(segment.FontSize) * scale);
        double outline = Math.Max(1, size * (SubtitleColors.Of(style.Preset).Box ? SubtitleStyle.BoxPaddingRatio : SubtitleStyle.OutlineRatio));
        double margin = SubtitleStyle.NormalizeMargin(style.MarginVertical) * scale;
        double offset = SubtitleStyle.NormalizeOffset(segment.VerticalOffset) * scale;
        var (alignment, marginV) = segment.Position switch
        {
            SubtitlePosition.Top => (8, Math.Max(0, margin - offset)),
            SubtitlePosition.Middle => (5, margin),
            _ => (2, Math.Max(0, margin + offset)),
        };
        double centerY = Math.Clamp(videoHeight / 2.0 - offset, 0, videoHeight);
        return new SubtitleLayout(size, outline, marginV, videoWidth * MarginHorizontalRatio, alignment, centerY);
    }

    /// <summary>字幕 1 件の行（アプリで決めた改行位置）。プレビューと焼き込みで同じ結果にする。</summary>
    public static IReadOnlyList<string> Lines(SubtitleSegment segment, SubtitleLayout layout, int videoWidth, string fontFamily, ISubtitleTextMeasurer? measurer)
    {
        var m = measurer ?? EstimatedTextMeasurer.Instance;
        var text = (segment.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        return SubtitleLineBreaker.Break(text, layout.MaxLineWidth(videoWidth), s => m.Measure(s, fontFamily, layout.FontSizePx));
    }

    /// <summary>ASS ファイルの内容。videoWidth / videoHeight は字幕を焼き込む時点の映像の大きさ。</summary>
    public static string Build(IEnumerable<SubtitleSegment> segments, SubtitleStyle style, int videoWidth, int videoHeight,
        ISubtitleTextMeasurer? measurer = null)
    {
        if (videoWidth <= 0 || videoHeight <= 0) throw new ArgumentOutOfRangeException(nameof(videoWidth), "映像の大きさが不明です。");
        var font = SubtitleStyle.NormalizeFontFamily(style.FontFamily);
        var preset = Enum.IsDefined(style.Preset) ? style.Preset : SubtitleStyle.DefaultPreset;
        var normalized = new SubtitleStyle { FontFamily = font, Preset = preset, MarginVertical = style.MarginVertical, FontSize = style.FontSize };
        var colors = SubtitleColors.Of(preset);
        var baseLayout = Layout(new SubtitleSegment { FontSize = style.FontSize }, videoWidth, videoHeight, normalized);
        // 帯（BorderStyle 4）: 箱の色は BackColour。OutlineColour は文字の縁取りになるので透明にする。
        var outlineColour = colors.Box ? 0xFF000000u : colors.EdgeColor;
        var backColour = colors.Box ? colors.EdgeColor : 0x00000000u;
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n");
        sb.Append("; Manual Video Studio が生成した字幕（焼き込み用の一時ファイル）\n");
        sb.Append("ScriptType: v4.00+\n");
        sb.Append(Inv($"PlayResX: {videoWidth}\n"));
        sb.Append(Inv($"PlayResY: {videoHeight}\n"));
        // 改行位置はアプリで決めて \N で渡す。1 = 行末で折り返す（0 / 3 の行長さの再調整は改行位置がずれることがあるため使わない。開発メモ参照）。
        sb.Append("WrapStyle: 1\n");
        sb.Append("ScaledBorderAndShadow: yes\n");
        sb.Append("YCbCr Matrix: None\n\n");
        sb.Append("[V4+ Styles]\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        sb.Append(Inv($"Style: Default,{font},{N(baseLayout.FontSizePx)},{Color(colors.TextColor)},&H000000FF,{Color(outlineColour)},{Color(backColour)},0,0,0,0,100,100,0,0,{(colors.Box ? 4 : 1)},{N(baseLayout.OutlinePx)},0,2,{(int)Math.Round(baseLayout.MarginHorizontalPx)},{(int)Math.Round(baseLayout.MarginHorizontalPx)},{(int)Math.Round(baseLayout.MarginVerticalPx)},1\n\n"));
        sb.Append("[Events]\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (s.End <= s.Start) continue;
            var l = Layout(s, videoWidth, videoHeight, normalized);
            var text = string.Join("\\N", Lines(s, l, videoWidth, font, measurer).Select(EscapeText));
            if (text.Replace("\\N", "").Length == 0) continue;
            // 中央の字幕を上下にずらす場合は位置を指定する（libass は中央揃えで MarginV を使わないため）。
            var pos = l.AssAlignment == 5 && Math.Abs(l.CenterY - videoHeight / 2.0) > 0.01
                ? Inv($"\\pos({N(videoWidth / 2.0)},{N(l.CenterY)})") : "";
            // MarginV は字幕ごとに書く（0 は Style の値を使う意味になるため、0 のときは 1px にする）。
            int marginV = Math.Max(1, (int)Math.Round(l.MarginVerticalPx));
            sb.Append(Inv($"Dialogue: 0,{Time(s.Start)},{Time(s.End)},Default,,0,0,{marginV},,{{\\an{l.AssAlignment}{pos}\\fs{N(l.FontSizePx)}\\bord{N(l.OutlinePx)}}}{text}\n"));
        }
        return sb.ToString();
    }

    /// <summary>ASS の時刻（H:MM:SS.cc）。</summary>
    public static string Time(double seconds)
    {
        long cs = (long)Math.Round(Math.Max(0, seconds) * 100, MidpointRounding.AwayFromZero);
        return Inv($"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    /// <summary>
    /// 字幕の文字（1 行）を ASS の本文にする。改行は \N。「{」「}」は上書きタグと解釈されるため全角に、
    /// 「\」は直後の文字と組み合わせて \N などと解釈されないよう、後ろに単語結合子（U+2060、幅なし）を置く。
    /// </summary>
    public static string EscapeText(string text)
    {
        var t = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
        t = t.Replace("\\", "\\⁠").Replace('{', '｛').Replace('}', '｝');
        return t.Replace("\n", "\\N");
    }

    /// <summary>ASS の色（&amp;HAABBGGRR）。</summary>
    public static string Color(uint value) => "&H" + value.ToString("X8", CultureInfo.InvariantCulture);

    private static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
