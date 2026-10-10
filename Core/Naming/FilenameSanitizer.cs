using System.Globalization;
using System.Text;

namespace ManualVideoStudio.Core.Naming;

/// <summary>
/// キャプション等の任意文字列を Windows のファイル名として安全な文字列にする。
/// 規則は仕様書「手順書画像抽出仕様 / ファイル名サニタイズ」を参照。
/// </summary>
public static class FilenameSanitizer
{
    /// <summary>既定の最大長（UTF-16 コード単位）。拡張子・連番を除いたキャプション部分に適用する。</summary>
    public const int DefaultMaxLength = 80;

    // 使用不可の半角記号は意味を保つため全角の同形文字へ置き換える。
    private static readonly Dictionary<char, char> Replacements = new()
    {
        ['\\'] = '＼',
        ['/'] = '／',
        [':'] = '：',
        ['*'] = '＊',
        ['?'] = '？',
        ['"'] = '”',
        ['<'] = '＜',
        ['>'] = '＞',
        ['|'] = '｜',
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// ファイル名部分（拡張子なし）をサニタイズする。結果が空になり得る（呼び出し側で扱う）。
    /// </summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            char ch = c;
            if (Replacements.TryGetValue(ch, out var rep)) ch = rep;
            // 改行・タブ等の制御文字や書式制御文字は空白扱い。
            if (char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) ch = ' ';
            if (char.IsWhiteSpace(ch) && ch != '　')
            {
                if (lastWasSpace || sb.Length == 0) continue;
                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            lastWasSpace = false;
            sb.Append(ch);
        }

        var result = TrimEnd(sb.ToString().Trim());
        result = Truncate(result, maxLength);
        result = TrimEnd(result);

        // 予約名は拡張子の有無に関係なく使えない（"CON.png" も不可）。先頭要素で判定する。
        var stem = result.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem)) result = "_" + result;
        return result;
    }

    /// <summary>末尾のピリオド・空白（全角空白含む）を取り除く。</summary>
    private static string TrimEnd(string s) => s.TrimEnd('.', ' ', '　');

    /// <summary>サロゲートペア・結合文字を途中で切らないように切り詰める。</summary>
    private static string Truncate(string s, int maxLength)
    {
        if (s.Length <= maxLength) return s;
        var enumerator = StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (sb.Length + element.Length > maxLength) break;
            sb.Append(element);
        }
        return sb.ToString();
    }
}
