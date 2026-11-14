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

    /// <summary>表示時間に加えて、まとまりの図形（拡大。v1.21）も検証する。</summary>
    public static ValidationResult Validate(double start, double end, double outputDuration, int imageWidth, int imageHeight, IEnumerable<Annotation> shapes)
    {
        var errors = new List<string>(Validate(start, end, outputDuration, imageWidth, imageHeight).Errors);
        if (imageWidth > 0 && imageHeight > 0) errors.AddRange(ValidateShapes(shapes, imageWidth, imageHeight).Errors);
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors);
    }

    public const string TooManyZoomsMessage = "拡大は 1 つのまとまりに 1 つだけにしてください。";
    public const string ZoomFactorMessage = "拡大の倍率は ×1.1～×4 にしてください。";
    public const string ZoomOutsideMessage = "拡大の範囲が画像の外にはみ出しています。";

    /// <summary>
    /// まとまりの図形の検証（v1.21）。拡大は 0～1 個、倍率（画像の幅 / 範囲の幅）は ×1.1～×4、範囲は画像の中。
    /// 座標は小数第 1 位に丸めて保存するため、0.5px までのはみ出しは許す。
    /// </summary>
    public static ValidationResult ValidateShapes(IEnumerable<Annotation> shapes, int imageWidth, int imageHeight)
    {
        const double tolerance = 0.5;
        var zooms = shapes.OfType<ZoomAnnotation>().ToList();
        if (zooms.Count == 0) return ValidationResult.Ok;
        var errors = new List<string>();
        if (zooms.Count > 1) errors.Add(TooManyZoomsMessage);
        foreach (var z in zooms)
        {
            if (z.Width <= 0 || z.Height <= 0) { errors.Add(ZoomFactorMessage); continue; }
            var factor = z.FactorFor(imageWidth);
            // 座標の丸め（0.1px）で境界の倍率がわずかに外れるのは許す。
            if (factor < ZoomAnnotation.MinFactor - 1e-3 || factor > ZoomAnnotation.MaxFactor + 1e-3) errors.Add(ZoomFactorMessage);
            if (z.X < -tolerance || z.Y < -tolerance || z.X + z.Width > imageWidth + tolerance || z.Y + z.Height > imageHeight + tolerance)
                errors.Add(ZoomOutsideMessage);
        }
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(errors.Distinct().ToList());
    }

    public const string ZoomOverlapMessage = "拡大の表示時間が、ほかの拡大と重なっています。";

    /// <summary>
    /// 拡大を含むまとまりどうしの表示時間の重なり（v1.21。拡大は同時に 1 つだけ）。接するだけなら許す。
    /// excludeId は変更中のまとまり（それ自身とは比べない）。
    /// </summary>
    public static bool ZoomOverlaps(double start, double end, IEnumerable<Annotation> shapes, IEnumerable<ShapeGroup> groups, string? excludeId = null)
    {
        const double eps = CutSegmentValidator.Epsilon;
        if (!shapes.OfType<ZoomAnnotation>().Any()) return false;
        return groups.Any(g => g.Id != excludeId && g.Zoom is not null && start < g.End - eps && g.Start < end - eps);
    }
}
