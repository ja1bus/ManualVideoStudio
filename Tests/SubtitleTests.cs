using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Tests;

public class SubtitleValidatorTests
{
    private static ValidationResult V(double s, double e, string text = "ボタンを押します", double duration = 60, params SubtitleSegment[] existing)
        => SubtitleValidator.Validate(s, e, text, duration, existing);

    [Fact] public void Valid() => Assert.True(V(1, 3).IsValid);
    [Fact] public void EmptyText() => Assert.False(V(1, 3, "  \n ").IsValid);
    [Fact] public void EndBeforeStart() => Assert.False(V(3, 1).IsValid);
    [Fact] public void TooShort() => Assert.False(V(1, 1.1).IsValid);
    [Fact] public void NegativeStart() => Assert.False(V(-0.5, 2).IsValid);
    [Fact] public void BeyondOutputDuration() => Assert.False(V(58, 61).IsValid);
    [Fact] public void UnknownDurationSkipsLengthCheck() => Assert.True(V(58, 610, duration: 0).IsValid);
    [Fact] public void TooManyLines() => Assert.False(V(1, 3, "1\n2\n3\n4").IsValid);
    [Fact] public void ThreeLinesAllowed() => Assert.True(V(1, 3, "1\n2\n3").IsValid);
    [Fact] public void TooLong() => Assert.False(V(1, 3, new string('あ', SubtitleValidator.MaxLength + 1)).IsValid);

    [Fact]
    public void OverlapIsRejectedButTouchingIsAllowed()
    {
        var other = new SubtitleSegment { Start = 5, End = 8, Text = "a" };
        Assert.False(V(7, 9, existing: other).IsValid);
        Assert.False(V(4, 6, existing: other).IsValid);
        Assert.True(V(8, 10, existing: other).IsValid);
        Assert.True(V(2, 5, existing: other).IsValid);
        Assert.True(SubtitleValidator.Validate(6, 7, "x", 60, [other], other.Id).IsValid); // 自分自身は除外
    }

    [Fact]
    public void NormalizeTextUnifiesNewlinesAndTrims()
        => Assert.Equal("1行目\n2行目", SubtitleValidator.NormalizeText("\r\n 1行目  \r\n2行目\r\n\r\n"));
}

public class SubtitleEditingTests
{
    private static ProjectEditor Confirmed(double duration = 60)
    {
        var ed = new ProjectEditor(TestProjects.Create(duration));
        Assert.True(ed.ConfirmCuts().Success);
        return ed;
    }

    [Fact]
    public void AddRequiresConfirmedCuts()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (r, seg) = ed.AddSubtitle(1, 3, "字幕");
        Assert.False(r.Success);
        Assert.Null(seg);
        Assert.Empty(ed.Project.SubtitleSegments);
    }

    [Fact]
    public void AddUsesStyleDefaultsAndSorts()
    {
        var ed = Confirmed();
        ed.SubtitleDefaults = new SubtitleStyle { FontSize = 64, Position = SubtitlePosition.Top };
        ed.AddSubtitle(10, 12, "後");
        var (r, seg) = ed.AddSubtitle(1, 3, "  前\r\n2行目  ");
        Assert.True(r.Success);
        Assert.Equal(SubtitlePosition.Top, seg!.Position);
        Assert.Equal(64, seg.FontSize);
        Assert.Equal("前\n2行目", seg.Text);
        Assert.Equal(["前\n2行目", "後"], ed.Project.SubtitleSegments.Select(s => s.Text));
    }

    [Fact]
    public void FontSizeIsSnappedToChoices()
    {
        var ed = Confirmed();
        var (_, seg) = ed.AddSubtitle(1, 3, "a", SubtitlePosition.Bottom, 50);
        Assert.Contains(seg!.FontSize, SubtitleStyle.FontSizes);
        Assert.Equal(48, seg.FontSize);
    }

    [Fact]
    public void AddIsValidatedAgainstOutputDuration()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        ed.AddCut(10, 40, SegmentAction.Remove, 0); // 出力 30 秒
        ed.ConfirmCuts();
        Assert.Equal(30, ed.OutputDuration, 3);
        Assert.False(ed.AddSubtitle(29, 31, "範囲外").Result.Success);
        Assert.True(ed.AddSubtitle(28, 30, "末尾").Result.Success);
    }

    [Fact]
    public void AddRemoveEditAreUndoable()
    {
        var ed = Confirmed();
        var (_, seg) = ed.AddSubtitle(1, 3, "最初");
        Assert.Equal("字幕の追加", ed.History.UndoLabel);

        Assert.True(ed.UpdateSubtitle(seg!.Id, 1, 3, "変更後", seg.Position, seg.FontSize).Success);
        Assert.Equal("字幕の文字の変更", ed.History.UndoLabel);
        Assert.True(ed.UpdateSubtitle(seg.Id, 2, 4, "変更後", seg.Position, seg.FontSize).Success);
        Assert.Equal("字幕の時刻の変更", ed.History.UndoLabel);
        Assert.True(ed.UpdateSubtitle(seg.Id, 2, 5, "変更後", seg.Position, seg.FontSize).Success);
        Assert.Equal("字幕の時刻の変更", ed.History.UndoLabel); // 終了時刻の変更

        ed.Undo();
        Assert.Equal(4, ed.Project.SubtitleSegments[0].End);
        ed.Undo();
        Assert.Equal(1, ed.Project.SubtitleSegments[0].Start);
        ed.Undo();
        Assert.Equal("最初", ed.Project.SubtitleSegments[0].Text);
        ed.Redo();
        Assert.Equal("変更後", ed.Project.SubtitleSegments[0].Text);

        Assert.True(ed.RemoveSubtitle(seg.Id).Success);
        Assert.Empty(ed.Project.SubtitleSegments);
        ed.Undo();
        Assert.Single(ed.Project.SubtitleSegments);
    }

    [Fact]
    public void UnchangedUpdateIsNotRecorded()
    {
        var ed = Confirmed();
        var (_, seg) = ed.AddSubtitle(1, 3, "a");
        var count = ed.History.UndoCount;
        Assert.True(ed.UpdateSubtitle(seg!.Id, 1, 3, "a", seg.Position, seg.FontSize).Success);
        Assert.Equal(count, ed.History.UndoCount);
    }

    [Fact]
    public void InvalidUpdateKeepsOriginal()
    {
        var ed = Confirmed();
        var (_, a) = ed.AddSubtitle(1, 3, "a");
        ed.AddSubtitle(5, 7, "b");
        var r = ed.UpdateSubtitle(a!.Id, 2, 6, "a", a.Position, a.FontSize); // b と重なる
        Assert.False(r.Success);
        Assert.Equal(3, ed.Project.SubtitleSegments[0].End);
    }

    [Fact]
    public void RemoveSeveralAtOnce()
    {
        var ed = Confirmed();
        var a = ed.AddSubtitle(1, 2, "a").Segment!;
        var b = ed.AddSubtitle(3, 4, "b").Segment!;
        ed.AddSubtitle(5, 6, "c");
        Assert.True(ed.RemoveSubtitles([a.Id, b.Id]).Success);
        Assert.Equal(["c"], ed.Project.SubtitleSegments.Select(s => s.Text));
        Assert.Equal("字幕の削除", ed.History.UndoLabel);
    }
}

/// <summary>カット → 字幕の編集段階とカットのロック（仕様 §40 のケース 1～4 を Core で確認）。</summary>
public class EditPhaseTests
{
    [Fact]
    public void Case1_CutConfirmSubtitleThenCutIsLocked()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        var (cutResult, cut) = ed.AddCut(5, 10, SegmentAction.Remove, 0);
        Assert.True(cutResult.Success);
        Assert.False(ed.IsCutLocked);

        Assert.True(ed.ConfirmCuts().Success);
        Assert.True(ed.IsCutLocked);
        Assert.Equal(EditPhase.Subtitle, ed.Project.EditPhase);
        Assert.True(ed.AddSubtitle(1, 3, "字幕").Result.Success);

        // すべてのカット操作が拒否され、カットは変わらない。
        Assert.False(ed.AddCut(20, 25, SegmentAction.Remove, 0).Result.Success);
        Assert.False(ed.AddCut(20, 25, SegmentAction.Shorten, 1).Result.Success);
        Assert.False(ed.UpdateCut(cut!.Id, 5, 12, SegmentAction.Remove, 0).Success);
        Assert.False(ed.UpdateCut(cut.Id, 5, 10, SegmentAction.Shorten, 1).Success);
        Assert.False(ed.RemoveCut(cut.Id).Success);
        Assert.Single(ed.Project.CutSegments);
        Assert.Equal(10, ed.Project.CutSegments[0].End);
    }

    [Fact]
    public void CandidateAcceptanceIsLockedToo()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCandidates([new CutCandidate { Start = 20, End = 30, SuggestedAction = SegmentAction.Remove }], "候補");
        ed.ConfirmCuts();
        var r = ed.AcceptCandidate(ed.Project.CutCandidates[0].Id);
        Assert.False(r.Success);
        Assert.Empty(ed.Project.CutSegments);
    }

    [Fact]
    public void Case2_ReturnWithSubtitlesFailsAndKeepsSubtitles()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.ConfirmCuts();
        ed.AddSubtitle(1, 3, "字幕");
        var r = ed.ReturnToCutEditing(); // 警告に相当（確認なしでは戻れない）→ キャンセル
        Assert.False(r.Success);
        Assert.Contains("字幕をすべて破棄", r.Message);
        Assert.True(ed.IsCutLocked);
        Assert.Single(ed.Project.SubtitleSegments);
    }

    [Fact]
    public void Case3_DiscardAllSubtitlesUnlocksCuts()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.AddCut(5, 10, SegmentAction.Remove, 0);
        ed.ConfirmCuts();
        ed.AddSubtitle(1, 3, "a");
        ed.AddSubtitle(4, 6, "b");

        Assert.True(ed.DiscardSubtitlesAndReturnToCutEditing().Success);
        Assert.Empty(ed.Project.SubtitleSegments);
        Assert.Equal(EditPhase.Cut, ed.Project.EditPhase);
        Assert.False(ed.IsCutLocked);
        Assert.True(ed.AddCut(20, 25, SegmentAction.Remove, 0).Result.Success);

        // 破棄は 1 回の Undo で戻せる（カットの追加を戻した後）。
        ed.Undo();
        ed.Undo();
        Assert.Equal(2, ed.Project.SubtitleSegments.Count);
        Assert.True(ed.IsCutLocked);
    }

    [Fact]
    public void Case4_WithoutSubtitlesCutEditingWorks()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        Assert.True(ed.AddCut(5, 10, SegmentAction.Remove, 0).Result.Success);
        ed.ConfirmCuts();
        Assert.True(ed.ReturnToCutEditing().Success); // 字幕 0 件なら確認なしで戻れる
        Assert.False(ed.IsCutLocked);
        Assert.True(ed.AddCut(20, 25, SegmentAction.Shorten, 1).Result.Success);
        Assert.Equal(2, ed.Project.CutSegments.Count);
    }

    [Fact]
    public void UndoOfConfirmReturnsToCutPhase()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.ConfirmCuts();
        Assert.Equal("カットの確定", ed.History.UndoLabel);
        ed.Undo();
        Assert.False(ed.IsCutLocked);
    }

    [Fact]
    public void CannotConfirmWhenEverythingIsRemoved()
    {
        var ed = new ProjectEditor(TestProjects.Create(10));
        ed.AddCut(0, 10, SegmentAction.Remove, 0);
        Assert.False(ed.ConfirmCuts().Success);
        Assert.False(ed.IsCutLocked);
    }
}

public class EditTimelineMappingTests
{
    // 60 秒の動画から 10～20 を削除、30～40 を短縮（2 秒残す）→ 残す区間 [0,10) [20,32) [40,60)
    private static IReadOnlyList<TimeRange> Keep() => EditTimelineCalculator.ComputeKeepRanges(60,
    [
        new CutSegment { Start = 10, End = 20, Action = SegmentAction.Remove },
        new CutSegment { Start = 30, End = 40, Action = SegmentAction.Shorten, KeepSeconds = 2 },
    ]);

    [Theory]
    [InlineData(5, 5)]
    [InlineData(15, 10)]   // 削除部分 → 直後に残る位置
    [InlineData(25, 15)]
    [InlineData(35, 22)]   // 短縮で消える部分
    [InlineData(45, 27)]
    [InlineData(70, 42)]   // 末尾より後
    public void MapToOutputClamped(double source, double output)
        => Assert.Equal(output, EditTimelineCalculator.MapToOutputClamped(source, Keep()), 3);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 20)]
    [InlineData(21, 31)]
    [InlineData(22, 40)]
    [InlineData(42, 60)]
    [InlineData(99, 60)]
    public void MapToSource(double output, double source)
        => Assert.Equal(source, EditTimelineCalculator.MapToSource(output, Keep()), 3);

    [Fact]
    public void RoundTripInsideKeptParts()
    {
        var keep = Keep();
        foreach (var t in new[] { 1.234, 9.9, 20.5, 31.9, 40.0, 59.5 })
            Assert.Equal(t, EditTimelineCalculator.MapToSource(EditTimelineCalculator.MapToOutputClamped(t, keep), keep), 3);
    }

    [Fact]
    public void OutputRangeAcrossCutIsSplit()
    {
        var parts = EditTimelineCalculator.MapOutputRangeToSource(8, 12, Keep());
        Assert.Equal([new TimeRange(8, 10), new TimeRange(20, 22)], parts);
    }

    [Fact]
    public void NextKeepStartAndIsRemoved()
    {
        var keep = Keep();
        Assert.Equal(20, EditTimelineCalculator.NextKeepStart(12, keep));
        Assert.Equal(40, EditTimelineCalculator.NextKeepStart(33, keep));
        Assert.Null(EditTimelineCalculator.NextKeepStart(5, keep));
        Assert.True(EditTimelineCalculator.IsRemoved(15, keep));
        Assert.False(EditTimelineCalculator.IsRemoved(31, keep));
        Assert.True(EditTimelineCalculator.IsRemoved(60, keep));
    }

    // v1.18.2: 「カットをスキップ」の再生で、削除区間の終端（79.689 秒）へ飛んだ直後に最後まで飛ぶ不具合。
    // プレーヤーの位置は TimeSpan 経由で 79.6889999 になり、IsRemoved は削除中・NextKeepStart は null と食い違っていた。
    private static IReadOnlyList<TimeRange> AlmaKeep() => EditTimelineCalculator.ComputeKeepRanges(370.565,
    [
        new CutSegment { Start = 59.124, End = 79.689, Action = SegmentAction.Remove },
        new CutSegment { Start = 170.353, End = 178.586, Action = SegmentAction.Remove },
    ]);

    [Fact]
    public void PlayerPositionJustBelowCutEndIsNotTreatedAsRemovedOrJumpedToEnd()
    {
        var keep = AlmaKeep();
        var back = TimeSpan.FromSeconds(79.689).TotalSeconds; // 79.6889999（プレーヤーが返す値）
        Assert.True(back < 79.689);
        Assert.False(EditTimelineCalculator.IsRemoved(back, keep));
        Assert.Null(EditTimelineCalculator.NextKeepStart(back, keep));
        Assert.Equal(EditTimelineCalculator.SkipKind.None, EditTimelineCalculator.ResolveSkip(back, keep, out _));
    }

    [Fact]
    public void ResolveSkipJumpsOverCutAndStopsOnlyAfterLastKeep()
    {
        var keep = AlmaKeep();
        Assert.Equal(EditTimelineCalculator.SkipKind.None, EditTimelineCalculator.ResolveSkip(58.8, keep, out _));
        Assert.Equal(EditTimelineCalculator.SkipKind.Jump, EditTimelineCalculator.ResolveSkip(59.2, keep, out var t));
        Assert.Equal(79.689, t);
        Assert.Equal(EditTimelineCalculator.SkipKind.Jump, EditTimelineCalculator.ResolveSkip(79.0, keep, out t));
        Assert.Equal(79.689, t);
        Assert.Equal(EditTimelineCalculator.SkipKind.None, EditTimelineCalculator.ResolveSkip(79.689, keep, out _));
        Assert.Equal(EditTimelineCalculator.SkipKind.None, EditTimelineCalculator.ResolveSkip(300, keep, out _));

        // 最後の区間を削除した場合だけ、削除の始まりで止める
        var tail = EditTimelineCalculator.ComputeKeepRanges(100, [new CutSegment { Start = 80, End = 100, Action = SegmentAction.Remove }]);
        Assert.Equal(EditTimelineCalculator.SkipKind.Stop, EditTimelineCalculator.ResolveSkip(80.5, tail, out t));
        Assert.Equal(80, t);
    }

    [Fact]
    public void ResolveSkipNeverStopsInAGapBeforeLaterKeep()
    {
        // 「削除中だが次に残る位置が無い」とされる隙間があっても、最後へは飛ばさない（保険）
        var keep = new List<TimeRange> { new(0, 10), new(20, 30) };
        for (var t = 0.0; t < 30; t += 0.0005)
        {
            var kind = EditTimelineCalculator.ResolveSkip(t, keep, out _);
            Assert.NotEqual(EditTimelineCalculator.SkipKind.Stop, kind);
        }
    }
}

public class AssSubtitleBuilderTests
{
    // v1.3 までの見た目（縁取り）で ASS の書式を確かめる。帯のプリセットは下の Preset* で確かめる。
    private static readonly SubtitleStyle Style = new() { Preset = SubtitlePreset.Outline };

    [Fact]
    public void HeaderUsesVideoSizeMeiryoWhiteTextBlackOutline()
    {
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a" }], Style, 1920, 1080);
        Assert.Contains("PlayResX: 1920\n", ass);
        Assert.Contains("PlayResY: 1080\n", ass);
        Assert.Contains("WrapStyle: 1\n", ass);
        Assert.Contains("Style: Default,Meiryo,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,3.8,0,2,", ass);
    }

    [Fact]
    public void DefaultPresetIsTranslucentBand()
    {
        // v1.9 から既定は白文字＋半透明の黒帯（v1.5～v1.8 は白文字＋黒背景）。
        Assert.Equal(SubtitlePreset.TranslucentBand, new SubtitleStyle().Preset);
        var translucent = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a" }], new SubtitleStyle(), 1920, 1080);
        Assert.Contains("Style: Default,Meiryo,48,&H00FFFFFF,&H000000FF,&HFF000000,&H60000000,0,0,0,0,100,100,0,0,4,9.6,0,2,96,96,54,1", translucent);

        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a" }], new SubtitleStyle { Preset = SubtitlePreset.BlackBox }, 1920, 1080);
        // BorderStyle 4（字幕全体を 1 枚の帯で囲む。v1.5）・白文字・縁取りは透明・帯は不透明の黒（BackColour）・余白 = 48 × 0.2
        Assert.Contains("Style: Default,Meiryo,48,&H00FFFFFF,&H000000FF,&HFF000000,&H00000000,0,0,0,0,100,100,0,0,4,9.6,0,2,96,96,54,1", ass);
        Assert.Contains(",Default,,0,0,54,,{\\an2\\fs48\\bord9.6}a", ass);
    }

    [Theory]
    [InlineData(SubtitlePreset.Outline, "&H00FFFFFF", "&H00000000", "&H00000000", 1)]
    [InlineData(SubtitlePreset.TranslucentBand, "&H00FFFFFF", "&HFF000000", "&H60000000", 4)]
    [InlineData(SubtitlePreset.BlackBox, "&H00FFFFFF", "&HFF000000", "&H00000000", 4)]
    [InlineData(SubtitlePreset.WhiteBox, "&H00000000", "&HFF000000", "&H00FFFFFF", 4)]
    public void PresetColorsAndBorderStyle(SubtitlePreset preset, string text, string outline, string back, int borderStyle)
    {
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a" }], new SubtitleStyle { Preset = preset }, 1920, 1080);
        Assert.Contains($"Style: Default,Meiryo,48,{text},&H000000FF,{outline},{back},0,0,0,0,100,100,0,0,{borderStyle},", ass);
    }

    [Theory]
    [InlineData("BIZ UDPGothic", "BIZ UDPGothic")]
    [InlineData("  Yu Gothic Medium ", "Yu Gothic Medium")]
    [InlineData("Bad,Font", "Meiryo")]
    [InlineData("", "Meiryo")]
    [InlineData(null, "Meiryo")]
    public void FontFamilyIsNormalized(string? family, string expected)
    {
        Assert.Equal(expected, SubtitleStyle.NormalizeFontFamily(family));
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a" }], new SubtitleStyle { FontFamily = family! }, 1920, 1080);
        Assert.Contains($"Style: Default,{expected},48,", ass);
    }

    [Fact]
    public void AssColorToPreviewArgb()
    {
        Assert.Equal(((byte)255, (byte)0xFF, (byte)0xFF, (byte)0xFF), SubtitleColors.ToArgb(0x00FFFFFF));
        Assert.Equal(((byte)0x9F, (byte)0, (byte)0, (byte)0), SubtitleColors.ToArgb(0x60000000));
        Assert.Equal(((byte)255, (byte)0x33, (byte)0x22, (byte)0x11), SubtitleColors.ToArgb(0x00112233));
    }

    [Theory]
    [InlineData(SubtitlePosition.Top, 8)]
    [InlineData(SubtitlePosition.Middle, 5)]
    [InlineData(SubtitlePosition.Bottom, 2)]
    public void PositionBecomesAlignment(SubtitlePosition position, int an)
    {
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a", Position = position }], Style, 1920, 1080);
        Assert.Contains($"{{\\an{an}\\fs48\\bord3.8}}a", ass);
    }

    [Fact]
    public void FontSizeScalesWithVideoHeight()
    {
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a", FontSize = 48 }], Style, 1280, 720);
        Assert.Contains("\\fs32\\", ass);
        Assert.Contains("PlayResY: 720", ass);
    }

    [Fact]
    public void DialogueTimesAndEscaping()
    {
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 3661.234, End = 3662.5, Text = "設定{A}\n C:\\new" }], Style, 1920, 1080);
        // v1.5: MarginV は字幕ごとに書く（上下の余白 54 + ずらし 0）。行頭の空白は折り返しで取り除く。
        Assert.Contains("Dialogue: 0,1:01:01.23,1:01:02.50,Default,,0,0,54,,", ass);
        Assert.Contains("設定｛A｝\\NC:\\⁠new", ass);
        Assert.DoesNotContain("{A}", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentsAreOrderedByStart()
    {
        var ass = AssSubtitleBuilder.Build(
            [new SubtitleSegment { Start = 5, End = 6, Text = "後" }, new SubtitleSegment { Start = 1, End = 2, Text = "先" }], Style, 1920, 1080);
        Assert.True(ass.IndexOf("先", StringComparison.Ordinal) < ass.IndexOf("後", StringComparison.Ordinal));
    }

    [Fact]
    public void LayoutMatchesAssValues()
    {
        var l = AssSubtitleBuilder.Layout(new SubtitleSegment { FontSize = 64, Position = SubtitlePosition.Top }, 1920, 1080);
        Assert.Equal(64, l.FontSizePx);
        Assert.Equal(54, l.MarginVerticalPx, 3);
        Assert.Equal(96, l.MarginHorizontalPx, 3);
        Assert.Equal(8, l.AssAlignment);
    }
}

public class SubtitleProjectPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-subtest-" + Guid.NewGuid().ToString("N"));

    public SubtitleProjectPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void SaveAndLoadKeepsSubtitlesPhaseStyleGifAndRecording()
    {
        var project = TestProjects.Create();
        var ed = new ProjectEditor(project);
        ed.AddCut(5, 10, SegmentAction.Remove, 0);
        ed.ConfirmCuts();
        project.ExportSettings.CutTransition = false;
        var (_, added) = ed.AddSubtitle(1, 3, "1行目\n2行目", SubtitlePosition.Middle, 40);
        Assert.True(ed.SetSubtitleLayout(added!.Id, SubtitlePosition.Middle, 40, SubtitleStyle.OffsetStep * 2).Success);
        project.ExportGifSettings.Quality = GifQuality.High;
        project.RecordingSettings = new RecordingInfo { Mode = RecordingMode.Window, Fps = 15, Microphone = true, MicrophoneDevice = "マイク", Target = "ウィンドウ: メモ帳", Width = 800, Height = 600, SegmentCount = 2 };
        var path = Path.Combine(_dir, "s.mvsproj.json");
        new ProjectService().Save(project, path);

        var text = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 8", text);
        Assert.Contains("\"verticalOffset\": 54", text);
        Assert.DoesNotContain("subtitleStyle", text); // v1.5 から見た目はアプリ設定
        Assert.Contains("\"editPhase\": \"subtitle\"", text);
        Assert.Contains("\"subtitleSegments\": [", text);
        Assert.Contains("\"position\": \"middle\"", text);
        Assert.Contains("\"exportGifSettings\": {", text);
        Assert.Contains("\"quality\": \"high\"", text);
        Assert.Contains("\"recordingSettings\": {", text);
        Assert.DoesNotContain("cutLockedBySubtitles", text);

        var loaded = new ProjectService().Load(path).Project;
        Assert.Equal(EditPhase.Subtitle, loaded.EditPhase);
        Assert.True(loaded.CutLockedBySubtitles);
        var s = Assert.Single(loaded.SubtitleSegments);
        Assert.Equal((1.0, 3.0, "1行目\n2行目", SubtitlePosition.Middle, 40.0, 54.0), (s.Start, s.End, s.Text, s.Position, s.FontSize, s.VerticalOffset));
        Assert.Contains("\"cutTransition\": false", text);
        Assert.DoesNotContain("transitionSecondsForExport", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(loaded.ExportSettings.CutTransition);
        Assert.Equal(GifQuality.High, loaded.ExportGifSettings.Quality);
        Assert.Equal((RecordingMode.Window, 15, true, 2), (loaded.RecordingSettings!.Mode, loaded.RecordingSettings.Fps, loaded.RecordingSettings.Microphone, loaded.RecordingSettings.SegmentCount));
    }

    [Fact]
    public void SchemaVersion2FileLoadsWithDefaults()
    {
        var path = Path.Combine(_dir, "v2.mvsproj.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 2,
              "sourceVideo": { "path": "C:\\none\\a.mp4" },
              "cutSegments": [ { "id": "c1", "start": 1, "end": 2, "action": "remove" } ],
              "capturePoints": []
            }
            """);
        var p = new ProjectService().Load(path).Project;
        Assert.Equal(EditPhase.Cut, p.EditPhase);
        Assert.Empty(p.SubtitleSegments);
        Assert.True(p.ExportSettings.CutTransition);
        Assert.Equal(GifQuality.Standard, p.ExportGifSettings.Quality);
        Assert.Null(p.RecordingSettings);
        Assert.False(new ProjectEditor(p).IsCutLocked);
    }

    [Fact]
    public void SubtitlesForceSubtitlePhaseAndBrokenOnesAreDropped()
    {
        var path = Path.Combine(_dir, "hand.mvsproj.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 3,
              "sourceVideo": { "path": "C:\\none\\a.mp4" },
              "editPhase": "cut",
              "subtitleSegments": [
                { "id": "s2", "start": 5, "end": 6, "text": "b", "fontSize": 50 },
                null,
                { "id": "bad", "start": 3, "end": 2, "text": "逆" },
                { "id": "empty", "start": 7, "end": 8, "text": " " },
                { "id": "s1", "start": 1, "end": 2, "text": "a" }
              ],
              "subtitleStyle": null
            }
            """);
        var p = new ProjectService().Load(path).Project;
        Assert.Equal(EditPhase.Subtitle, p.EditPhase);
        Assert.Equal(["s1", "s2"], p.SubtitleSegments.Select(s => s.Id));
        Assert.Equal(48, p.SubtitleSegments[1].FontSize);
        Assert.All(p.SubtitleSegments, x => Assert.Equal(0, x.VerticalOffset));
    }

    [Fact]
    public void OldSubtitleStyleIsIgnoredAndOffsetIsSnapped()
    {
        // v1.4 までの subtitleStyle（プロジェクトごとの見た目）は読み飛ばす。上下のずらしは段の倍数・上限内に直す。
        var v4 = Path.Combine(_dir, "v4.mvsproj.json");
        File.WriteAllText(v4, """
            { "schemaVersion": 4, "sourceVideo": { "path": "C:\\none\\a.mp4" }, "subtitleStyle": { "fontFamily": "Unknown Font", "preset": "translucentBand" },
              "subtitleSegments": [ { "id": "a", "start": 1, "end": 2, "text": "a", "verticalOffset": 40 }, { "id": "b", "start": 3, "end": 4, "text": "b", "verticalOffset": -9999 } ] }
            """);
        var p = new ProjectService().Load(v4).Project;
        Assert.Equal([27.0, -SubtitleStyle.MaxOffset], p.SubtitleSegments.Select(x => x.VerticalOffset));
    }

    [Fact]
    public void LegacyImagesFolderIsKeptForOldProjects()
    {
        var video = Path.Combine(_dir, "manual.mp4");
        File.WriteAllBytes(video, [0]);
        Directory.CreateDirectory(Path.Combine(_dir, "manual_images"));
        var json = "{ \"schemaVersion\": 3, \"sourceVideo\": { \"path\": \"" + video.Replace("\\", "\\\\") + "\" } }";
        var old = Path.Combine(_dir, "old.mvsproj.json");
        File.WriteAllText(old, json);
        Assert.Equal(Path.Combine(_dir, "manual_images"), new ProjectService().Load(old).Project.ExportSettings.ImageOutputDirectory);

        // v4 は空欄のまま（images を使う）。
        var current = Path.Combine(_dir, "new.mvsproj.json");
        File.WriteAllText(current, json.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 4"));
        Assert.Equal("", new ProjectService().Load(current).Project.ExportSettings.ImageOutputDirectory);
    }
}
