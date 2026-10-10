using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>字幕の妥当性検証。時刻は出力動画基準。字幕どうしの表示区間の重なりは許可しない。</summary>
public static class SubtitleValidator
{
    /// <summary>字幕の最短表示時間（秒）。</summary>
    public const double MinDuration = 0.2;
    /// <summary>1 件の最大文字数（改行を除く）。</summary>
    public const int MaxLength = 200;
    /// <summary>最大行数。</summary>
    public const int MaxLines = 3;

    /// <summary>入力された字幕の文字を整える（改行を LF に統一し、各行の末尾と前後の空行を除く）。</summary>
    public static string NormalizeText(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim('\n', ' ', '　', '\t');
    }

    public static ValidationResult Validate(double start, double end, string text, double outputDuration,
        IEnumerable<SubtitleSegment> existing, string? ignoreId = null)
    {
        const double eps = CutSegmentValidator.Epsilon;
        var errors = new List<string>();
        text = NormalizeText(text);
        if (text.Length == 0) errors.Add("字幕の文字を入力してください。");
        else
        {
            if (text.Replace("\n", "").Length > MaxLength) errors.Add($"字幕が長すぎます（{MaxLength} 文字以内）。");
            if (text.Split('\n').Length > MaxLines) errors.Add($"字幕は {MaxLines} 行以内にしてください。");
        }
        if (double.IsNaN(start) || double.IsNaN(end)) errors.Add("時刻が不正です。");
        if (start < -eps) errors.Add("開始位置は 0 秒以上にしてください。");
        if (end <= start + eps) errors.Add("終了位置は開始位置より後にしてください。");
        else if (end - start < MinDuration - eps) errors.Add($"表示時間が短すぎます（{MinDuration} 秒以上）。");
        if (outputDuration > 0 && end > outputDuration + eps)
            errors.Add($"終了位置が編集後の動画の長さ（{TimeFormat.Format(outputDuration)}）を超えています。");

        foreach (var other in existing)
        {
            if (other.Id == ignoreId) continue;
            if (start < other.End - eps && other.Start < end - eps)
            {
                errors.Add($"他の字幕（{TimeFormat.FormatShort(other.Start)}～{TimeFormat.FormatShort(other.End)}）と表示時間が重なっています。");
                break;
            }
        }
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }
}
