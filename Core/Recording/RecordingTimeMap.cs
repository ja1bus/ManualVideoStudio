namespace ManualVideoStudio.Core.Recording;

/// <summary>
/// 区間の時計と実際の映像の合わせ方（v1.21）。区間の時計（ffmpeg の起動から計る）と、その区間の実際の映像の長さには
/// 開始の遅れ（0.2～0.5 秒とされる）の分の差がある。その差が区間の先頭側にあるか末尾側にあるかを選ぶ。
/// </summary>
public enum RecordingClockAlignment
{
    /// <summary>先頭合わせ: 時計の 0 秒 ＝ 映像の 0 秒（差は末尾側）。</summary>
    Start,
    /// <summary>末尾合わせ: 時計の終わり ＝ 映像の終わり（ffmpeg の取り込みが遅れて始まり、差は先頭側）。</summary>
    End,
}

/// <summary>
/// 録画中に記録した時刻（区間の番号＋区間の時計の秒）を、完成した動画の秒へ直す（v1.21。純粋関数）。
/// 後の区間の始まり ＝ それより前の使った区間の実際の長さの合計。使わなかった区間（1 フレームも無い）の記録は捨てる（null）。
/// </summary>
public static class RecordingTimeMap
{
    /// <summary>
    /// 既定の合わせ方。2026-11-14 の実測（統合試験 RecordingTimeAlignmentIntegrationTests、3 回×一時停止なし／あり）で、
    /// 映像は時計より長く（停止の指示の後も少し取り込む＝差は末尾側）、末尾合わせは一時停止のたびに遅れが増えた（最大 +0.26 秒）。
    /// 先頭合わせは +0.085～+0.15 秒でほぼ一定のため、先頭合わせにして <see cref="CaptureLatencySeconds"/> を引く。
    /// </summary>
    public const RecordingClockAlignment DefaultAlignment = RecordingClockAlignment.Start;

    /// <summary>
    /// 先頭合わせで差し引く、ffmpeg の起動から最初のコマまでの遅れ（秒。実測 +0.085～+0.15 秒の中ほど）。
    /// 区間ごとに引き、区間の中の 0 秒より前にはしない。
    /// </summary>
    public const double CaptureLatencySeconds = 0.1;

    /// <summary>
    /// 区間 segmentIndex の時計で clockSeconds 秒の時刻を、動画の秒へ直す。区間が範囲外・使わなかった区間なら null。
    /// 区間の中の時刻は [0, 実際の長さ] に収める。
    /// </summary>
    public static double? ToVideoTime(int segmentIndex, double clockSeconds, IReadOnlyList<double> clockLengths,
        IReadOnlyList<double> actualDurations, IReadOnlyList<bool> usable, RecordingClockAlignment alignment = DefaultAlignment,
        double latencySeconds = CaptureLatencySeconds)
    {
        if (segmentIndex < 0 || segmentIndex >= clockLengths.Count || segmentIndex >= actualDurations.Count || segmentIndex >= usable.Count)
            return null;
        if (!usable[segmentIndex] || !double.IsFinite(clockSeconds)) return null;

        double offset = 0;
        for (int i = 0; i < segmentIndex; i++)
            if (usable[i]) offset += Math.Max(0, actualDurations[i]);

        double actual = Math.Max(0, actualDurations[segmentIndex]);
        double within = alignment == RecordingClockAlignment.End
            ? clockSeconds - (clockLengths[segmentIndex] - actual)
            : clockSeconds - Math.Max(0, latencySeconds);
        return offset + Math.Clamp(within, 0, actual);
    }
}
