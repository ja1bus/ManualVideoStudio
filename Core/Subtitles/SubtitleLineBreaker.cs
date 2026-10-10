using System.Globalization;
using System.Text;

namespace ManualVideoStudio.Core.Subtitles;

/// <summary>字幕の文字の幅を測る（折り返し位置を決めるため）。単位は px。</summary>
public interface ISubtitleTextMeasurer
{
    double Measure(string text, string fontFamily, double fontSizePx);
}

/// <summary>
/// フォントを読まずに幅を見積もる（テスト・フォントを測れない場合の代わり）。
/// 全角（かな・漢字・全角記号）は 1 文字 = 文字サイズ、それ以外は文字サイズ × 0.6。
/// </summary>
public sealed class EstimatedTextMeasurer : ISubtitleTextMeasurer
{
    public static readonly EstimatedTextMeasurer Instance = new();

    public double Measure(string text, string fontFamily, double fontSizePx)
    {
        double w = 0;
        foreach (var r in text.EnumerateRunes())
            w += IsWide(r) ? 1.0 : Rune.GetUnicodeCategory(r) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark ? 0 : 0.6;
        return w * fontSizePx;
    }

    internal static bool IsWide(Rune r) => r.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF
        or >= 0x20000 and <= 0x3FFFD;
}

/// <summary>
/// 字幕の自動折り返し（v1.5）。改行位置をアプリで決め、ASS には明示の改行（\N）として渡す。
/// <para>
/// gyan.dev の ffmpeg 9.0.1（libass 0.17.5）は半角スペースの位置でしか折り返さず、スペースの無い日本語の長い字幕は
/// 画面からはみ出す（ゼロ幅スペースも改行位置にならない。v1.5 で確認）。改行位置をこちらで決めると、
/// libass の作り（改行規則のライブラリの有無）に左右されず、プレビュー（SubtitlePreview）と書き出しで同じ位置で改行できる。
/// </para>
/// 規則: 行末に収まらなくなったら、直前の改行してよい位置で改行する。
/// ・英数字の語（「IP」「Wi-Fi」「192.168.1.1」など、空白を挟まない半角文字の並びで英数字を含む 2 文字以上）は途中で改行しない
///   （1 行に収まらない長い語だけは途中で改行する）。
/// ・行頭禁則（「、」「。」「）」「ッ」など）の前、行末禁則（「（」「「」など）の後では改行しない。
/// ・半角スペースの位置で改行したら、そのスペースは消す。利用者が入れた改行はそのまま残す。
/// </summary>
public static class SubtitleLineBreaker
{
    /// <summary>行頭に置かない文字。</summary>
    private const string NoLineStart = "、。，．,.・：；:;？！?!‼⁇⁈⁉゛゜ヽヾゝゞ々〻ー―‐‑–—～~…‥）)］]｝}〕〉》」』】〙〗〟’”»"
        + "ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶㇰㇱㇲㇳㇴㇵㇶㇷㇸㇹㇺㇻㇼㇽㇾㇿ％%℃°′″";
    /// <summary>行末に置かない文字。</summary>
    private const string NoLineEnd = "（(［[｛{〔〈《「『【〘〖〝‘“«＄$￥¥＃#";

    /// <summary>
    /// 文字を行に分ける。maxWidth は 1 行の最大幅（px）、measure は文字列の幅（px）。
    /// 空の行は返さない（利用者が入れた空行は、前後の空白を除いた後の文字では起きない）。
    /// </summary>
    public static IReadOnlyList<string> Break(string text, double maxWidth, Func<string, double> measure)
    {
        var result = new List<string>();
        foreach (var paragraph in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            BreakParagraph(paragraph, maxWidth, measure, result);
        return result.Count == 0 ? [""] : result;
    }

    private static void BreakParagraph(string paragraph, double maxWidth, Func<string, double> measure, List<string> result)
    {
        var units = Split(paragraph, maxWidth, measure);
        var line = new List<Unit>();
        double width = 0;
        foreach (var unit in units)
        {
            if (line.Count == 0 && unit.IsSpace) continue; // 行頭の空白は捨てる
            if (line.Count == 0 || width + unit.Width <= maxWidth || unit.IsSpace)
            {
                line.Add(unit);
                width += unit.Width;
                continue;
            }
            // 収まらない: 新しい単位の前から戻って、改行してよい位置を探す。
            int cut = -1;
            for (int k = line.Count; k >= 1; k--)
            {
                var next = k < line.Count ? line[k] : unit;
                if (CanBreakBetween(line[k - 1], next)) { cut = k; break; }
            }
            if (cut < 0) cut = line.Count; // 改行してよい位置が無い（禁則文字の連続など）: ここで改行する
            result.Add(Join(line.Take(cut)));
            var rest = line.Skip(cut).SkipWhile(u => u.IsSpace).ToList();
            rest.Add(unit);
            line = rest;
            width = line.Sum(u => u.Width);
            // 持ち越した部分だけで収まらない場合（長い禁則の連続）は、その場で改行を繰り返す。
            while (line.Count > 1 && width > maxWidth)
            {
                int n = 1;
                double w = line[0].Width;
                while (n < line.Count && w + line[n].Width <= maxWidth) w += line[n++].Width;
                result.Add(Join(line.Take(n)));
                line = line.Skip(n).SkipWhile(u => u.IsSpace).ToList();
                width = line.Sum(u => u.Width);
            }
        }
        var last = Join(line);
        if (last.Length > 0 || result.Count == 0) result.Add(last);
    }

    private static string Join(IEnumerable<Unit> units) => string.Concat(units.Select(u => u.Text)).TrimEnd(' ');

    private static bool CanBreakBetween(Unit before, Unit after)
    {
        if (after.IsSpace) return false;          // 空白の前ではなく、空白の後で改行する
        if (before.IsSpace) return true;
        if (NoLineStart.Contains(FirstChar(after.Text))) return false;
        if (NoLineEnd.Contains(LastChar(before.Text))) return false;
        return true;
    }

    private static char FirstChar(string s) => s.Length > 0 ? s[0] : ' ';
    private static char LastChar(string s) => s.Length > 0 ? s[^1] : ' ';

    private sealed record Unit(string Text, double Width, bool IsSpace);

    /// <summary>改行しない単位に分ける: 英数字の語（1 行に収まるもの）・半角スペース・それ以外は 1 文字（結合文字を含む書記素）ずつ。</summary>
    private static List<Unit> Split(string paragraph, double maxWidth, Func<string, double> measure)
    {
        var units = new List<Unit>();
        var elements = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(paragraph);
        while (e.MoveNext()) elements.Add(e.GetTextElement());
        int i = 0;
        while (i < elements.Count)
        {
            var el = elements[i];
            if (el == " ")
            {
                units.Add(new Unit(" ", measure(" "), true));
                i++;
                continue;
            }
            if (IsWordChar(el))
            {
                int j = i;
                while (j < elements.Count && IsWordChar(elements[j])) j++;
                var word = string.Concat(elements.Skip(i).Take(j - i));
                var w = measure(word);
                if (j - i >= 2 && word.Any(char.IsAsciiLetterOrDigit) && w <= maxWidth)
                {
                    units.Add(new Unit(word, w, false));
                    i = j;
                    continue;
                }
                // 1 行に収まらない長い語（URL・パスなど）・記号だけの並びは 1 文字ずつ（語の途中から語として扱い直さない）。
                for (; i < j; i++) units.Add(new Unit(elements[i], measure(elements[i]), false));
                continue;
            }
            units.Add(new Unit(el, measure(el), false));
            i++;
        }
        return units;
    }

    /// <summary>英数字の語を作る文字（空白以外の半角の表示文字）。</summary>
    private static bool IsWordChar(string element) => element.Length == 1 && element[0] is > ' ' and <= '~';
}
