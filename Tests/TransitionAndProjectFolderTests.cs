using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Tests;

/// <summary>カットのつなぎ目のクロスフェード（v1.4）。</summary>
public class CutTransitionTests
{
    private static readonly VideoEncodeOptions Enc = new();
    private static VideoMetadata Meta(bool audio = true) => new() { Duration = 60, Width = 1920, Height = 1080, HasAudio = audio, HasVideo = true };
    private static IReadOnlyList<TimeRange> Keep(params CutSegment[] cuts) => EditTimelineCalculator.ComputeKeepRanges(60, cuts);

    [Fact]
    public void ExportSettingsDefaultIsOn()
    {
        Assert.True(new ExportSettings().CutTransition);
        Assert.Equal(0.3, new ExportSettings().TransitionSecondsForExport);
        Assert.Equal(0, new ExportSettings { CutTransition = false }.TransitionSecondsForExport);
    }

    [Fact]
    public void WithoutTransitionArgumentsAreUnchanged()
    {
        var keep = Keep(new CutSegment { Start = 5, End = 10 });
        var a = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9);
        var b = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, null, transitionSeconds: 0);
        Assert.Equal(a.Arguments, b.Arguments);
        Assert.DoesNotContain("xfade", string.Join(' ', a.Arguments));
    }

    [Fact]
    public void CrossfadeBorrowsFromTheRemovedPartAndKeepsDuration()
    {
        var keep = Keep(new CutSegment { Start = 5, End = 10 });
        var filter = FFmpegArguments.BuildJoinFilter(keep, hasAudio: true, videoPostFilter: null, transitionSeconds: 0.3);
        // 前の区間を 0.15 秒延ばし、次の区間を 0.15 秒早く始め、0.3 秒重ねる（5.15 + 50.15 - 0.3 = 55 秒 = カット後の長さ）。
        Assert.Contains("[0:v:0]trim=start=0:end=5.15,setpts=PTS-STARTPTS[v0];", filter);
        Assert.Contains("[0:v:0]trim=start=9.85:end=60,setpts=PTS-STARTPTS[v1];", filter);
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.3:offset=4.85[outv]", filter);
        Assert.Contains("[a0][a1]acrossfade=d=0.3[outa]", filter);
        Assert.DoesNotContain("concat", filter);
    }

    [Fact]
    public void ShortGapUsesHalfOfTheGapAndTinyGapUsesConcat()
    {
        // 削る部分 0.1 秒 → のりしろ 0.05 秒。削る部分 0.02 秒 → のりしろ 0.01 秒（最小未満）なので concat。
        var keep = Keep(new CutSegment { Start = 10, End = 10.1 }, new CutSegment { Start = 20, End = 20.02 });
        var halves = FFmpegArguments.TransitionHalves(keep, 0.3);
        Assert.Equal([0.05, 0], halves);
        var filter = FFmpegArguments.BuildJoinFilter(keep, hasAudio: false, videoPostFilter: null, transitionSeconds: 0.3);
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.1:offset=9.95[vx1];", filter);
        Assert.Contains("[vx1][v2]concat=n=2:v=1:a=0[outv]", filter);
        Assert.DoesNotContain("[0:a:0]", filter);
    }

    [Fact]
    public void ShortSegmentsLimitTheOverlap()
    {
        // 残す区間が 0.4 秒しかなければ、のりしろはその 1/4（0.1 秒）まで。
        var keep = Keep(new CutSegment { Start = 0, End = 10 }, new CutSegment { Start = 10.4, End = 20 });
        Assert.Equal([0.1], FFmpegArguments.TransitionHalves(keep, 0.3));
    }

    [Fact]
    public void ThreeSegmentsChainOffsetsOnOutputTimeline()
    {
        var keep = Keep(new CutSegment { Start = 5, End = 10 }, new CutSegment { Start = 20, End = 30 });
        var filter = FFmpegArguments.BuildJoinFilter(keep, hasAudio: true, videoPostFilter: "subtitles=filename=s.ass", transitionSeconds: 0.3);
        // 区間 0: 0～5.15（5.15 秒）、区間 1: 9.85～20.15（10.3 秒）、区間 2: 29.85～60。
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.3:offset=4.85[vx1];", filter);
        // 2 つ目の offset = (5.15 + 10.3 - 0.3) - 0.3 = 14.85（出力の 15 秒目を中心に重ねる）。
        Assert.Contains("[vx1][v2]xfade=transition=fade:duration=0.3:offset=14.85[vcat];", filter);
        Assert.Contains("[ax1][a2]acrossfade=d=0.3[outa];", filter);
        Assert.EndsWith("[vcat]subtitles=filename=s.ass[outv]", filter);
    }

    [Fact]
    public void ExportAndGifUseTransition()
    {
        var keep = Keep(new CutSegment { Start = 5, End = 10 });
        var mp4 = string.Join(' ', FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, null, 0.3).Arguments);
        Assert.Contains("xfade=transition=fade", mp4);
        var gif = FFmpegArguments.BuildGifFilter(keep, Meta(), null, GifPreset.Of(GifQuality.Standard), "null", 0.3);
        Assert.Contains("xfade=transition=fade", gif);
        Assert.DoesNotContain("acrossfade", gif);
    }

    [Fact]
    public void IdentityHasNoTransition()
    {
        var mp4 = string.Join(' ', FFmpegArguments.Export("in.mp4", Keep(), Meta(), "out.mp4", Enc, 9, null, 0.3).Arguments);
        Assert.DoesNotContain("xfade", mp4);
    }
}

/// <summary>プロジェクトフォルダー（v1.4）。</summary>
public class ProjectFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-folder-" + Guid.NewGuid().ToString("N"));

    public ProjectFolderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly DateTime Time = new(2026, 9, 23, 10, 15, 42);

    [Theory]
    [InlineData("ルーター設定", "20260923_1015_ルーター設定")]
    [InlineData("  a:b  ", "20260923_1015_a：b")]
    [InlineData("", "20260923_1015")]
    [InlineData(null, "20260923_1015")]
    [InlineData("\n2 行目だけ", "20260923_1015_2 行目だけ")]
    public void FolderNameIsDateAndName(string? name, string expected)
        => Assert.Equal(expected, ProjectFolders.FolderName(Time, name));

    [Fact]
    public void IsInProjectFolderMeansDirectChildOfRoot()
    {
        Assert.True(ProjectFolders.IsInProjectFolder(@"C:\root\20260923_1015\rec.mp4", @"C:\root"));
        Assert.True(ProjectFolders.IsInProjectFolder(@"C:\root\x\rec.mp4", @"c:\ROOT\"));
        Assert.False(ProjectFolders.IsInProjectFolder(@"C:\root\rec.mp4", @"C:\root"));
        Assert.False(ProjectFolders.IsInProjectFolder(@"C:\root\a\b\rec.mp4", @"C:\root"));
        Assert.False(ProjectFolders.IsInProjectFolder(@"D:\other\x\rec.mp4", @"C:\root"));
    }

    [Fact]
    public void MoveIntoNewFolderMovesVideoAndAvoidsExistingFolders()
    {
        var root = Path.Combine(_dir, "root");
        var source = Path.Combine(_dir, "画面 録画.mp4");
        File.WriteAllText(source, "video");
        Directory.CreateDirectory(Path.Combine(root, "20260923_1015_手順"));

        var moved = ProjectFolders.MoveIntoNewFolder(source, root, Time, "手順");
        Assert.Equal(Path.Combine(root, "20260923_1015_手順_2", "画面 録画.mp4"), moved);
        Assert.False(File.Exists(source));
        Assert.Equal("video", File.ReadAllText(moved));
        Assert.True(ProjectFolders.IsInProjectFolder(moved, root));
        Assert.Equal(Path.Combine(root, "20260923_1015_手順_2", "images"), ProjectFolders.ImagesDirectory(moved));
    }

    [Fact]
    public void FailedMoveLeavesNoEmptyFolder()
    {
        var root = Path.Combine(_dir, "root");
        Assert.ThrowsAny<IOException>(() => ProjectFolders.MoveIntoNewFolder(Path.Combine(_dir, "none.mp4"), root, Time, "x"));
        Assert.Empty(Directory.GetDirectories(root));
    }
}
