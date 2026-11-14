using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Ai;

/// <summary>区間表の 1 行の特徴（v1.21）。</summary>
[Flags]
public enum SegmentFeature
{
    None = 0,
    /// <summary>ローカル解析の候補（静止など）と 50% 以上重なる。</summary>
    Still = 1,
    /// <summary>入力の無い範囲と 80% 以上重なる。</summary>
    Idle = 2,
    /// <summary>区間内に録画中の不要マーカーがある。</summary>
    Marker = 4,
    /// <summary>確定カットに含まれる（99% 以上が削除される）。</summary>
    AlreadyCut = 8,
}

/// <summary>区間表の 1 行（時刻は元動画の秒）。Id は 1 始まり。</summary>
public sealed record SegmentRow(int Id, double Start, double End, SegmentFeature Features)
{
    public double Duration => End - Start;

    /// <summary>特徴の表示（「静止・入力なし」。無ければ空）。</summary>
    public string FeatureText => SegmentTable.FeatureText(Features);
}

/// <summary>
/// 不要区間の解析（CutAnalysis）でプロンプトへ渡す区間表（v1.21。設計 4.1）。
/// AI には区間番号で答えてもらい、取り込みでは <see cref="ToHistory"/> で保存した表を使って番号を時刻へ戻す。
/// </summary>
public sealed class SegmentTable
{
    public SegmentTable(IReadOnlyList<SegmentRow> rows, double minSegmentSeconds, double maxSegmentSeconds = SegmentTableBuilder.DefaultMaxSegmentSeconds)
    {
        Rows = rows;
        MinSegmentSeconds = minSegmentSeconds;
        MaxSegmentSeconds = maxSegmentSeconds;
    }

    public IReadOnlyList<SegmentRow> Rows { get; }

    /// <summary>作成に使った最短の長さ（区間が上限を超えたときは既定より大きい）。</summary>
    public double MinSegmentSeconds { get; }

    /// <summary>作成に使った最長の長さ（長い区間を等分した結果が上限を超えたときは既定より大きい）。</summary>
    public double MaxSegmentSeconds { get; }

    public int Count => Rows.Count;

    /// <summary>特徴が無い区間の最後の列に書く文字。列の数をそろえて AI が読み違えないようにする。</summary>
    public const string NoFeatureText = "-";

    /// <summary>
    /// プロンプトの {{SEGMENT_TABLE}} に入れる文字列。1 行 1 区間で
    /// 「12 | 00:03:25〜00:03:41 | 16秒 | 静止・入力なし」の形（時刻は秒に丸める。長さは丸めた時刻の差）。
    /// 特徴が無い区間は最後の列を「-」にする。行の区切りは LF。
    /// </summary>
    public string ToPromptText()
    {
        var sb = new StringBuilder();
        foreach (var r in Rows)
        {
            long s = RoundSeconds(r.Start);
            long e = RoundSeconds(r.End);
            var features = r.Features == SegmentFeature.None ? NoFeatureText : r.FeatureText;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"{r.Id} | {Clock(s)}〜{Clock(e)} | {Math.Max(0, e - s)}秒 | {features}");
        }
        return sb.ToString();
    }

    /// <summary>履歴（<see cref="AiHistoryEntry.Segments"/>）に保存する形。</summary>
    public List<AiSegment> ToHistory()
        => Rows.Select(r => new AiSegment { Id = r.Id, Start = Math.Round(r.Start, 3), End = Math.Round(r.End, 3) }).ToList();

    /// <summary>特徴の表示（静止・入力なし・マーカー・カット済みの順。「・」でつなぐ）。</summary>
    public static string FeatureText(SegmentFeature f)
    {
        var parts = new List<string>(4);
        if (f.HasFlag(SegmentFeature.Still)) parts.Add("静止");
        if (f.HasFlag(SegmentFeature.Idle)) parts.Add("入力なし");
        if (f.HasFlag(SegmentFeature.Marker)) parts.Add("マーカー");
        if (f.HasFlag(SegmentFeature.AlreadyCut)) parts.Add("カット済み");
        return string.Join("・", parts);
    }

    /// <summary>秒を "HH:MM:SS" にする（秒に丸めた値）。</summary>
    public static string Clock(long seconds)
    {
        seconds = Math.Max(0, seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");
    }

    public static long RoundSeconds(double seconds) => (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
}

/// <summary>
/// 区間表を作る純粋関数（v1.21。設計 4.1）。
/// 境目の元は優先順に、確定カットの端 → ローカル候補の端 → 入力なしの範囲の端 → 画面変化。
/// 優先順の高いものから採用し、採用済みの境目（動画の先頭・末尾を含む）から最短の長さ未満の境目は捨てる。
/// 区間が上限を超えたら、最短の長さを 1.5 倍ずつ上げて作り直す。
/// その後、最長の長さ（既定 20 秒）を超える区間を等分する（各区間は最短の長さ以上）。等分して上限を超えるなら、最長の長さを 1.5 倍ずつ上げる。
/// </summary>
public static class SegmentTableBuilder
{
    public const double DefaultMinSegmentSeconds = 2.0;
    /// <summary>区間の最長の長さ（これより長い区間は等分する。区間が上限を超えるときは 1.5 倍ずつ上げる）。</summary>
    public const double DefaultMaxSegmentSeconds = 20.0;
    public const int MaxSegments = 120;
    /// <summary>「静止」とする、ローカル候補と重なる割合。</summary>
    public const double StillOverlapRatio = 0.5;
    /// <summary>「入力なし」とする、入力の無い範囲と重なる割合。</summary>
    public const double IdleOverlapRatio = 0.8;
    /// <summary>「カット済み」とする、確定カットで削除される割合。</summary>
    public const double CutCoverRatio = 0.99;

    /// <param name="duration">元動画の長さ（秒）。0 以下なら空の表。</param>
    /// <param name="cuts">確定カット。</param>
    /// <param name="candidates">候補。使うのはローカル解析（<see cref="SegmentSource.LocalAnalysis"/>）で、採用済みでないものだけ。</param>
    /// <param name="idleRanges">入力の無い範囲（操作の記録が無ければ空）。</param>
    /// <param name="markers">録画中の不要マーカー（秒。無ければ空）。</param>
    /// <param name="sceneChanges">画面変化の時刻（秒）。</param>
    public static SegmentTable Build(double duration, IEnumerable<CutSegment> cuts, IEnumerable<CutCandidate> candidates,
        IReadOnlyList<TimeRange> idleRanges, IReadOnlyList<double> markers, IReadOnlyList<double> sceneChanges)
    {
        if (!(duration > 0) || double.IsInfinity(duration)) return new SegmentTable([], DefaultMinSegmentSeconds);

        var cutList = cuts.Where(c => c.End > c.Start).ToList();
        var local = candidates.Where(c => c.Source == SegmentSource.LocalAnalysis && c.Status != CandidateStatus.Accepted && c.End > c.Start)
            .Select(c => new TimeRange(c.Start, c.End)).ToList();
        var idle = idleRanges.Where(r => r.End > r.Start).ToList();

        // 優先順に並べた境目の元（同じ優先順の中は時刻順）。
        var tiers = new List<List<double>>
        {
            Sorted(cutList.SelectMany(c => new[] { c.Start, c.End })),
            Sorted(local.SelectMany(r => new[] { r.Start, r.End })),
            Sorted(idle.SelectMany(r => new[] { r.Start, r.End })),
            Sorted(sceneChanges),
        };

        var min = DefaultMinSegmentSeconds;
        List<double> picked;
        while (true)
        {
            picked = PickBoundaries(duration, tiers, min);
            if (picked.Count - 1 <= MaxSegments) break;
            min *= 1.5;
        }

        // 長すぎる区間（画面変化の少ない動画）は等分する。上限を超えるなら最長の長さを 1.5 倍ずつ上げる。
        var max = DefaultMaxSegmentSeconds;
        List<double> bounds;
        while (true)
        {
            bounds = SplitLong(picked, min, max);
            if (bounds.Count - 1 <= MaxSegments) break;
            max *= 1.5;
        }

        // 特徴の判定に使う範囲（カット済みは確定カットで削除される部分）。
        var keep = EditTimelineCalculator.ComputeKeepRanges(duration, cutList);
        var localUnion = Union(local);
        var idleUnion = Union(idle);
        var markerList = markers.Where(m => m >= 0 && m <= duration).ToList();

        var rows = new List<SegmentRow>(bounds.Count - 1);
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            double s = bounds[i], e = bounds[i + 1];
            double len = e - s;
            var f = SegmentFeature.None;
            if (Overlap(localUnion, s, e) >= len * StillOverlapRatio) f |= SegmentFeature.Still;
            if (Overlap(idleUnion, s, e) >= len * IdleOverlapRatio) f |= SegmentFeature.Idle;
            bool last = i + 2 == bounds.Count;
            if (markerList.Any(m => m >= s && (m < e || (last && m <= e)))) f |= SegmentFeature.Marker;
            if (len - Overlap(keep, s, e) >= len * CutCoverRatio) f |= SegmentFeature.AlreadyCut;
            rows.Add(new SegmentRow(i + 1, s, e, f));
        }
        return new SegmentTable(rows, min, max);
    }

    /// <summary>
    /// <paramref name="max"/> 秒を超える区間を等分する（分けた後の各区間は <paramref name="min"/> 秒以上。
    /// 最短の長さが最長の長さより大きいときは、最短の長さを守る範囲で分ける）。
    /// </summary>
    internal static List<double> SplitLong(IReadOnlyList<double> bounds, double min, double max)
    {
        var result = new List<double>(bounds.Count) { bounds[0] };
        for (int i = 1; i < bounds.Count; i++)
        {
            double s = bounds[i - 1], e = bounds[i], len = e - s;
            if (len > max)
            {
                var n = (int)Math.Ceiling(len / max - 1e-9);
                n = Math.Max(1, Math.Min(n, (int)Math.Floor(len / min + 1e-9)));
                for (int k = 1; k < n; k++) result.Add(s + len * k / n);
            }
            result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// プロジェクトから区間表を作る（確定カット・候補・画面変化はプロジェクトのもの。入力なし・マーカーは操作の記録から渡す）。
    /// </summary>
    public static SegmentTable FromProject(ProjectDocument project, IReadOnlyList<TimeRange>? idleRanges = null, IReadOnlyList<double>? markers = null)
        => Build(project.VideoMetadata?.Duration ?? 0, project.CutSegments, project.CutCandidates,
            idleRanges ?? [], markers ?? [], project.SceneChanges);

    private static List<double> PickBoundaries(double duration, IReadOnlyList<List<double>> tiers, double min)
    {
        var kept = new List<double> { 0, duration };
        foreach (var tier in tiers)
        {
            foreach (var t in tier)
            {
                if (!(t > 0 && t < duration)) continue;
                // kept は常に昇順。挿入位置の前後とだけ比べればよい。
                int idx = kept.BinarySearch(t);
                if (idx >= 0) continue;
                idx = ~idx;
                if (t - kept[idx - 1] < min || kept[idx] - t < min) continue;
                kept.Insert(idx, t);
            }
        }
        return kept;
    }

    private static List<double> Sorted(IEnumerable<double> values)
        => values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).Distinct().Order().ToList();

    /// <summary>重なりを除いた範囲の和（昇順）。</summary>
    internal static List<TimeRange> Union(IEnumerable<TimeRange> ranges)
    {
        var result = new List<TimeRange>();
        foreach (var r in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (result.Count > 0 && r.Start <= result[^1].End)
            {
                var prev = result[^1];
                result[^1] = new TimeRange(prev.Start, Math.Max(prev.End, r.End));
            }
            else result.Add(r);
        }
        return result;
    }

    /// <summary>[s, e] と範囲の和との重なりの長さ（範囲どうしは重ならないこと）。</summary>
    internal static double Overlap(IEnumerable<TimeRange> disjoint, double s, double e)
    {
        double total = 0;
        foreach (var r in disjoint)
        {
            var a = Math.Max(s, r.Start);
            var b = Math.Min(e, r.End);
            if (b > a) total += b - a;
        }
        return total;
    }
}
