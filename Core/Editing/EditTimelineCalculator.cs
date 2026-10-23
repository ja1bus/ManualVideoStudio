using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Editing;

/// <summary>元動画上の [Start, End) 区間。</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// 確定カット区間から「出力に残す区間」を計算する。書き出し引数生成と出力時刻の対応付けに使う純粋ロジック。
/// Shorten は区間の先頭から KeepSeconds だけ残す。
/// </summary>
public static class EditTimelineCalculator
{
    private const double MinRange = 0.001;

    public static IReadOnlyList<TimeRange> ComputeKeepRanges(double duration, IEnumerable<CutSegment> segments)
    {
        var result = new List<TimeRange>();
        double cursor = 0;
        foreach (var seg in segments.Where(s => s.Action is SegmentAction.Remove or SegmentAction.Shorten)
                                    .OrderBy(s => s.Start))
        {
            double start = Math.Clamp(seg.Start, 0, duration);
            double end = Math.Clamp(seg.End, 0, duration);
            if (end <= cursor) continue; // 重なり・逆転に対する防御（通常は検証で排除済み）
            start = Math.Max(start, cursor);
            Add(result, cursor, start);
            if (seg.Action == SegmentAction.Shorten)
                Add(result, start, Math.Min(end, start + Math.Max(0, seg.KeepSeconds)));
            cursor = end;
        }
        Add(result, cursor, duration);
        return result;
    }

    private static void Add(List<TimeRange> list, double start, double end)
    {
        if (end - start < MinRange) return;
        // 隣接する区間は 1 つにまとめる（Shorten の残し部分と直前の通常区間など）。
        if (list.Count > 0 && Math.Abs(list[^1].End - start) < MinRange)
        {
            list[^1] = list[^1] with { End = end };
            return;
        }
        list.Add(new TimeRange(start, end));
    }

    public static double OutputDuration(IReadOnlyList<TimeRange> keepRanges) => keepRanges.Sum(r => r.Duration);

    /// <summary>元動画の時刻を出力動画の時刻へ変換する。削除される位置なら null。</summary>
    public static double? MapToOutput(double sourceTime, IReadOnlyList<TimeRange> keepRanges)
    {
        double offset = 0;
        foreach (var r in keepRanges)
        {
            if (sourceTime >= r.Start && sourceTime < r.End) return Math.Round(offset + (sourceTime - r.Start), 3);
            offset += r.Duration;
        }
        return null;
    }

    /// <summary>
    /// 元動画の時刻を出力動画の時刻へ変換する。削除される位置なら、その直後に残る位置（無ければ出力の末尾）の時刻を返す。
    /// 字幕編集で「現在位置」を出力時刻として使うため。
    /// </summary>
    public static double MapToOutputClamped(double sourceTime, IReadOnlyList<TimeRange> keepRanges)
    {
        double offset = 0;
        foreach (var r in keepRanges)
        {
            if (sourceTime < r.Start) return Math.Round(offset, 3);
            if (sourceTime < r.End) return Math.Round(offset + (sourceTime - r.Start), 3);
            offset += r.Duration;
        }
        return Math.Round(offset, 3);
    }

    /// <summary>出力動画の時刻を元動画の時刻へ変換する（範囲外は先頭・末尾に寄せる）。</summary>
    public static double MapToSource(double outputTime, IReadOnlyList<TimeRange> keepRanges)
    {
        if (keepRanges.Count == 0) return 0;
        if (outputTime <= 0) return keepRanges[0].Start;
        double offset = 0;
        foreach (var r in keepRanges)
        {
            if (outputTime < offset + r.Duration) return Math.Round(r.Start + (outputTime - offset), 3);
            offset += r.Duration;
        }
        return keepRanges[^1].End;
    }

    /// <summary>出力動画の区間 [start, end) が元動画のどの区間に当たるか（削除区間をまたぐと複数に分かれる）。タイムライン表示用。</summary>
    public static IReadOnlyList<TimeRange> MapOutputRangeToSource(double outputStart, double outputEnd, IReadOnlyList<TimeRange> keepRanges)
    {
        var list = new List<TimeRange>();
        double offset = 0;
        foreach (var r in keepRanges)
        {
            double a = Math.Max(outputStart, offset);
            double b = Math.Min(outputEnd, offset + r.Duration);
            if (b - a >= MinRange) list.Add(new TimeRange(r.Start + (a - offset), r.Start + (b - offset)));
            offset += r.Duration;
            if (offset >= outputEnd) break;
        }
        return list;
    }

    /// <summary>
    /// 区間に含まれるか。開始側は <see cref="MinRange"/> の許容を持たせる（v1.18.2）。
    /// プレーヤーの位置は秒⇔時間の変換で 1 tick ほど小さくなることがあり（例: 79.689 → 79.6889999）、
    /// 削除区間の終端へ飛んだ直後に「まだ削除中」と誤判定して、最後まで飛ばしてしまうのを防ぐ。
    /// </summary>
    private static bool Contains(TimeRange r, double sourceTime)
        => sourceTime >= r.Start - MinRange && sourceTime < r.End;

    /// <summary>元動画の時刻が削除される位置なら、次に残る区間の開始時刻を返す（再生時に削除部分を飛ばす）。残る位置・末尾以降なら null。</summary>
    public static double? NextKeepStart(double sourceTime, IReadOnlyList<TimeRange> keepRanges)
    {
        foreach (var r in keepRanges)
        {
            if (Contains(r, sourceTime)) return null;
            if (sourceTime < r.Start) return r.Start;
        }
        return null;
    }

    /// <summary>元動画の時刻が出力に残る範囲の外（削除区間・短縮で消える部分・最後の残す区間より後）か。</summary>
    public static bool IsRemoved(double sourceTime, IReadOnlyList<TimeRange> keepRanges)
        => !keepRanges.Any(r => Contains(r, sourceTime));

    /// <summary>再生中の位置に対する、削除部分の扱い。</summary>
    public enum SkipKind
    {
        /// <summary>何もしない（残る位置）。</summary>
        None,
        /// <summary>次に残る位置へ飛ばす。</summary>
        Jump,
        /// <summary>最後に残る区間より後なので、そこで止める。</summary>
        Stop,
    }

    /// <summary>
    /// 再生中の位置で、削除部分を飛ばすか・止めるかを決める（v1.18.2）。
    /// 次に残る位置が見つからないだけで最後へ飛ばすことはせず、最後に残る区間の終端以降のときだけ止める。
    /// </summary>
    public static SkipKind ResolveSkip(double sourceTime, IReadOnlyList<TimeRange> keepRanges, out double target)
    {
        target = 0;
        if (!IsRemoved(sourceTime, keepRanges)) return SkipKind.None;
        if (NextKeepStart(sourceTime, keepRanges) is { } next)
        {
            target = next;
            return SkipKind.Jump;
        }
        if (keepRanges.Count == 0 || sourceTime >= keepRanges[^1].End)
        {
            target = keepRanges.Count > 0 ? keepRanges[^1].End : 0;
            return SkipKind.Stop;
        }
        return SkipKind.None; // 想定外の隙間は、飛ばさずそのまま再生する
    }

    /// <summary>編集が何も無い（出力＝元動画全体）か。</summary>
    public static bool IsIdentity(double duration, IReadOnlyList<TimeRange> keepRanges)
        => keepRanges.Count == 1 && keepRanges[0].Start < MinRange && Math.Abs(keepRanges[0].End - duration) < MinRange;
}
