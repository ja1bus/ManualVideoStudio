using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>
/// 図形のまとまり（v1.6）の表示時間の検証。時刻は出力動画基準。字幕と違い、まとまりどうしの表示時間の重なりは許可する
/// （矢印を出したまま別の四角形を足す、などのため）。
/// </summary>
public static class ShapeGroupValidator
{
    /// <summary>最短表示時間（秒。字幕と同じ）。</summary>
    public const double MinDuration = SubtitleValidator.MinDuration;

    public static ValidationResult Validate(double start, double end, double outputDuration, int imageWidth, int imageHeight)
    {
        const double eps = CutSegmentValidator.Epsilon;
        var errors = new List<string>();
        if (double.IsNaN(start) || double.IsNaN(end)) errors.Add("時刻が不正です。");
        if (start < -eps) errors.Add("開始位置は 0 秒以上にしてください。");
        if (end <= start + eps) errors.Add("終了位置は開始位置より後にしてください。");
        else if (end - start < MinDuration - eps) errors.Add($"表示時間が短すぎます（{MinDuration} 秒以上）。");
        if (outputDuration > 0 && end > outputDuration + eps)
            errors.Add($"終了位置が編集後の動画の長さ（{TimeFormat.Format(outputDuration)}）を超えています。［最後まで］で動画の最後にできます。");
        if (imageWidth <= 0 || imageHeight <= 0) errors.Add("図形の基準の画像サイズが不正です。");
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }
}
