using System.Globalization;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Naming;

/// <summary>手順書画像の出力ファイル名「連番_キャプション.拡張子」を作る。</summary>
public static class OutputFilenameBuilder
{
    public const int DefaultDigits = 3;

    public static string Extension(ImageFormat format) => format == ImageFormat.Jpeg ? ".jpg" : ".png";

    /// <summary>
    /// 例: (1, "Windowsの設定画面を開く", Png) → "001_Windowsの設定画面を開く.png"。
    /// キャプションが空（サニタイズ後を含む）の場合は "001.png"。
    /// </summary>
    public static string Build(int sequence, string? caption, ImageFormat format,
        int digits = DefaultDigits, int maxCaptionLength = FilenameSanitizer.DefaultMaxLength)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        // 連番が桁数を超える場合（1000件以上）はそのまま桁を増やす。
        var number = sequence.ToString(new string('0', Math.Max(1, digits)), CultureInfo.InvariantCulture);
        var safe = FilenameSanitizer.Sanitize(caption, maxCaptionLength);
        return (safe.Length == 0 ? number : $"{number}_{safe}") + Extension(format);
    }

    /// <summary>連番の桁数。件数が 999 を超える場合は桁を増やしてソート順を保つ。</summary>
    public static int DigitsFor(int count) => Math.Max(DefaultDigits, count.ToString(CultureInfo.InvariantCulture).Length);
}
