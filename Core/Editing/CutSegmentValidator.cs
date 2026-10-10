using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public string Message => string.Join(Environment.NewLine, Errors);
    public static readonly ValidationResult Ok = new(Array.Empty<string>());
}

/// <summary>確定カット区間の妥当性検証。重なりは許可しない（候補採用時も同じ規則）。</summary>
public static class CutSegmentValidator
{
    /// <summary>区間の最小長（秒）。</summary>
    public const double MinDuration = 0.05;
    /// <summary>時刻比較の許容誤差（秒）。</summary>
    public const double Epsilon = 0.0005;

    public static ValidationResult Validate(double start, double end, SegmentAction action, double keepSeconds,
        double videoDuration, IEnumerable<CutSegment> existing, string? ignoreId = null)
    {
        var errors = new List<string>();
        if (double.IsNaN(start) || double.IsNaN(end)) errors.Add("時刻が不正です。");
        if (start < -Epsilon) errors.Add("開始位置は 0 秒以上にしてください。");
        if (end <= start + Epsilon) errors.Add("終了位置は開始位置より後にしてください。");
        else if (end - start < MinDuration - Epsilon) errors.Add($"区間が短すぎます（{MinDuration} 秒以上）。");
        if (videoDuration > 0 && end > videoDuration + Epsilon)
            errors.Add($"終了位置が動画の長さ（{TimeFormat.Format(videoDuration)}）を超えています。");

        switch (action)
        {
            case SegmentAction.Remove:
                break;
            case SegmentAction.Shorten:
                if (keepSeconds <= 0) errors.Add("短縮で残す秒数は 0 より大きくしてください（完全に消す場合は「削除」）。");
                else if (keepSeconds >= end - start - Epsilon) errors.Add("短縮で残す秒数は区間の長さより短くしてください。");
                break;
            default:
                errors.Add("確定区間の動作は「削除」または「短縮」です。");
                break;
        }

        foreach (var other in existing)
        {
            if (other.Id == ignoreId) continue;
            if (start < other.End - Epsilon && other.Start < end - Epsilon)
            {
                errors.Add($"既存の区間 {TimeFormat.FormatShort(other.Start)}～{TimeFormat.FormatShort(other.End)} と重なっています。");
                break;
            }
        }
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }

    public static ValidationResult Validate(CutSegment segment, double videoDuration, IEnumerable<CutSegment> existing)
        => Validate(segment.Start, segment.End, segment.Action, segment.KeepSeconds, videoDuration, existing, segment.Id);
}
