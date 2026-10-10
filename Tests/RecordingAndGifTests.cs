using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;

namespace ManualVideoStudio.Tests;

public class RecordingSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-rectest-" + Guid.NewGuid().ToString("N"));

    public RecordingSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Defaults()
    {
        var s = new RecordingSettings();
        Assert.Equal(RecordingMode.Region, s.Mode);
        Assert.Equal(30, s.Fps);
        Assert.True(s.DrawCursor);
        Assert.False(s.MicrophoneEnabled);
        Assert.True(s.Countdown);
        Assert.Equal("", s.MicrophoneDevice);
        Assert.Equal("", s.OutputDirectory);
    }

    [Fact]
    public void FpsPresetsAreOnly15And30()
    {
        Assert.Equal([15, 30], RecordingSettings.FpsPresets);
        Assert.Equal(30, RecordingSettings.DefaultFps);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(60, 30)]
    [InlineData(0, 30)]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    public void NormalizeKeepsOnly15Or30(int fps, int expected)
    {
        var s = new RecordingSettings { Fps = fps };
        s.NormalizeOnLoad();
        Assert.Equal(expected, s.Fps);
    }

    [Fact]
    public void NormalizeAlwaysTurnsMicrophoneOff()
    {
        var s = new RecordingSettings { MicrophoneEnabled = true, MicrophoneDevice = "マイク (USB)" };
        s.NormalizeOnLoad();
        Assert.False(s.MicrophoneEnabled);
        Assert.Equal("マイク (USB)", s.MicrophoneDevice); // 選んだマイクは覚えておく
    }

    [Fact]
    public void SettingsFileRoundTripButMicrophoneStartsOff()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        var r = svc.Current.Recording;
        r.Mode = RecordingMode.Monitor;
        r.Fps = 15;
        r.DrawCursor = false;
        r.MicrophoneEnabled = true;
        r.MicrophoneDevice = "マイク配列";
        r.Countdown = false;
        r.OutputDirectory = @"D:\録画";
        r.RegionX = 10; r.RegionY = 20; r.RegionWidth = 1280; r.RegionHeight = 720;
        svc.Save();
        Assert.Contains("\"microphoneEnabled\": true", File.ReadAllText(path)); // 保存はする

        var again = new SettingsService(path);
        again.Load();
        var l = again.Current.Recording;
        Assert.Equal((RecordingMode.Monitor, 15, false, "マイク配列", false, @"D:\録画"),
            (l.Mode, l.Fps, l.DrawCursor, l.MicrophoneDevice, l.Countdown, l.OutputDirectory));
        Assert.Equal((10, 20, 1280, 720), (l.RegionX, l.RegionY, l.RegionWidth, l.RegionHeight));
        Assert.False(l.MicrophoneEnabled); // 再起動後は OFF
    }

    [Fact]
    public void OldSettingsWithoutRecordingGetDefaults()
    {
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "ffmpegPath": "" }""");
        var svc = new SettingsService(path);
        svc.Load();
        Assert.Equal(30, svc.Current.Recording.Fps);
        Assert.False(svc.Current.Recording.MicrophoneEnabled);
    }

    [Fact]
    public void FileNameAndDefaultDirectory()
    {
        Assert.Equal("rec_20260921_230102.mp4", RecordingSettings.BuildFileName(new DateTime(2026, 9, 21, 23, 1, 2)));
        Assert.EndsWith("ManualVideoStudio", RecordingSettings.DefaultOutputDirectory());
        Assert.Equal(@"D:\x", new RecordingSettings { OutputDirectory = @"D:\x" }.ResolveOutputDirectory());
    }
}

public class CaptureRegionTests
{
    [Fact]
    public void EvenSize() => Assert.Equal(new CaptureRegion(1, 2, 1280, 720), new CaptureRegion(1, 2, 1281, 721).ToEvenSize());

    [Fact]
    public void ClampToScreen()
    {
        var screen = new CaptureRegion(0, 0, 1920, 1080);
        Assert.Equal(new CaptureRegion(640, 360, 1280, 720), new CaptureRegion(1000, 900, 1280, 720).ClampTo(screen));
        Assert.Equal(new CaptureRegion(0, 0, 1920, 1080), new CaptureRegion(-50, -50, 4000, 3000).ClampTo(screen));
    }

    [Fact]
    public void NegativeCoordinatesForLeftMonitor()
    {
        var screen = new CaptureRegion(-1920, 0, 3840, 1080);
        Assert.Equal(new CaptureRegion(-1920, 100, 800, 600), new CaptureRegion(-2000, 100, 800, 600).ClampTo(screen));
    }
}

public class DshowDeviceParserTests
{
    private const string Sample = """
        [in#0 @ 000001] "USB Camera" (video)
        [in#0 @ 000001]   Alternative name "@device_pnp_\\?\usb#vid_0000"
        [in#0 @ 000001] "マイク (Realtek(R) Audio)" (audio)
        [in#0 @ 000001]   Alternative name "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{AAA}"
        [in#0 @ 000001] "ヘッドセット マイク (USB Audio)" (audio)
        [in#0 @ 000001]   Alternative name "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{BBB}"
        [in#0 @ 000001] Could not enumerate video devices (or none found).
        Error opening input file dummy.
        """;

    [Fact]
    public void ParsesAudioDevicesWithAlternativeNames()
    {
        var list = DshowDeviceParser.Parse(Sample);
        Assert.Equal(3, list.Count);
        var audio = list.Where(d => d.IsAudio).ToList();
        Assert.Equal(["マイク (Realtek(R) Audio)", "ヘッドセット マイク (USB Audio)"], audio.Select(d => d.Name));
        Assert.EndsWith(@"\wave_{AAA}", audio[0].AlternativeName);
    }

    [Fact]
    public void NoDevices() => Assert.Empty(DshowDeviceParser.Parse("[in#0 @ 1] Could not enumerate audio only devices (or none found).\n"));

    [Fact]
    public void DirectShowSpec()
    {
        var a = AudioInputSpec.DirectShow("@device_cm_x", "マイク");
        Assert.Equal(("dshow", "audio=@device_cm_x", "マイク"), (a.Format, a.Input, a.DisplayName));
    }
}

public class RecordingArgumentsTests
{
    private static readonly CaptureRegion Region = new(100, 50, 1280, 720);

    [Fact]
    public void RegionCursorOn30FpsWithoutMicrophone()
    {
        var args = FFmpegArguments.ScreenRecordingSegment(new ScreenRecordingOptions(Region, 30, true, null), "seg001.mkv");
        var s = string.Join(' ', args);
        Assert.Contains("-f gdigrab -framerate 30 -draw_mouse 1 -offset_x 100 -offset_y 50 -video_size 1280x720 -i desktop", s);
        Assert.Contains("-c:v libx264 -preset ultrafast", s);
        Assert.Contains("-r 30 -fps_mode cfr", s);
        Assert.Contains("-an", args);
        Assert.DoesNotContain("dshow", s);
        Assert.DoesNotContain("-nostdin", args); // 停止は標準入力の q
        Assert.Equal("seg001.mkv", args[^1]);
        Assert.Equal("matroska", args[^2]);
    }

    [Fact]
    public void CursorOff15Fps()
    {
        var s = string.Join(' ', FFmpegArguments.ScreenRecordingSegment(new ScreenRecordingOptions(Region, 15, false, null), "a.mkv"));
        Assert.Contains("-framerate 15 -draw_mouse 0", s);
        Assert.Contains("-r 15 -fps_mode cfr -g 30", s);
    }

    [Fact]
    public void MicrophoneAddsDshowAudio()
    {
        var mic = AudioInputSpec.DirectShow("マイク (Realtek(R) Audio)", "マイク");
        var args = FFmpegArguments.ScreenRecordingSegment(new ScreenRecordingOptions(Region, 30, true, mic), "a.mkv");
        var s = string.Join(' ', args);
        Assert.Contains("-f dshow -i", s);
        Assert.Contains("audio=マイク (Realtek(R) Audio)", args); // 1 要素 1 引数（空白・日本語をそのまま）
        Assert.Contains("-map 0:v:0 -map 1:a:0", s);
        Assert.Contains("-c:a aac", s);
        Assert.DoesNotContain("-an", args);
    }

    [Fact]
    public void ConcatSingleAndMultiple()
    {
        var one = FFmpegArguments.ConcatRecordingSegments(["seg001.mkv"], "list.txt", @"C:\out\rec.mp4");
        Assert.Equal(["-i", "seg001.mkv"], one.Skip(5).Take(2));
        Assert.Contains("+faststart", one);
        Assert.Contains("copy", one);

        var many = string.Join(' ', FFmpegArguments.ConcatRecordingSegments(["seg001.mkv", "seg002.mkv"], "list.txt", "o.mp4"));
        Assert.Contains("-f concat -safe 0 -i list.txt -map 0 -c copy", many);
    }

    [Fact]
    public void ConcatListEscapesQuotes()
        => Assert.Equal("file 'a.mkv'\nfile 'b'\\''c.mkv'\n", FFmpegArguments.ConcatList(["a.mkv", "b'c.mkv"]));
}

public class SubtitleBurnInArgumentsTests
{
    private static readonly VideoEncodeOptions Enc = new();
    private static VideoMetadata Meta(int w = 1920, int h = 1080, bool audio = true) => new() { Duration = 60, Width = w, Height = h, HasAudio = audio, HasVideo = true };

    [Fact]
    public void WithoutSubtitlesArgumentsAreUnchanged()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 5, End = 10 }]);
        var a = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9);
        var b = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, subtitleFile: null);
        Assert.Equal(a.Arguments, b.Arguments);
        Assert.DoesNotContain("subtitles", string.Join(' ', a.Arguments));
    }

    [Fact]
    public void Mp4IdentityBurnsSubtitlesWithVf()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, []);
        var args = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass").Arguments;
        var i = args.ToList().IndexOf("-vf");
        Assert.Equal("subtitles=filename=subtitles.ass", args[i + 1]);
        Assert.Contains("-c:a", args); // 音声（マイク）は保持
    }

    [Fact]
    public void Mp4OddSizeCropsBeforeSubtitles()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, []);
        var args = FFmpegArguments.Export("in.mp4", keep, Meta(1281, 721), "out.mp4", Enc, 9, "subtitles.ass").Arguments;
        Assert.Contains(FFmpegArguments.EvenCrop + ",subtitles=filename=subtitles.ass", args);
    }

    [Fact]
    public void Mp4CutsBurnSubtitlesAfterConcat()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 5, End = 10 }]);
        var args = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass").Arguments;
        var filter = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.EndsWith("concat=n=2:v=1:a=1[vcat][outa];[vcat]subtitles=filename=subtitles.ass[outv]", filter);
    }

    [Theory]
    [InlineData("C:\\temp\\a.ass")]
    [InlineData("a b.ass")]
    [InlineData("字幕.ass")]
    [InlineData("a:b.ass")]
    [InlineData("")]
    public void SubtitleFileNameMustBeSafe(string name) => Assert.Throws<ArgumentException>(() => FFmpegArguments.SubtitlesFilter(name));
}

public class GifArgumentsTests
{
    private static VideoMetadata Meta(bool audio = true) => new() { Duration = 60, Width = 1920, Height = 1080, HasAudio = audio, HasVideo = true };

    [Fact]
    public void PresetsAreSeparateFromRecordingFps()
    {
        Assert.Equal([8, 12, 15], GifPreset.All.Select(p => p.Fps));
        Assert.Equal(["軽量", "標準", "高品質"], GifPreset.All.Select(p => p.Label));
        Assert.Equal(GifQuality.Standard, new GifExportSettings().Quality);
        Assert.True(GifPreset.Light.MaxWidth < GifPreset.Standard.MaxWidth && GifPreset.Standard.MaxWidth < GifPreset.High.MaxWidth);
    }

    [Fact]
    public void PalettePassWithoutCuts()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, []);
        var cmd = FFmpegArguments.GifPalette("in.mp4", keep, Meta(), null, GifPreset.Standard, "palette", 9);
        var filter = cmd.Arguments[cmd.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Equal("[0:v:0]fps=12,scale=w='min(iw,1280)':h='min(ih,960)':force_original_aspect_ratio=decrease:flags=lanczos,palettegen=max_colors=256:stats_mode=full[outv]", filter);
        Assert.Contains("-an", cmd.Arguments);
        // 拡張子なしの一時ファイルへ PNG として書く（v1.4.2）。
        Assert.Equal(["-c:v", "png", "-f", "image2", "palette"], cmd.Arguments.TakeLast(5));
    }

    [Fact]
    public void GifIgnoresAudioAndBurnsSubtitlesBeforeFpsAndScale()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, [new CutSegment { Start = 5, End = 10 }]);
        var cmd = FFmpegArguments.GifPalette("in.mp4", keep, Meta(audio: true), "subtitles.ass", GifPreset.Light, "p.png", 9);
        var filter = cmd.Arguments[cmd.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("atrim", filter);
        Assert.Contains("concat=n=2:v=1:a=0[vcat];[vcat]subtitles=filename=subtitles.ass,fps=8,scale=", filter);
        Assert.Contains("palettegen=max_colors=128", filter);
    }

    [Fact]
    public void EncodePassUsesPaletteDiffRectangleAndLoops()
    {
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, []);
        var cmd = FFmpegArguments.GifEncode("in.mp4", keep, Meta(), "subtitles.ass", GifPreset.High, "palette", "o.gif", 9);
        var args = cmd.Arguments.ToList();
        // パレットは拡張子なしの一時ファイルなので、形式（PNG）を明示して読む（v1.4.2）。
        Assert.Equal(["-i", "in.mp4", "-f", "png_pipe", "-i", "palette"], args.Skip(3).Take(6));
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal("[0:v:0]subtitles=filename=subtitles.ass,fps=15,scale=w='min(iw,1920)':h='min(ih,1440)':force_original_aspect_ratio=decrease:flags=lanczos,null[gifsrc];[gifsrc][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle[outv]", filter);
        Assert.Contains("-an", args);
        Assert.Equal("0", args[args.IndexOf("-loop") + 1]);
        Assert.Equal("gif", args[args.LastIndexOf("-f") + 1]);
        Assert.Equal("o.gif", args[^1]);
    }

    [Fact]
    public void LongFilterGoesToScript()
    {
        var cuts = Enumerable.Range(0, 400).Select(i => new CutSegment { Start = i * 0.15 + 0.01, End = i * 0.15 + 0.1 }).ToList();
        var keep = EditTimelineCalculator.ComputeKeepRanges(60, cuts);
        var cmd = FFmpegArguments.GifEncode("in.mp4", keep, Meta(), null, GifPreset.Standard, "p.png", "o.gif", 9);
        Assert.NotNull(cmd.FilterScript);
        Assert.Contains("-/filter_complex", cmd.Arguments);
        Assert.Contains("paletteuse", cmd.FilterScript);
    }

    [Fact]
    public void DefaultOutputPath()
    {
        Assert.Equal(@"C:\v\rec_edited.gif", Core.Export.GifExportService.DefaultOutputPath(@"C:\v\rec.mp4", new GifExportSettings()));
        Assert.Equal(@"D:\g\x.gif", Core.Export.GifExportService.DefaultOutputPath(@"C:\v\rec.mp4", new GifExportSettings { OutputDirectory = @"D:\g", OutputFileName = "x.gif" }));
    }
}

/// <summary>RecordingService の状態遷移（ffmpeg は偽物）。</summary>
public class RecordingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-recsvc-" + Guid.NewGuid().ToString("N"));

    public RecordingServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class FakeProcess(string file, string workDir, bool failOnStart) : IRecordingProcess
    {
        public bool Stopped { get; private set; }
        public bool HasExited => Stopped;
        public string ErrorTail => failOnStart ? "Could not find audio only device" : "";
        public event EventHandler? Exited;

        public Task StartAsync(CancellationToken ct)
        {
            if (failOnStart) throw new RecordingException("録画を開始できませんでした。", ErrorTail);
            File.WriteAllBytes(Path.Combine(workDir, file), new byte[4096]);
            return Task.CompletedTask;
        }

        public Task<bool> StopAsync(TimeSpan timeout)
        {
            Stopped = true;
            return Task.FromResult(true);
        }

        public void Crash() => Exited?.Invoke(this, EventArgs.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory : IRecordingProcessFactory
    {
        public List<(IReadOnlyList<string> Args, string WorkDir)> Calls { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public bool FailNext { get; set; }

        public IRecordingProcess Create(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Calls.Add((arguments, workingDirectory));
            var p = new FakeProcess(arguments[^1], workingDirectory, FailNext);
            Processes.Add(p);
            return p;
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public string? ConcatList { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var list = Path.Combine(request.WorkingDirectory!, FFmpegArguments.ConcatListFileName);
            if (File.Exists(list)) ConcatList = File.ReadAllText(list);
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory!, request.Arguments[^1]), new byte[2048]);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class FakeMetadata(double duration = 5, bool hasVideo = true) : IMediaMetadataService
    {
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
            => Task.FromResult(new VideoMetadata { Duration = duration, HasVideo = hasVideo, Width = 1280, Height = 720 });
    }

    private RecordingStartRequest Request(AudioInputSpec? audio = null) => new("ffmpeg.exe", "ffprobe.exe",
        new ScreenRecordingOptions(new CaptureRegion(0, 0, 1280, 720), 30, true, audio), Path.Combine(_dir, "rec.mp4"), new RecordingInfo());

    [Fact]
    public async Task StartPauseResumeStopConcatenatesSegments()
    {
        var factory = new FakeFactory();
        var runner = new FakeRunner();
        var svc = new RecordingService(factory, runner, new FakeMetadata());
        var states = new List<RecordingState>();
        svc.StateChanged += (_, _) => states.Add(svc.State);

        await svc.StartAsync(Request());
        Assert.Equal(RecordingState.Recording, svc.State);
        await svc.PauseAsync();
        Assert.Equal(RecordingState.Paused, svc.State);
        Assert.True(factory.Processes[0].Stopped); // 一時停止で区間を閉じる
        var pausedAt = svc.Elapsed;
        await Task.Delay(120);
        Assert.Equal(pausedAt, svc.Elapsed); // 一時停止中は時間が進まない
        await svc.ResumeAsync();
        Assert.Equal(RecordingState.Recording, svc.State);
        var result = await svc.StopAsync();

        Assert.Equal(RecordingState.Completed, svc.State);
        Assert.Equal([RecordingState.Starting, RecordingState.Recording, RecordingState.Paused, RecordingState.Starting,
            RecordingState.Recording, RecordingState.Stopping, RecordingState.Completed], states);
        Assert.Equal(["seg001.mkv", "seg002.mkv"], factory.Calls.Select(c => c.Args[^1]));
        Assert.Equal("file 'seg001.mkv'\nfile 'seg002.mkv'\n", runner.ConcatList);
        Assert.Contains("concat", runner.Requests.Single().Arguments);
        Assert.Equal(2, result.SegmentCount);
        Assert.Equal(2, result.Info.SegmentCount);
        Assert.True(File.Exists(Path.Combine(_dir, "rec.mp4")));
        Assert.False(Directory.Exists(factory.Calls[0].WorkDir)); // 作業フォルダーは削除
    }

    [Fact]
    public async Task StopWithoutPauseUsesSingleSegment()
    {
        var factory = new FakeFactory();
        var runner = new FakeRunner();
        var svc = new RecordingService(factory, runner, new FakeMetadata());
        await svc.StartAsync(Request());
        var result = await svc.StopAsync();
        Assert.Equal(1, result.SegmentCount);
        Assert.DoesNotContain("concat", runner.Requests.Single().Arguments);
        Assert.Equal(TimeSpan.FromSeconds(5), result.RecordedDuration);
    }

    [Fact]
    public async Task StartFailureCleansUpAndReportsDetail()
    {
        var factory = new FakeFactory { FailNext = true };
        var svc = new RecordingService(factory, new FakeRunner(), new FakeMetadata());
        var ex = await Assert.ThrowsAsync<RecordingException>(() => svc.StartAsync(Request(AudioInputSpec.DirectShow("x", "x"))));
        Assert.Contains("audio", ex.Detail);
        Assert.Equal(RecordingState.Failed, svc.State);
        Assert.False(Directory.Exists(factory.Calls[0].WorkDir));
        // 失敗後にもう一度始められる
        factory.FailNext = false;
        await svc.StartAsync(Request());
        Assert.Equal(RecordingState.Recording, svc.State);
        await svc.AbortAsync();
    }

    [Fact]
    public async Task BrokenOutputIsReportedAndSegmentsAreKept()
    {
        var factory = new FakeFactory();
        var svc = new RecordingService(factory, new FakeRunner(), new FakeMetadata(duration: 0, hasVideo: false));
        await svc.StartAsync(Request());
        var ex = await Assert.ThrowsAsync<RecordingException>(() => svc.StopAsync());
        Assert.Contains("壊れている", ex.Message);
        Assert.Equal(RecordingState.Failed, svc.State);
        Assert.True(File.Exists(Path.Combine(factory.Calls[0].WorkDir, "seg001.mkv"))); // 復旧用に残す
        Assert.False(File.Exists(Path.Combine(_dir, "rec.mp4")));
    }

    [Fact]
    public async Task UnexpectedExitRaisesInterrupted()
    {
        var factory = new FakeFactory();
        var svc = new RecordingService(factory, new FakeRunner(), new FakeMetadata());
        string? detail = null;
        svc.Interrupted += (_, d) => detail = d;
        await svc.StartAsync(Request());
        factory.Processes[0].Crash();
        Assert.NotNull(detail);
        await svc.AbortAsync();
    }

    [Fact]
    public async Task AbortStopsProcessAndDeletesWork()
    {
        var factory = new FakeFactory();
        var svc = new RecordingService(factory, new FakeRunner(), new FakeMetadata());
        await svc.StartAsync(Request());
        await svc.AbortAsync();
        Assert.True(factory.Processes[0].Stopped);
        Assert.False(Directory.Exists(factory.Calls[0].WorkDir));
        Assert.False(File.Exists(Path.Combine(_dir, "rec.mp4")));
    }

    [Fact]
    public async Task CannotStartTwice()
    {
        var svc = new RecordingService(new FakeFactory(), new FakeRunner(), new FakeMetadata());
        await svc.StartAsync(Request());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(Request()));
        await svc.AbortAsync();
    }
}
