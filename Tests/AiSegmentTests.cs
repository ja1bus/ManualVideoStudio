using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

/// <summary>区間表（v1.21。設計 4.1）。</summary>
public class SegmentTableBuilderTests
{
    private static SegmentTable Build(double duration, IEnumerable<CutSegment>? cuts = null, IEnumerable<CutCandidate>? candidates = null,
        IReadOnlyList<TimeRange>? idle = null, IReadOnlyList<double>? markers = null, IReadOnlyList<double>? scenes = null)
        => SegmentTableBuilder.Build(duration, cuts ?? [], candidates ?? [], idle ?? [], markers ?? [], scenes ?? []);

    private static double[] Bounds(SegmentTable t) => [.. t.Rows.Select(r => r.Start), t.Rows[^1].End];

    [Fact]
    public void EmptyDurationGivesEmptyTable()
    {
        Assert.Equal(0, Build(0).Count);
        Assert.Equal("", Build(0).ToPromptText());
    }

    [Fact]
    public void NoBoundariesGivesOneSegment()
    {
        var t = Build(20);
        var row = Assert.Single(t.Rows);
        Assert.Equal((1, 0.0, 20.0), (row.Id, row.Start, row.End));
    }

    [Fact]
    public void SplitsSegmentsLongerThanMaximumEvenly()
    {
        // 画面変化の無い 30 秒は 15 秒ずつ 2 区間、70 秒は 17.5 秒ずつ 4 区間。
        Assert.Equal([0, 15, 30], Bounds(Build(30)));
        Assert.Equal([0, 17.5, 35, 52.5, 70], Bounds(Build(70)));
        // 境目のある動画は、長い区間だけを分ける（[10, 55] の 45 秒 → 15 秒ずつ 3 区間）。
        Assert.Equal([0, 10, 25, 40, 55], Bounds(Build(55, scenes: [10])));
        Assert.Equal(SegmentTableBuilder.DefaultMaxSegmentSeconds, Build(55).MaxSegmentSeconds);
    }

    [Fact]
    public void SplitKeepsFeaturesPerPiece()
    {
        // [0, 40] を 20 秒ずつに分けても、特徴は分けた区間ごとに判定する（入力なし [1, 39] は両方、マーカー 30 秒は後ろだけ）。
        // 入力なしの端（1・39）は先頭・末尾から 2 秒未満なので境目にならない。
        var t = Build(40, idle: [new TimeRange(1, 39)], markers: [30]);
        Assert.Equal([0, 20, 40], Bounds(t));
        Assert.Equal(SegmentFeature.Idle, t.Rows[0].Features);
        Assert.Equal(SegmentFeature.Idle | SegmentFeature.Marker, t.Rows[1].Features);
    }

    [Fact]
    public void LongVideoWithoutScenesRaisesMaximumToStayWithinCap()
    {
        // 1 時間（3600 秒）を 20 秒ずつにすると 180 区間 → 30 秒、45 秒…と上げて 120 以下にする。
        var t = Build(3600);
        Assert.InRange(t.Count, 1, SegmentTableBuilder.MaxSegments);
        Assert.True(t.MaxSegmentSeconds > SegmentTableBuilder.DefaultMaxSegmentSeconds);
        Assert.All(t.Rows, r => Assert.True(r.Duration <= t.MaxSegmentSeconds + 1e-9));
        // 1 段階前の最長の長さでは上限を超えていた。
        Assert.True(Math.Ceiling(3600 / (t.MaxSegmentSeconds / 1.5)) > SegmentTableBuilder.MaxSegments);
        Assert.Equal(Enumerable.Range(1, t.Count), t.Rows.Select(r => r.Id));
    }

    [Fact]
    public void SplitNeverMakesPiecesShorterThanMinimum()
    {
        // 最短の長さが最長の長さより大きいとき（min 25 秒）、50 秒は 2 区間まで（20 秒ずつの 3 区間にはしない）。
        Assert.Equal([0, 25, 50], SegmentTableBuilder.SplitLong([0, 50], 25, 20));
        Assert.Equal([0, 10, 20], SegmentTableBuilder.SplitLong([0, 10, 20], 2, 20));
        // ちょうど最長の長さの区間は分けない。
        Assert.Equal([0, 20], SegmentTableBuilder.SplitLong([0, 20], 2, 20));
    }

    [Fact]
    public void DropsBoundariesCloserThanMinimum()
    {
        // 1 は先頭から 2 秒未満、6 は 5 から 2 秒未満。
        var t = Build(20, scenes: [1, 5, 6, 9]);
        Assert.Equal([0, 5, 9, 20], Bounds(t));
        Assert.Equal([1, 2, 3], t.Rows.Select(r => r.Id));
    }

    [Fact]
    public void HigherPriorityBoundaryWinsEvenIfLater()
    {
        // 画面変化 9.5 は時刻が先でも、確定カットの端 10 が優先される。
        var t = Build(30, cuts: [new CutSegment { Start = 10, End = 20 }], scenes: [9.5, 15, 21]);
        Assert.Equal([0, 10, 15, 20, 30], Bounds(t));
    }

    [Fact]
    public void PriorityOrderCutsCandidatesIdleScenes()
    {
        var t = Build(40,
            cuts: [new CutSegment { Start = 10, End = 12.5 }],
            candidates: [new CutCandidate { Start = 11, End = 20, Source = SegmentSource.LocalAnalysis }],
            idle: [new TimeRange(19, 30)],
            scenes: [29, 35]);
        // 候補の 11 は確定カットの端に近いので捨て、入力なしの 19 は候補の端 20 に近いので捨てる。画面変化 29 は入力なしの 30 に負ける。
        Assert.Equal([0, 10, 12.5, 20, 30, 35, 40], Bounds(t));
    }

    [Fact]
    public void IgnoresAcceptedAndAiCandidates()
    {
        var t = Build(30, candidates:
        [
            new CutCandidate { Start = 5, End = 15, Source = SegmentSource.LocalAnalysis, Status = CandidateStatus.Accepted },
            new CutCandidate { Start = 8, End = 22, Source = SegmentSource.Ai, Kind = CandidateKind.Ai },
        ]);
        Assert.Equal([0, 15, 30], Bounds(t)); // 候補の端は境目にしない（30 秒は最長の長さで等分するだけ）
        Assert.All(t.Rows, r => Assert.Equal(SegmentFeature.None, r.Features));
    }

    [Fact]
    public void CapsAt120SegmentsByRaisingMinimum()
    {
        var scenes = Enumerable.Range(1, 299).Select(i => i * 2.0).ToList();
        var t = Build(600, scenes: scenes);
        Assert.InRange(t.Count, 1, SegmentTableBuilder.MaxSegments);
        Assert.True(t.MinSegmentSeconds > SegmentTableBuilder.DefaultMinSegmentSeconds);
        Assert.All(t.Rows, r => Assert.True(r.Duration >= t.MinSegmentSeconds - 1e-9));
        Assert.Equal(Enumerable.Range(1, t.Count), t.Rows.Select(r => r.Id));
        // 1.5 倍ずつなので、1 段階前の最短の長さでは上限を超えていた。
        Assert.True(t.MinSegmentSeconds / 1.5 >= SegmentTableBuilder.DefaultMinSegmentSeconds);
    }

    [Fact]
    public void Features()
    {
        var t = Build(40,
            cuts: [new CutSegment { Start = 20, End = 30 }],
            candidates: [new CutCandidate { Start = 10, End = 20, Source = SegmentSource.LocalAnalysis }],
            idle: [new TimeRange(10, 20)],
            markers: [5, 40],
            scenes: [10, 30]);
        Assert.Equal([0, 10, 20, 30, 40], Bounds(t));
        Assert.Equal(SegmentFeature.Marker, t.Rows[0].Features);
        Assert.Equal(SegmentFeature.Still | SegmentFeature.Idle, t.Rows[1].Features);
        Assert.Equal(SegmentFeature.AlreadyCut, t.Rows[2].Features);
        // 末尾ちょうどのマーカーは最後の区間に入る。
        Assert.Equal(SegmentFeature.Marker, t.Rows[3].Features);
        Assert.Equal("静止・入力なし", t.Rows[1].FeatureText);
    }

    [Fact]
    public void FeatureThresholds()
    {
        // 区間 [0, 10] に対して、候補 1.5 秒（15%）は静止にならず、入力なし 7 秒（70%）も入力なしにならない。
        // 範囲の端が境目にならないよう、端は区間の端から 2 秒未満に置く。
        var t1 = Build(10,
            candidates: [new CutCandidate { Start = 8.5, End = 12.5, Source = SegmentSource.LocalAnalysis }],
            idle: [new TimeRange(1.5, 8.5)]);
        Assert.Single(t1.Rows);
        Assert.Equal(SegmentFeature.None, t1.Rows[0].Features);

        var t2 = Build(10,
            candidates: [new CutCandidate { Start = 1.5, End = 6.5, Source = SegmentSource.LocalAnalysis }],
            idle: [new TimeRange(1.5, 10)]);
        Assert.Equal([0, 6.5, 10], Bounds(t2)); // 1.5 は先頭に近いので捨てる
        // [0, 6.5]: 候補 5/6.5 ≥ 50% → 静止。入力なし 5/6.5 < 80%。
        Assert.Equal(SegmentFeature.Still, t2.Rows[0].Features);
        // [6.5, 10]: 入力なし 100%。
        Assert.Equal(SegmentFeature.Idle, t2.Rows[1].Features);
    }

    [Fact]
    public void AlreadyCutNeedsAlmostFullCover()
    {
        // 確定カットの端は必ず境目になるため、一部だけ残るのは短縮のとき。先頭 0.05 秒だけ残す（99.5% 削除）はカット済み。
        var t = Build(30, cuts: [new CutSegment { Start = 10, End = 20, Action = SegmentAction.Shorten, KeepSeconds = 0.05 }]);
        Assert.Equal(SegmentFeature.AlreadyCut, t.Rows[1].Features);
        // 短縮（先頭 3 秒を残す）は 70% しか削除されないのでカット済みにしない。
        var s = Build(30, cuts: [new CutSegment { Start = 10, End = 20, Action = SegmentAction.Shorten, KeepSeconds = 3 }]);
        Assert.Equal(SegmentFeature.None, s.Rows[1].Features);
    }

    [Fact]
    public void PromptTextFormat()
    {
        var t = Build(250.2, cuts: [new CutSegment { Start = 205.4, End = 221.3 }],
            candidates: [new CutCandidate { Start = 205.4, End = 221.3, Source = SegmentSource.LocalAnalysis }],
            idle: [new TimeRange(205.4, 221.3)]);
        var lines = t.ToPromptText().Split('\n');
        // [0, 205.4] は 20 秒以下の 11 区間、[221.3, 250.2] は 2 区間に等分される。
        Assert.Equal(14, lines.Length);
        Assert.Equal("1 | 00:00:00〜00:00:19 | 19秒 | -", lines[0]);
        Assert.Equal("12 | 00:03:25〜00:03:41 | 16秒 | 静止・入力なし・カット済み", lines[11]);
        Assert.Equal("13 | 00:03:41〜00:03:56 | 15秒 | -", lines[12]);
        Assert.Equal("14 | 00:03:56〜00:04:10 | 14秒 | -", lines[13]);
        Assert.Equal("01:00:05", SegmentTable.Clock(3605));
    }

    [Fact]
    public void ToHistoryKeepsIdsAndTimes()
    {
        var t = Build(30, scenes: [10.12345]);
        var h = t.ToHistory();
        Assert.Equal(2, h.Count);
        Assert.Equal((1, 0.0, 10.123), (h[0].Id, h[0].Start, h[0].End));
        Assert.Equal((2, 10.123, 30.0), (h[1].Id, h[1].Start, h[1].End));
    }

    [Fact]
    public void FromProjectUsesProjectData()
    {
        var p = TestProjects.Create(30);
        p.SceneChanges.AddRange([10, 20]);
        p.CutSegments.Add(new CutSegment { Start = 20, End = 30 });
        var t = SegmentTableBuilder.FromProject(p);
        Assert.Equal(3, t.Count);
        Assert.Equal(SegmentFeature.AlreadyCut, t.Rows[2].Features);
    }
}

/// <summary>プロンプトの区間表の変数と既定テンプレート（v1.21。設計 4.3）。</summary>
public class SegmentPromptTests
{
    [Fact]
    public void BuildWithoutTableRendersEmpty()
    {
        var v = PromptVariables.Build(TestProjects.Create(30), "日本語");
        Assert.Equal("", v[PromptVariables.SegmentTable]);
        Assert.Equal("0", v[PromptVariables.SegmentCount]);
    }

    [Fact]
    public void BuildWithTable()
    {
        var p = TestProjects.Create(30);
        p.SceneChanges.Add(10);
        var table = SegmentTableBuilder.FromProject(p);
        var v = PromptVariables.Build(p, "日本語", table);
        Assert.Equal("2", v[PromptVariables.SegmentCount]);
        var r = PromptRenderer.Render(DefaultPromptTemplates.CutAnalysis, v);
        Assert.False(r.HasWarnings);
        Assert.Contains("1 | 00:00:00〜00:00:10 | 10秒 | -\n2 | 00:00:10〜00:00:30 | 20秒 | -", r.Text.ReplaceLineEndings("\n"));
        Assert.Contains("長さ 00:00:30.000", r.Text);
        Assert.Contains("reason は日本語で", r.Text);
    }

    [Fact]
    public void DefaultCutAnalysisTemplateUsesSegments()
    {
        var t = DefaultPromptTemplates.CutAnalysis;
        Assert.Contains("{{SEGMENT_TABLE}}", t);
        Assert.Contains("{{LANGUAGE}}", t);
        Assert.Contains("{{VIDEO_DURATION}}", t);
        Assert.Contains("\"segments\"", t);
        Assert.Empty(PromptVariables.MissingRequired(AiPurpose.CutAnalysis, t));
        // 編集済みで {{SEGMENT_TABLE}} が無いテンプレートも警告なしで動く（必須にしない）。
        Assert.Empty(PromptVariables.MissingRequired(AiPurpose.CutAnalysis, "{{CUT_SEGMENTS}}"));
        Assert.Contains(PromptVariables.All, v => v.Name == PromptVariables.SegmentTable);
        Assert.Contains(PromptVariables.All, v => v.Name == PromptVariables.SegmentCount);
    }
}

/// <summary>区間方式の回答の解析（v1.21。設計 4.4）。</summary>
public class SegmentParseTests
{
    [Fact]
    public void ParsesSegmentsAndIgnoresSteps()
    {
        var r = AiResponseParser.ParseCutAnalysis("""
            ```json
            {
              "steps": [ { "time": "00:00:05", "action": "設定画面を開く" } ],
              "segments": [
                { "id": 12, "category": "Wait", "confidence": 0.9, "reason": "完了待ち" },
                { "id": "20", "category": "mistake", "confidence": 0.7, "reason": "やり直し", "start": "00:05:12", "end": "00:05:20" },
                { "id": "x", "category": "wait" },
                "bad",
              ]
            }
            ```
            """);
        Assert.True(r.Success);
        Assert.True(r.IsSegmentMode);
        Assert.Empty(r.Cuts);
        Assert.Equal(2, r.Count);
        Assert.Equal(new ParsedSegment(12, "wait", 0.9, "完了待ち"), r.Segments[0]);
        Assert.Equal(new ParsedSegment(20, "mistake", 0.7, "やり直し", "00:05:12", "00:05:20"), r.Segments[1]);
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void EmptySegmentsIsValid()
    {
        var r = AiResponseParser.ParseCutAnalysis("""{ "steps": [], "segments": [] }""");
        Assert.True(r.Success);
        Assert.True(r.IsSegmentMode);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void FallsBackToCuts()
    {
        var r = AiResponseParser.ParseCutAnalysis("""{ "cuts": [ { "start": "00:00:01.000", "end": "00:00:03.000", "action": "remove", "confidence": 0.5, "reason": "待ち" } ] }""");
        Assert.True(r.Success);
        Assert.False(r.IsSegmentMode);
        Assert.Single(r.Cuts);
        Assert.Equal("00:00:01.000", r.Cuts[0].Start);

        var array = AiResponseParser.ParseCutAnalysis("""[ { "start": "1", "end": "2", "action": "remove" } ]""");
        Assert.True(array.Success);
        Assert.False(array.IsSegmentMode);
        Assert.Single(array.Cuts);
    }

    [Fact]
    public void NeitherSegmentsNorCutsFails()
    {
        var r = AiResponseParser.ParseCutAnalysis("""{ "steps": [] }""");
        Assert.False(r.Success);
        Assert.Contains("segments", r.Error);
        Assert.False(AiResponseParser.ParseCutAnalysis("ただの文章").Success);
    }
}

/// <summary>区間方式の取り込み・時刻方式の境目への寄せ（v1.21。設計 4.4）。</summary>
public class SegmentImportTests
{
    private static readonly List<AiSegment> Table =
    [
        new() { Id = 1, Start = 0, End = 10 },
        new() { Id = 2, Start = 10, End = 20 },
        new() { Id = 3, Start = 20, End = 30 },
        new() { Id = 4, Start = 30, End = 40 },
        new() { Id = 5, Start = 40, End = 50 },
    ];

    private static List<AiCutRow> Import(IReadOnlyList<ParsedSegment> items, IEnumerable<CutSegment>? cuts = null, double keep = 1.5,
        IReadOnlyList<AiSegment>? table = null)
        => AiCutImporter.ValidateSegments(items, table ?? Table, cuts ?? [], 50, keep);

    private static ParsedSegment Seg(int id, string category = "mistake", double? conf = 0.8, string reason = "理由", string? start = null, string? end = null)
        => new(id, category, conf, reason, start, end);

    [Fact]
    public void UnknownIdIsError()
    {
        var rows = Import([Seg(99)]);
        var row = Assert.Single(rows);
        Assert.False(row.IsValid);
        Assert.False(row.Include);
        Assert.Contains("区間 99", row.Note);
        Assert.Empty(AiCutImporter.ToCandidates(rows, "h"));
    }

    [Fact]
    public void DuplicateIdIsExcluded()
    {
        var rows = Import([Seg(1, reason: "a"), Seg(1, reason: "b")]);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Include);
        Assert.Equal("［やり直し］a", rows[0].Reason);
        Assert.True(rows[1].Excluded);
        Assert.False(rows[1].IsValid);
        Assert.Contains("重複", rows[1].Note);
        Assert.Single(AiCutImporter.ToCandidates(rows, "h"));
    }

    [Fact]
    public void AlreadyCutSegmentIsExcluded()
    {
        var rows = Import([Seg(5), Seg(3)], cuts: [new CutSegment { Start = 39.95, End = 50 }]);
        Assert.Equal(2, rows.Count);
        Assert.Equal([3], rows[0].SegmentIds);
        Assert.True(rows[0].IsValid);
        Assert.Equal([5], rows[1].SegmentIds);
        Assert.True(rows[1].Excluded);
        Assert.Contains("カット済み", rows[1].Note);
        Assert.Equal((40.0, 50.0), (rows[1].Start, rows[1].End));
    }

    [Fact]
    public void StartEndAreClampedIntoSegment()
    {
        // 29 は区間 4（30〜40）の 1 秒外まで → 区間の中へ収める。
        var row = Assert.Single(Import([Seg(4, start: "00:00:29", end: "00:00:35")]));
        Assert.Equal((30.0, 35.0), (row.Start, row.End));
        Assert.Empty(row.Warnings);
    }

    [Fact]
    public void StartEndStickingOutUseWholeSegment()
    {
        var row = Assert.Single(Import([Seg(4, start: "00:00:25", end: "00:00:35")]));
        Assert.Equal((30.0, 40.0), (row.Start, row.End));
        Assert.Contains("区間全体", row.Note);
        var bad = Assert.Single(Import([Seg(4, start: "あ")]));
        Assert.Equal((30.0, 40.0), (bad.Start, bad.End));
        Assert.Contains("区間全体", bad.Note);
    }

    [Fact]
    public void ConsecutiveSameCategoryIsMerged()
    {
        var rows = Import([Seg(3, conf: 0.6, reason: "b"), Seg(2, conf: 0.9, reason: "a"), Seg(4, "hesitation", 0.5, "c")]);
        Assert.Equal(2, rows.Count);
        var m = rows[0];
        Assert.Equal([2, 3], m.SegmentIds);
        Assert.Equal((10.0, 30.0), (m.Start, m.End));
        Assert.Equal(0.6, m.Confidence);
        Assert.Equal("［やり直し］a；b", m.Reason);
        Assert.Contains("まとめました", m.Note);
        Assert.Equal("［迷い］c", rows[1].Reason);
        Assert.Equal((30.0, 40.0), (rows[1].Start, rows[1].End));
        var candidates = AiCutImporter.ToCandidates(rows, "h");
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(SegmentSource.Ai, c.Source));
    }

    [Fact]
    public void NotMergedWhenGapOrNarrowed()
    {
        Assert.Equal(2, Import([Seg(1), Seg(3)]).Count);
        // 区間 2 を途中までに狭めると、区間 3 とはつながらない。
        Assert.Equal(2, Import([Seg(2, end: "00:00:15"), Seg(3)]).Count);
    }

    [Fact]
    public void WaitShiftsStartUnlessTooShort()
    {
        var row = Assert.Single(Import([Seg(2, "wait", reason: "完了待ち")]));
        Assert.Equal((11.5, 20.0), (row.Start, row.End));
        Assert.Equal("［待ち］完了待ち", row.Reason);
        Assert.Contains("先頭 1.5 秒を残しました", row.Note);

        var shortTable = new List<AiSegment> { new() { Id = 1, Start = 0, End = 1.8 } };
        var s = Assert.Single(Import([Seg(1, "wait")], table: shortTable));
        Assert.Equal((0.0, 1.8), (s.Start, s.End));
        Assert.Contains("残していません", s.Note);
        Assert.True(s.IsValid);
    }

    [Fact]
    public void CategoryPrefixes()
    {
        var rows = Import([Seg(1, "wait", reason: "w"), Seg(2, "mistake", reason: "m"), Seg(3, "hesitation", reason: "h"),
            Seg(4, "unrelated", reason: "u"), Seg(5, "other", reason: "o")], keep: 0);
        Assert.Equal(["［待ち］w", "［やり直し］m", "［迷い］h", "［無関係］u", "o"], rows.Select(r => r.Reason));
        Assert.Contains("other", rows[4].Note);
        Assert.True(rows[4].IsValid);
    }

    [Fact]
    public void ConfidenceIsNormalized()
    {
        var rows = Import([Seg(1, conf: 80), Seg(3, conf: null)]);
        Assert.Equal(0.8, rows[0].Confidence, 6);
        Assert.Equal(0, rows[1].Confidence);
    }

    [Fact]
    public void SnapToSegmentBoundary()
    {
        Assert.Equal(10, AiCutImporter.SnapToSegmentBoundary(10.8, Table));
        Assert.Equal(20, AiCutImporter.SnapToSegmentBoundary(19.2, Table));
        Assert.Equal(15, AiCutImporter.SnapToSegmentBoundary(15, Table));
        Assert.Equal(11.2, AiCutImporter.SnapToSegmentBoundary(11.2, Table));
    }

    [Fact]
    public void TimeModeSnapsOnlyWhenSegmentsGiven()
    {
        ParsedCut cut = new("00:00:10.600", "00:00:19.500", "remove", null, 0.7, "待ち");
        var plain = Assert.Single(AiCutImporter.Validate([cut], 50, 1.5));
        Assert.Equal((10.6, 19.5), (plain.Start, plain.End));
        Assert.Empty(plain.Warnings);

        var snapped = Assert.Single(AiCutImporter.Validate([cut], 50, 1.5, Table));
        Assert.Equal((10.0, 20.0), (snapped.Start, snapped.End));
        Assert.Contains("境目に寄せました", snapped.Note);
        Assert.True(snapped.IsValid);

        var empty = Assert.Single(AiCutImporter.Validate([cut], 50, 1.5, []));
        Assert.Equal((10.6, 19.5), (empty.Start, empty.End));
    }
}

/// <summary>依頼用動画の帯（ASS）と FFmpeg 引数（v1.21。設計 4.2）。</summary>
public class AiRequestVideoTests
{
    [Fact]
    public void TimecodeAssHasOneEventPerSecond()
    {
        var segs = new List<AiSegment> { new() { Id = 1, Start = 0, End = 10.4 }, new() { Id = 2, Start = 10.4, End = 65.3 } };
        var ass = TimecodeAssBuilder.Build(65.3, segs, 1280, 764, 44);
        Assert.Contains("PlayResX: 1280\n", ass);
        Assert.Contains("PlayResY: 764\n", ass);
        Assert.Contains("Meiryo", ass);
        var events = ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();
        Assert.Equal(66, events.Count);
        Assert.Equal(66, TimecodeAssBuilder.EventCount(65.3));
        Assert.Equal(3, TimecodeAssBuilder.EventCount(3));
        Assert.EndsWith("区間 1　00:00:00", events[0]);
        Assert.EndsWith("区間 1　00:00:10", events[10]); // 10 秒の始まりはまだ区間 1
        Assert.EndsWith("区間 2　00:00:11", events[11]);
        Assert.StartsWith("Dialogue: 0,0:01:05.00,0:01:05.30,", events[65]);
        Assert.Contains("\\an4\\pos(14,742)", events[0]);
    }

    [Fact]
    public void TimecodeWithoutSegmentsShowsTimeOnly()
    {
        var ass = TimecodeAssBuilder.Build(2, [], 640, 400, 40);
        Assert.EndsWith("}00:00:01", ass.Split('\n').Last(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1280, 720, 1280, 720, 44, false)]
    [InlineData(1920, 1080, 1920, 1080, 64, false)]
    [InlineData(2560, 1440, 1920, 1080, 64, true)]
    [InlineData(3840, 1600, 1920, 800, 48, true)]
    [InlineData(640, 360, 640, 360, 40, false)]
    [InlineData(1281, 721, 1280, 720, 44, false)]
    public void LayoutRules(int w, int h, int ew, int eh, int band, bool scaled)
    {
        var l = FFmpegArguments.AiRequestVideoSize(w, h);
        Assert.Equal((ew, eh, band, scaled), (l.VideoWidth, l.VideoHeight, l.BandHeight, l.Scaled));
        Assert.Equal(eh + band, l.Height);
        Assert.Equal(0, l.Height % 2);
    }

    [Fact]
    public void ArgumentsWithoutScale()
    {
        var meta = new VideoMetadata { Width = 1280, Height = 720, Duration = 30, Fps = 30, HasAudio = true, HasVideo = true };
        var args = FFmpegArguments.AiRequestVideo(@"C:\v\in.mp4", meta, "timecodes.ass", @"C:\o\out.partial.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.Equal("fps=10,pad=1280:764:0:0:black,subtitles=filename=timecodes.ass", vf);
        Assert.Contains("28", args);
        Assert.Contains("veryfast", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("96k", args);
        Assert.Contains("pipe:1", args);
        Assert.Equal(@"C:\o\out.partial.mp4", args[^1]);
        Assert.DoesNotContain("-filter_complex", args); // カットを適用しない
    }

    [Fact]
    public void ArgumentsWithScaleAndNoAudio()
    {
        var meta = new VideoMetadata { Width = 2560, Height = 1440, Duration = 30, Fps = 30, HasAudio = false, HasVideo = true };
        var args = FFmpegArguments.AiRequestVideo("in.mp4", meta, "timecodes.ass", "out.mp4");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("scale=1920:1080:", vf);
        Assert.Contains("pad=1920:1144:0:0:black", vf);
        Assert.Contains("-an", args);
        Assert.DoesNotContain("0:a:0", args);
    }

    [Fact]
    public void ArgumentsCropOddSize()
    {
        var meta = new VideoMetadata { Width = 1281, Height = 721, Duration = 30, Fps = 30, HasVideo = true };
        var args = FFmpegArguments.AiRequestVideo("in.mp4", meta, "timecodes.ass", "out.mp4");
        Assert.StartsWith(FFmpegArguments.EvenCrop + ",fps=10,", args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void StorePrefix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mvs-cutreq-" + Guid.NewGuid().ToString("N"));
        var store = new CaptionRequestStore(dir);
        var loc = new CaptionRequestLocation(dir);
        var now = new DateTime(2026, 11, 13, 10, 15, 0);
        Assert.Equal("動画_20261113_101500_gemini.mp4", Path.GetFileName(store.NewPath(loc, "動画", now)));
        Assert.Equal("cut-request_動画_20261113_101500_gemini.mp4", Path.GetFileName(store.NewPath(loc, "動画", now, AiRequestVideoService.FilePrefix)));
    }
}
