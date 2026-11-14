using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

/// <summary>v1.21.0 の純粋ロジック（逆再生の飛ばし先・Q/W の対象・［字幕に合わせる］・入力なしの候補）。</summary>
public class PrevKeepEndTests
{
    private static readonly TimeRange[] Keeps = [new(0, 10), new(20, 30), new(40, 60)];

    [Theory]
    [InlineData(15.0, 10.0)]   // 削除部分 → 手前の残す区間の終端
    [InlineData(35.0, 30.0)]
    [InlineData(10.0, 10.0)]   // 残す区間の終端ちょうど（[Start, End) の外）は削除部分
    [InlineData(70.0, 60.0)]   // 最後の残す区間より後
    public void RemovedPositionReturnsPreviousKeepEnd(double t, double expected)
        => Assert.Equal(expected, EditTimelineCalculator.PrevKeepEnd(t, Keeps));

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(20.0)]
    [InlineData(59.9)]
    public void KeptPositionReturnsNull(double t) => Assert.Null(EditTimelineCalculator.PrevKeepEnd(t, Keeps));

    [Fact]
    public void NoKeepRangeBeforeReturnsNull()
    {
        TimeRange[] keeps = [new(5, 10)];
        Assert.Null(EditTimelineCalculator.PrevKeepEnd(2, keeps));
        Assert.Null(EditTimelineCalculator.PrevKeepEnd(2, []));
    }
}

public class CutEdgeTargetTests
{
    private static readonly CutSegment A = new() { Id = "a", Start = 10, End = 20 };
    private static readonly CutSegment B = new() { Id = "b", Start = 30, End = 40 };
    private static readonly CutSegment C = new() { Id = "c", Start = 50, End = 60 };
    private static readonly CutSegment[] Cuts = [A, B, C];

    private static CutEdgePick Pick(double current, CutEdge edge, params string[] selected)
        => CutEdgeTarget.Pick(Cuts, selected, current, edge);

    private static void AssertPick(CutEdgePick p, CutSegment cut, double start, double end)
    {
        Assert.True(p.Success, p.Error);
        Assert.Same(cut, p.Cut);
        Assert.Equal(start, p.Start);
        Assert.Equal(end, p.End);
    }

    [Fact]
    public void SingleSelectedCutWinsOverContainingCut()
    {
        AssertPick(Pick(15, CutEdge.Start, "b"), B, 15, 40);   // 再生位置は A の中だが、選択中の B を広げる
        AssertPick(Pick(35, CutEdge.End, "b"), B, 30, 35);
        AssertPick(Pick(45, CutEdge.End, "b"), B, 30, 45);
    }

    [Fact]
    public void SelectedCutWithInvertedResultIsRejected()
    {
        var p = Pick(45, CutEdge.Start, "b");
        Assert.False(p.Success);
        Assert.Same(B, p.Cut);
        Assert.Contains("開始を合わせられません", p.Error);
        Assert.Contains("終了を合わせられません", Pick(25, CutEdge.End, "b").Error);
    }

    [Fact]
    public void MultipleOrNonCutSelectionFallsBackToContainingCut()
    {
        AssertPick(Pick(15, CutEdge.Start, "b", "c"), A, 15, 20);
        AssertPick(Pick(15, CutEdge.End, "candidate-1"), A, 10, 15);
        AssertPick(Pick(15, CutEdge.End), A, 10, 15);
    }

    [Fact]
    public void NearestCutBeforeForEndAndAfterForStart()
    {
        AssertPick(Pick(25, CutEdge.End), A, 10, 25);
        AssertPick(Pick(25, CutEdge.Start), B, 25, 40);
        AssertPick(Pick(45, CutEdge.End), B, 30, 45);
        AssertPick(Pick(5, CutEdge.Start), A, 5, 20);
    }

    [Fact]
    public void NoTargetGivesReason()
    {
        var w = Pick(5, CutEdge.End);
        Assert.False(w.Success);
        Assert.Null(w.Cut);
        Assert.Contains("前で終わるカット", w.Error);
        var q = Pick(65, CutEdge.Start);
        Assert.False(q.Success);
        Assert.Contains("後で始まるカット", q.Error);
        Assert.False(CutEdgeTarget.Pick([], [], 10, CutEdge.Start).Success);
    }

    [Fact]
    public void TooShortResultIsRejected()
    {
        var q = Pick(19.97, CutEdge.Start);
        Assert.False(q.Success);
        Assert.Contains("0.05 秒未満", q.Error);
        Assert.False(Pick(10.02, CutEdge.End).Success);
        AssertPick(Pick(19.95, CutEdge.Start), A, 19.95, 20); // ちょうど 0.05 秒は可
    }
}

public class SubtitleAlignTests
{
    private static SubtitleSegment S(double start, double end, string text = "字幕") => new() { Start = start, End = end, Text = text };

    [Fact]
    public void SubtitleAtPlayheadIsPreferred()
    {
        var s1 = S(1, 3); var s2 = S(5, 8); var s3 = S(10, 12);
        Assert.Same(s2, SubtitleAlign.Pick([s1, s2, s3], 6, draftStart: 10, shapeStart: 10));
    }

    [Fact]
    public void WithoutSubtitleAtPlayheadClosestStartToDraftIsUsed()
    {
        var s1 = S(1, 3); var s2 = S(5, 8); var s3 = S(10, 12);
        Assert.Same(s3, SubtitleAlign.Pick([s1, s2, s3], null, draftStart: 9, shapeStart: 1));   // 削除部分にいる
        Assert.Same(s3, SubtitleAlign.Pick([s1, s2, s3], 4, draftStart: 9, shapeStart: 1));      // 字幕の出ていない位置
        Assert.Same(s2, SubtitleAlign.Pick([s3, s1, s2], null, draftStart: null, shapeStart: 4)); // 入力欄が解釈できない → 図形の開始
    }

    [Fact]
    public void TieChoosesEarlierStartThenListOrder()
    {
        var t1 = S(3, 4); var t2 = S(5, 6);
        Assert.Same(t1, SubtitleAlign.Pick([t2, t1], null, 4, 0));
        var u1 = S(5, 6, "一"); var u2 = S(5, 7, "二");
        Assert.Same(u1, SubtitleAlign.Pick([u1, u2], null, 5, 0));
        Assert.Same(u1, SubtitleAlign.Pick([u1, u2], 5.5, null, 0)); // 重なって出ている字幕も開始の早い・先のもの
    }

    [Fact]
    public void NoSubtitlesGivesNull() => Assert.Null(SubtitleAlign.Pick([], 1, 1, 1));

    [Fact]
    public void AlignStartKeepsEndWhenPossible()
    {
        var r = SubtitleAlign.AlignStart(S(5, 8), 2, 7, 60);
        Assert.Equal(5, r.Start);
        Assert.Equal(7, r.End);
    }

    [Fact]
    public void AlignStartKeepsLengthWhenStartReachesEnd()
    {
        var r = SubtitleAlign.AlignStart(S(5, 8), 2, 4, 60);
        Assert.Equal((5.0, 7.0), (r.Start, r.End));
        var same = SubtitleAlign.AlignStart(S(5, 8), 2, 5, 60);   // 開始＝終了になる場合も長さを保つ
        Assert.Equal((5.0, 8.0), (same.Start, same.End));
    }

    [Fact]
    public void AlignStartClampsEndToOutputDuration()
    {
        var r = SubtitleAlign.AlignStart(S(9, 10), 2, 4, 10);
        Assert.Equal((9.0, 10.0), (r.Start, r.End));
    }

    [Fact]
    public void AlignRangeUsesSubtitleTimes()
    {
        var r = SubtitleAlign.AlignRange(S(5.25, 8.5, "保存します"));
        Assert.Equal((5.25, 8.5), (r.Start, r.End));
        Assert.Equal("字幕「保存します」に合わせました", r.Description);
    }

    [Fact]
    public void DescriptionTruncatesAndJoinsLines()
    {
        Assert.Equal("字幕「一行目 二行目」に合わせました", SubtitleAlign.Describe(S(0, 1, "一行目\n二行目")));
        Assert.Equal("字幕「一行目 二行目」に合わせました", SubtitleAlign.Describe(S(0, 1, "一行目\r\n二行目")));
        Assert.Equal("字幕「あいうえおかきくけこさしすせそたちつてと…」に合わせました",
            SubtitleAlign.Describe(S(0, 1, "あいうえおかきくけこさしすせそたちつてとなに")));
        Assert.Equal("字幕「あいうえおかきくけこさしすせそたちつてと」に合わせました",
            SubtitleAlign.Describe(S(0, 1, "あいうえおかきくけこさしすせそたちつてと"))); // ちょうど 20 文字は切らない
    }
}

public class CandidateBuilderIdleTests
{
    private static readonly AnalysisSettings Standard = AnalysisSettings.FromPreset(AnalysisPreset.Standard);

    private static List<CutCandidate> Build(TimeRange[] freezes, TimeRange[] silences, TimeRange[]? idle,
        bool hasAudio = true, AnalysisSettings? settings = null, double duration = 60)
        => CandidateBuilder.Build(freezes, silences, duration, hasAudio, settings ?? Standard, idle);

    private static object[] Snapshot(IEnumerable<CutCandidate> list)
        => list.Select(c => (object)(c.Start, c.End, c.Kind, c.Confidence, c.Reason, c.SuggestedAction, c.KeepSeconds, c.Source)).ToArray();

    [Fact]
    public void NullAndEmptyIdleEqualOldOutput()
    {
        TimeRange[] freezes = [new(0, 5), new(10, 17.6), new(30, 40), new(55, 60)];
        TimeRange[] silences = [new(9, 18), new(20, 26)];
        foreach (var settings in new[] { Standard, AnalysisSettings.FromPreset(AnalysisPreset.Aggressive), AnalysisSettings.FromPreset(AnalysisPreset.Careful) })
        {
            foreach (var hasAudio in new[] { true, false })
            {
                var old = Snapshot(CandidateBuilder.Build(freezes, silences, 60, hasAudio, settings));
                Assert.Equal(old, Snapshot(CandidateBuilder.Build(freezes, silences, 60, hasAudio, settings, null)));
                Assert.Equal(old, Snapshot(CandidateBuilder.Build(freezes, silences, 60, hasAudio, settings, [])));
            }
        }
    }

    [Fact]
    public void FreezeMostlyIdleBecomesIdleAndFreeze()
    {
        var c = Assert.Single(Build([new(10, 17.6)], [], [new(9, 18)]));
        Assert.Equal(CandidateKind.IdleAndFreeze, c.Kind);
        Assert.Equal(0.9, c.Confidence);
        Assert.Equal("7.6秒の静止＋入力なし", c.Reason);
        Assert.Equal(12.0, c.Start);   // 余白と残す秒数は静止の候補と同じ
        Assert.Equal(17.1, c.End);
    }

    [Fact]
    public void IdleAndFreezeWithSilenceKeepsConfidenceAndNamesBoth()
    {
        var c = Assert.Single(Build([new(10, 17.6)], [new(9, 18)], [new(9, 18)]));
        Assert.Equal(CandidateKind.IdleAndFreeze, c.Kind);
        Assert.Equal(0.9, c.Confidence);
        Assert.Equal("7.6秒の静止＋無音＋入力なし", c.Reason);
    }

    [Fact]
    public void IdleAndFreezeWithoutAudioAndLongBonus()
    {
        var noAudio = Assert.Single(Build([new(20, 25)], [], [new(20, 25)], hasAudio: false));
        Assert.Equal(CandidateKind.IdleAndFreeze, noAudio.Kind);
        Assert.Equal(0.9, noAudio.Confidence);
        var longOne = Assert.Single(Build([new(10, 22)], [], [new(10, 22)]));
        Assert.Equal(0.95, longOne.Confidence);
    }

    [Fact]
    public void FreezePartlyIdleStaysFreeze()
    {
        // 入力なしは静止の 66% だけ → 従来の「静止」。入力なしの範囲は静止と半分以上重なるので Idle にもしない。
        var c = Assert.Single(Build([new(10, 17.6)], [], [new(10, 15)]));
        Assert.Equal(CandidateKind.Freeze, c.Kind);
        Assert.Equal(0.4, c.Confidence);
    }

    [Fact]
    public void IdleCandidateUsesLongerEndMargin()
    {
        var c = Assert.Single(Build([], [], [new(30, 35)]));
        Assert.Equal(CandidateKind.Idle, c.Kind);
        Assert.Equal(0.55, c.Confidence);
        Assert.Equal("5.0秒の入力なし（画面は変化あり）", c.Reason);
        Assert.Equal(32.0, c.Start);   // 余白 0.5 ＋ 先頭に残す 1.5
        Assert.Equal(34.0, c.End);     // 末尾は max(余白 0.5, 1.0) = 1.0
        Assert.Equal(SegmentAction.Remove, c.SuggestedAction);
        Assert.Equal(SegmentSource.LocalAnalysis, c.Source);
    }

    [Fact]
    public void IdleEndMarginUsesEdgeMarginWhenLarger()
    {
        var s = Standard with { Preset = AnalysisPreset.Custom, EdgeMarginSeconds = 1.5 };
        var c = Assert.Single(Build([], [], [new(30, 40)], settings: s));
        Assert.Equal(33.0, c.Start);
        Assert.Equal(38.5, c.End);
    }

    [Fact]
    public void IdleAtVideoEdgesHasNoMargin()
    {
        var c = Build([], [], [new(0, 6), new(50, 60)]);
        Assert.Equal(2, c.Count);
        Assert.Equal((1.5, 5.0), (c[0].Start, c[0].End));
        Assert.Equal((52.0, 60.0), (c[1].Start, c[1].End));
        Assert.Equal(0.6, c[1].Confidence); // 10 秒以上は +0.05
    }

    [Fact]
    public void ShortIdleIsSkipped()
    {
        Assert.Empty(Build([], [], [new(30, 32.5)]));   // FreezeMinSeconds（3 秒）未満
        Assert.Empty(Build([], [], [new(30, 33.2)]));   // 削除が 0.5 秒未満（32.0〜32.2）
    }

    [Fact]
    public void IdleOverlappingFreezeByHalfIsExcluded()
    {
        var c = Assert.Single(Build([new(30, 34)], [], [new(29, 36)]));   // 重なり 4/7 秒
        Assert.Equal(CandidateKind.IdleAndFreeze, c.Kind);

        var both = Build([new(30, 33.5)], [], [new(29, 40)]);   // 重なり 3.5/11 秒 → Idle も作る
        Assert.Equal(2, both.Count);
        Assert.Contains(both, x => x.Kind == CandidateKind.Idle && x.Confidence == 0.6);
        Assert.Contains(both, x => x.Kind == CandidateKind.IdleAndFreeze);
    }
}
