using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

public class FFmpegLocatorTests
{
    private sealed class FakeEnv(string path, params string[] files) : IToolEnvironment
    {
        private readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string p) => _files.Contains(p);
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? path : null;
    }

    [Fact]
    public void UserSettingTakesPriorityAndFfprobeFromSameDirectory()
    {
        var env = new FakeEnv(@"C:\bin", @"D:\ff\ffmpeg.exe", @"D:\ff\ffprobe.exe", @"C:\bin\ffmpeg.exe", @"C:\bin\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\ff\ffmpeg.exe");
        Assert.Equal(@"D:\ff\ffmpeg.exe", loc.Ffmpeg.Path);
        Assert.Equal(ToolSource.UserSetting, loc.Ffmpeg.Source);
        Assert.Equal(@"D:\ff\ffprobe.exe", loc.Ffprobe.Path);
        Assert.Equal(ToolSource.SameDirectoryAsFfmpeg, loc.Ffprobe.Source);
        Assert.True(loc.IsAvailable);
    }

    [Fact]
    public void FfprobeFallsBackToPath()
    {
        var env = new FakeEnv(@"C:\other;C:\bin", @"D:\ff\ffmpeg.exe", @"C:\bin\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\ff\ffmpeg.exe");
        Assert.Equal(@"C:\bin\ffprobe.exe", loc.Ffprobe.Path);
        Assert.Equal(ToolSource.PathEnvironment, loc.Ffprobe.Source);
    }

    [Fact]
    public void PathSearchedInOrderWhenNoSetting()
    {
        var env = new FakeEnv(@"C:\a;C:\b", @"C:\a\ffmpeg.exe", @"C:\b\ffmpeg.exe", @"C:\b\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate("");
        Assert.Equal(@"C:\a\ffmpeg.exe", loc.Ffmpeg.Path);
        Assert.Equal(ToolSource.PathEnvironment, loc.Ffmpeg.Source);
        // ffmpeg と同じフォルダ（C:\a）に無いので PATH から。
        Assert.Equal(@"C:\b\ffprobe.exe", loc.Ffprobe.Path);
    }

    [Fact]
    public void InvalidUserSettingFallsBackWithWarning()
    {
        var env = new FakeEnv(@"C:\bin", @"C:\bin\ffmpeg.exe", @"C:\bin\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\missing\ffmpeg.exe");
        Assert.Equal(@"C:\bin\ffmpeg.exe", loc.Ffmpeg.Path);
        Assert.Contains("見つかりません", loc.Ffmpeg.Message);
    }

    [Fact]
    public void WrongFileNameIsNotAccepted()
    {
        var env = new FakeEnv("", @"D:\ff\notffmpeg.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\ff\notffmpeg.exe");
        Assert.False(loc.Ffmpeg.Found);
    }

    [Fact]
    public void NotFound()
    {
        var loc = new FFmpegLocator(new FakeEnv(@"C:\bin")).Locate(null);
        Assert.Equal(ToolSource.NotFound, loc.Ffmpeg.Source);
        Assert.Equal(ToolSource.NotFound, loc.Ffprobe.Source);
        Assert.False(loc.IsAvailable);
    }

    [Fact]
    public void RelativeAndQuotedPathEntries()
    {
        var env = new FakeEnv(@"bin;"".\x"";""C:\Program Files\ff"";;", @"bin\ffmpeg.exe", @"C:\Program Files\ff\ffmpeg.exe");
        Assert.Equal(@"C:\Program Files\ff\ffmpeg.exe", new FFmpegLocator(env).FindInPath("ffmpeg.exe"));
    }

    [Fact]
    public void ExplicitFfprobeOverride()
    {
        var env = new FakeEnv("", @"D:\ff\ffmpeg.exe", @"E:\probe\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\ff\ffmpeg.exe", @"E:\probe\ffprobe.exe");
        Assert.Equal(ToolSource.UserSetting, loc.Ffprobe.Source);
    }
}

public class FFmpegInfoParsingTests
{
    [Fact]
    public void ParseVersion()
    {
        var v = FFmpegInfoService.ParseVersion("ffmpeg version 9.0.1-full_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers");
        Assert.Equal("9.0.1-full_build-www.gyan.dev", v);
        Assert.Equal(9, FFmpegInfoService.ParseMajor(v));
        Assert.Equal(6, FFmpegInfoService.ParseMajor("n6.1.1"));
        Assert.Equal(0, FFmpegInfoService.ParseMajor("N-113245-gabc"));
    }

    [Fact]
    public void ParseFilterAndEncoderLists()
    {
        const string filters = """
            Filters:
              T.. = Timeline support
              .S. = Slice threading
              ------
             ... freezedetect      V->V       Detects frozen video input.
             TS. scale             V->V       Scale the input video size.
             .. silencedetect      A->A       Detect silence.
            """;
        var set = FFmpegInfoService.ParseNameList(filters);
        Assert.Contains("freezedetect", set);
        Assert.Contains("scale", set);
        Assert.Contains("silencedetect", set);
        Assert.DoesNotContain("=", set);

        const string encoders = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
             V....D png                  PNG (Portable Network Graphics) image
            """;
        var enc = FFmpegInfoService.ParseNameList(encoders);
        Assert.Equal(["aac", "libx264", "png"], enc.Order());
    }
}

public class FfprobeParserTests
{
    private const string Json = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1921, "height": 1080, "pix_fmt": "yuv420p",
              "r_frame_rate": "60/1", "avg_frame_rate": "30000/1001", "bit_rate": "4500000" },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "bit_rate": "128000" },
            { "index": 2, "codec_name": "mjpeg", "codec_type": "video", "disposition": { "attached_pic": 1 } }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "123.456000", "size": "98765432", "bit_rate": "4700000" }
        }
        """;

    [Fact]
    public void ParsesMainFields()
    {
        var m = FfprobeJsonParser.Parse(Json);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", m.Container);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(1921, m.Width);
        Assert.Equal("1921x1080", m.Resolution);
        Assert.Equal(29.97, m.Fps, 2);
        Assert.Equal(123.456, m.Duration, 3);
        Assert.Equal("yuv420p", m.PixelFormat);
        Assert.Equal(4_500_000, m.VideoBitrate);
        Assert.Equal(128_000, m.AudioBitrate);
        Assert.Equal(98_765_432, m.FileSize);
        Assert.True(m.HasAudio);
    }

    [Fact]
    public void MkvBpsTagAndNoAudio()
    {
        const string mkv = """
            { "streams": [ { "codec_name": "vp9", "codec_type": "video", "width": 1280, "height": 720,
                             "avg_frame_rate": "0/0", "r_frame_rate": "25/1", "tags": { "BPS": "2000000" } } ],
              "format": { "format_name": "matroska,webm", "duration": "10.0" } }
            """;
        var m = FfprobeJsonParser.Parse(mkv);
        Assert.Equal(25, m.Fps);
        Assert.Equal(2_000_000, m.VideoBitrate);
        Assert.False(m.HasAudio);
        Assert.Equal("", m.AudioCodec);
    }

    [Fact]
    public void NoVideoStreamThrows()
        => Assert.Throws<InvalidOperationException>(() => FfprobeJsonParser.Parse("""{ "streams": [ { "codec_type": "audio" } ], "format": {} }"""));

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("0/0", 0)]
    [InlineData("25", 25)]
    [InlineData("", 0)]
    public void ParseRate(string rate, double expected) => Assert.Equal(expected, FfprobeJsonParser.ParseRate(rate), 2);
}

public class FFmpegArgumentsTests
{
    private const string Input = @"C:\動画 フォルダ\操作 & 手順 (1).mp4";
    private static VideoMetadata Meta(bool audio = true, int w = 1920, int h = 1080)
        => new() { Duration = 60, Width = w, Height = h, HasAudio = audio, HasVideo = true };

    private static string After(IReadOnlyList<string> args, string key) => args[args.ToList().IndexOf(key) + 1];

    [Fact]
    public void FrameExtractionPng()
    {
        var a = FFmpegArguments.FrameExtraction(Input, 12.4, @"D:\out\001_設定.png", ImageFormat.Png);
        Assert.Contains(Input, a); // 空白・日本語・記号を含むパスが 1 引数のまま
        Assert.Equal("12.4", After(a, "-ss"));
        Assert.True(a.ToList().IndexOf("-ss") < a.ToList().IndexOf("-i"));
        Assert.Equal("1", After(a, "-frames:v"));
        Assert.Equal("png", After(a, "-c:v"));
        Assert.Equal(@"D:\out\001_設定.png", a[^1]);
        Assert.Equal("image2", After(a, "-f")); // 拡張子なしのキャッシュにも書けるよう形式を明示（v1.4.2）
        Assert.DoesNotContain("-vf", a);
    }

    [Fact]
    public void FrameExtractionJpegWithScale()
    {
        var a = FFmpegArguments.FrameExtraction(Input, 1, "t.jpg", ImageFormat.Jpeg, jpegQuality: 50, maxWidth: 320);
        Assert.Equal("mjpeg", After(a, "-c:v"));
        Assert.Equal("31", After(a, "-q:v")); // 範囲に丸める
        Assert.Contains("scale='min(320,iw)':-2", After(a, "-vf"));
    }

    [Fact]
    public void ExportWithCuts()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 10, End = 20 }]);
        var cmd = FFmpegArguments.Export(Input, keep, Meta(), @"D:\out\edited.mp4", new VideoEncodeOptions(), 9);
        var a = cmd.Arguments;
        Assert.Null(cmd.FilterScript);
        var filter = After(a, "-filter_complex");
        Assert.Contains("[0:v:0]trim=start=0:end=10,setpts=PTS-STARTPTS[v0]", filter);
        Assert.Contains("[0:a:0]atrim=start=20:end=60,asetpts=PTS-STARTPTS[a1]", filter);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[outv][outa]", filter);
        Assert.Equal("[outv]", After(a, "-map"));
        Assert.Contains("[outa]", a);
        Assert.Equal("libx264", After(a, "-c:v"));
        Assert.Equal("aac", After(a, "-c:a"));
        Assert.Equal("me=umh:merange=64", After(a, "-x264-params"));
        Assert.Equal("+faststart", After(a, "-movflags"));
        Assert.Equal("pipe:1", After(a, "-progress"));
        Assert.Equal(Input, After(a, "-i"));
        Assert.Equal(@"D:\out\edited.mp4", a[^1]);
    }

    [Fact]
    public void ExportWithoutAudio()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 10, End = 20 }]);
        var a = FFmpegArguments.Export(Input, keep, Meta(audio: false), "o.mp4", new VideoEncodeOptions(), 9).Arguments;
        var filter = After(a, "-filter_complex");
        Assert.DoesNotContain("atrim", filter);
        Assert.Contains("concat=n=2:v=1:a=0[outv]", filter);
        Assert.Contains("-an", a);
        Assert.DoesNotContain("[outa]", a);
    }

    [Fact]
    public void ExportIdentityDoesNotUseFilterComplex()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, []);
        var a = FFmpegArguments.Export(Input, keep, Meta(), "o.mkv", new VideoEncodeOptions(), 9).Arguments;
        Assert.DoesNotContain("-filter_complex", a);
        Assert.Equal("0:v:0", After(a, "-map"));
        Assert.DoesNotContain("-movflags", a);
    }

    [Fact]
    public void ExportOddSizeIsCroppedToEven()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 10, End = 20 }]);
        var filter = After(FFmpegArguments.Export(Input, keep, Meta(w: 1921, h: 1079), "o.mp4", new VideoEncodeOptions(), 9).Arguments, "-filter_complex");
        Assert.EndsWith("[vcat];[vcat]crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0[outv]", filter.Replace("[outa]", ""));
    }

    [Fact]
    public void LongFilterUsesScriptFile()
    {
        var cuts = Enumerable.Range(0, 400).Select(i => new CutSegment { Start = i * 1.0 + 0.2, End = i * 1.0 + 0.6 }).ToList();
        var keep = EditTimelineCalculator.ComputeKeepRanges(401, cuts);
        var cmd = FFmpegArguments.Export(Input, keep, Meta() with { Duration = 401 }, "o.mp4", new VideoEncodeOptions(), 9);
        Assert.NotNull(cmd.FilterScript);
        Assert.Contains("-/filter_complex", cmd.Arguments);
        var replaced = cmd.WithScriptPath(@"C:\tmp\f.txt");
        Assert.Equal(@"C:\tmp\f.txt", After(replaced, "-/filter_complex"));
        var old = FFmpegArguments.Export(Input, keep, Meta() with { Duration = 401 }, "o.mp4", new VideoEncodeOptions(), 6);
        Assert.Contains("-filter_complex_script", old.Arguments);
    }

    [Fact]
    public void AnalysisArguments()
    {
        var s = AnalysisSettings.FromPreset(AnalysisPreset.Standard);
        var a = FFmpegArguments.Analysis(Input, s, hasAudio: true);
        var vf = After(a, "-vf");
        Assert.Contains("freezedetect=n=-60dB:d=3", vf);
        Assert.Contains("select='gt(scene,0.3)',showinfo", vf);
        Assert.True(vf.IndexOf("freezedetect", StringComparison.Ordinal) < vf.IndexOf("select", StringComparison.Ordinal));
        Assert.Equal("silencedetect=noise=-45dB:d=3", After(a, "-af"));
        Assert.Equal("null", After(a, "-f"));
    }

    [Fact]
    public void AnalysisWithoutAudioSkipsSilence()
    {
        var a = FFmpegArguments.Analysis(Input, new AnalysisSettings(), hasAudio: false);
        Assert.DoesNotContain("-af", a);
        Assert.Contains("-an", a);
    }

    [Fact]
    public void AnalysisNothingEnabledThrows()
        => Assert.Throws<InvalidOperationException>(() => FFmpegArguments.Analysis(Input,
            new AnalysisSettings { EnableFreeze = false, EnableSceneChange = false, EnableSilence = false }, true));

    [Fact]
    public void ProbeArgumentsKeepPathAsSingleArgument()
        => Assert.Equal(Input, MediaMetadataService.BuildArguments(Input)[^1]);
}

public class FFmpegProgressParserTests
{
    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_ms=2000000", 2.0)]
    public void ParsesTime(string line, double expected) => Assert.Equal(expected, FFmpegProgressParser.ParseLine(line));

    [Fact]
    public void EndAndNoise()
    {
        Assert.True(double.IsPositiveInfinity(FFmpegProgressParser.ParseLine("progress=end")!.Value));
        Assert.Null(FFmpegProgressParser.ParseLine("progress=continue"));
        Assert.Null(FFmpegProgressParser.ParseLine("out_time_us=N/A"));
        Assert.Null(FFmpegProgressParser.ParseLine("frame=10"));
        Assert.Null(FFmpegProgressParser.ParseLine(""));
        Assert.Equal(0.5, FFmpegProgressParser.Ratio(5, 10));
        Assert.Equal(1, FFmpegProgressParser.Ratio(15, 10));
        Assert.Equal(1, FFmpegProgressParser.Ratio(double.PositiveInfinity, 10));
    }
}

public class DetectionTests
{
    [Fact]
    public void ParseFreezes()
    {
        string[] log =
        [
            "[freezedetect @ 000001] lavfi.freezedetect.freeze_start: 2.002",
            "[freezedetect @ 000001] lavfi.freezedetect.freeze_duration: 3.003",
            "[freezedetect @ 000001] lavfi.freezedetect.freeze_end: 5.005",
            "[Parsed_freezedetect_1 @ 000002] lavfi.freezedetect.freeze_start: 50",
        ];
        var r = DetectionLogParser.ParseFreezes(log, 60);
        Assert.Equal([new TimeRange(2.002, 5.005), new TimeRange(50, 60)], r);
    }

    [Fact]
    public void ParseSilences()
    {
        string[] log =
        [
            "[silencedetect @ 0000] silence_start: -0.0213",
            "[silencedetect @ 0000] silence_end: 4.5 | silence_duration: 4.52",
            "size=N/A time=00:00:10.00 bitrate=N/A",
            "[silencedetect @ 0000] silence_start: 20.25",
            "[silencedetect @ 0000] silence_end: 23.75 | silence_duration: 3.5",
        ];
        Assert.Equal([new TimeRange(0, 4.5), new TimeRange(20.25, 23.75)], DetectionLogParser.ParseSilences(log, 60));
    }

    [Fact]
    public void ParseScenes()
    {
        string[] log =
        [
            "[Parsed_showinfo_3 @ 0000] config in time_base: 1/15360, frame_rate: 30/1",
            "[Parsed_showinfo_3 @ 0000] n:   0 pts:  61440 pts_time:4       duration:    512 duration_time:0.0333333",
            "[Parsed_showinfo_3 @ 0000] n:   1 pts: 190464 pts_time:12.4    duration:    512",
        ];
        Assert.Equal([4.0, 12.4], DetectionLogParser.ParseSceneChanges(log));
    }

    [Fact]
    public void CandidatesPrioritizeFreezeAndSilence()
    {
        var s = AnalysisSettings.FromPreset(AnalysisPreset.Standard);
        var freezes = new[] { new TimeRange(10, 17.6), new TimeRange(30, 40) };
        var silences = new[] { new TimeRange(9, 18) }; // 1 つ目の静止だけ無音
        var c = CandidateBuilder.Build(freezes, silences, 60, hasAudio: true, s);
        Assert.Equal(2, c.Count);
        Assert.Equal(CandidateKind.FreezeAndSilence, c[0].Kind);
        Assert.Equal("7.6秒の静止＋無音", c[0].Reason);
        // 余白 0.5 秒のあと、先頭に残す秒数（標準 1.5 秒）を残して、以降を削除する候補にする（短縮は廃止。v1.17.0）。
        Assert.Equal(12.0, c[0].Start);
        Assert.Equal(17.1, c[0].End);
        Assert.Equal(SegmentAction.Remove, c[0].SuggestedAction);
        Assert.Equal(0, c[0].KeepSeconds);
        Assert.Equal(CandidateKind.Freeze, c[1].Kind);
        Assert.True(c[0].Confidence > c[1].Confidence);
        Assert.All(c, x => Assert.Equal(SegmentSource.LocalAnalysis, x.Source));
    }

    [Fact]
    public void ShortCandidatesAreSkippedAndEdgesNotMargined()
    {
        var s = AnalysisSettings.FromPreset(AnalysisPreset.Standard);
        var c = CandidateBuilder.Build([new TimeRange(0, 5), new TimeRange(20, 22.5), new TimeRange(55, 60)], [], 60, false, s);
        Assert.Equal(2, c.Count);
        Assert.Equal(1.5, c[0].Start);  // 先頭は余白なし。残す秒数（1.5 秒）だけ後ろから削除する
        Assert.Equal(60, c[1].End);     // 末尾も余白なし
    }

    [Fact]
    public void SilenceOnlyWhenEnabled()
    {
        var silences = new[] { new TimeRange(10, 20) };
        Assert.Empty(CandidateBuilder.Build([], silences, 60, true, AnalysisSettings.FromPreset(AnalysisPreset.Standard)));
        var c = CandidateBuilder.Build([], silences, 60, true, AnalysisSettings.FromPreset(AnalysisPreset.Aggressive));
        Assert.Equal(CandidateKind.Silence, Assert.Single(c).Kind);
    }
}
