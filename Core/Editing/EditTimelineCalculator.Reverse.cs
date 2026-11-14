namespace ManualVideoStudio.Core.Editing;

public static partial class EditTimelineCalculator
{
    /// <summary>
    /// 逆方向の再生（J）で削除部分を飛ばすための、手前の残る区間の終端（v1.21.0）。
    /// 元動画の時刻が削除される位置なら、それ以前に終わる最も近い残す区間の終端を返す。
    /// 残る位置、または手前に残す区間が無い（先頭の削除部分）なら null。
    /// 返す終端は区間の外（[Start, End) の End）なので、呼び出し側はそこから続けて手前へ戻る。
    /// </summary>
    public static double? PrevKeepEnd(double sourceTime, IReadOnlyList<TimeRange> keepRanges)
    {
        if (!IsRemoved(sourceTime, keepRanges)) return null;
        for (int i = keepRanges.Count - 1; i >= 0; i--)
        {
            if (keepRanges[i].End <= sourceTime + MinRange) return keepRanges[i].End;
        }
        return null;
    }
}
