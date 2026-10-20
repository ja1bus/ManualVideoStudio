using System.Text.Json;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Join;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>ffprobe の JSON を作る補助（動画の条件だけを変えて試すため）。</summary>
internal static class JoinJson
{
    public static string Make(int width = 1280, int height = 720, string fps = "30/1", string timeBase = "1/15360", string codec = "h264",
        string pix = "yuv420p", string profile = "Constrained Baseline", bool audio = true, int sampleRate = 48000, int channels = 2,
        string layout = "stereo", string audioCodec = "aac", double duration = 10)
    {
        var a = audio
            ? $$""",{ "codec_type": "audio", "codec_name": "{{audioCodec}}", "sample_rate": "{{sampleRate}}", "channels": {{channels}}, "channel_layout": "{{layout}}" }"""
            : "";
        return $$"""
        { "streams": [
            { "codec_type": "video", "codec_name": "{{codec}}", "profile": "{{profile}}", "width": {{width}}, "height": {{height}},
              "pix_fmt": "{{pix}}", "r_frame_rate": "{{fps}}", "avg_frame_rate": "{{fps}}", "time_base": "{{timeBase}}" }
            {{a}}
          ],
          "format": { "duration": "{{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}}", "size": "1000" } }
        """;
    }

    public static JoinStreamInfo Info(string json) => JoinStreamParser.Parse(json);
}

/// <summary>v1.18: 動画を追加して録画（連結）。ストリーム検査・連結の可否・連結サービス・結合元の記録。</summary>
public class JoinCompatibilityTests
{
    [Fact]
    public void ParserReadsStreamsAndIgnoresAttachedPictures()
    {
        var info = JoinJson.Info(JoinJson.Make(width: 1920, height: 1080, fps: "30000/1001"));
        Assert.Equal(1920, info.Video.Width);
        Assert.Equal("h264", info.Video.Codec);
        Assert.Equal("Constrained Baseline", info.Video.Profile);
        Assert.Equal(30000.0 / 1001, info.Video.Fps, 6); // 丸めた平均値ではなく公称の値
        Assert.Equal("1/15360", info.Video.TimeBase);
        Assert.Equal(48000, info.Audio!.SampleRate);
        Assert.Equal(10, info.Duration);

        Assert.Null(JoinJson.Info(JoinJson.Make(audio: false)).Audio);

        var withCover = """
        { "streams": [ { "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
                       { "codec_type": "video", "codec_name": "h264", "width": 10, "height": 10, "r_frame_rate": "30/1" } ], "format": {} }
        """;
        Assert.Equal("h264", JoinStreamParser.Parse(withCover).Video.Codec);
        Assert.Throws<InvalidOperationException>(() => JoinStreamParser.Parse("""{ "streams": [ { "codec_type": "audio" } ], "format": {} }"""));
    }

    [Fact]
    public void IdenticalStreamsAreCompatibleEvenWithDifferentLengths()
    {
        var a = JoinJson.Info(JoinJson.Make(duration: 10));
        var b = JoinJson.Info(JoinJson.Make(duration: 123));
        Assert.Empty(JoinCompatibility.Compare(a, b));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("fps")]
    [InlineData("timebase")]
    [InlineData("codec")]
    [InlineData("pix")]
    [InlineData("profile")]
    [InlineData("audio-missing")]
    [InlineData("audio-rate")]
    [InlineData("audio-channels")]
    [InlineData("audio-codec")]
    public void DifferentConditionsAreReportedByItem(string what)
    {
        var a = JoinJson.Info(JoinJson.Make());
        var b = JoinJson.Info(what switch
        {
            "size" => JoinJson.Make(width: 1920, height: 1080),
            "fps" => JoinJson.Make(fps: "15/1"),
            "timebase" => JoinJson.Make(timeBase: "1/90000"),
            "codec" => JoinJson.Make(codec: "hevc"),
            "pix" => JoinJson.Make(pix: "yuv444p"),
            "profile" => JoinJson.Make(profile: "High"),
            "audio-missing" => JoinJson.Make(audio: false),
            "audio-rate" => JoinJson.Make(sampleRate: 44100),
            "audio-channels" => JoinJson.Make(channels: 1, layout: "mono"),
            "audio-codec" => JoinJson.Make(audioCodec: "mp3"),
            _ => throw new ArgumentException(what),
        });
        var diffs = JoinCompatibility.Compare(a, b);
        Assert.NotEmpty(diffs);
        // 違う項目の名前と、両方の値が分かる。
        Assert.All(diffs, d => Assert.Contains("（", d));
    }

    [Fact]
    public void AppendableRecordingFromThisAppIsAccepted()
    {
        var req = JoinCompatibility.CheckAppendable(JoinJson.Info(JoinJson.Make(width: 1280, height: 720, fps: "30/1")), out var problems);
        Assert.Empty(problems);
        Assert.Equal(new AppendRequirement(1280, 720, 30, true, req!.Description), req);
        Assert.Contains("1280x720", req.Description);

        var silent = JoinCompatibility.CheckAppendable(JoinJson.Info(JoinJson.Make(fps: "15/1", audio: false)), out _);
        Assert.Equal(15, silent!.Fps);
        Assert.False(silent.HasAudio);
    }

    [Theory]
    [InlineData("hevc", "yuv420p", "30/1", 1280, 720, true, 48000)]
    [InlineData("h264", "yuv444p", "30/1", 1280, 720, true, 48000)]
    [InlineData("h264", "yuv420p", "60/1", 1280, 720, true, 48000)]
    [InlineData("h264", "yuv420p", "30000/1001", 1280, 720, true, 48000)]
    [InlineData("h264", "yuv420p", "30/1", 1281, 720, true, 48000)]
    [InlineData("h264", "yuv420p", "30/1", 8, 8, true, 48000)]
    [InlineData("h264", "yuv420p", "30/1", 1280, 720, true, 44100)]
    public void UnrecordableSourcesAreRefusedBeforeRecording(string codec, string pix, string fps, int w, int h, bool audio, int rate)
    {
        var req = JoinCompatibility.CheckAppendable(JoinJson.Info(JoinJson.Make(width: w, height: h, fps: fps, codec: codec, pix: pix, audio: audio, sampleRate: rate)), out var problems);
        Assert.Null(req);
        Assert.NotEmpty(problems);
    }
}

public class JoinFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-join-" + Guid.NewGuid().ToString("N"));

    public JoinFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void ConcatListUsesForwardSlashesAndEscapesQuotes()
    {
        var a = Path.Combine(_dir, "日付 '1'", "録画 #1.mp4");
        var list = FFmpegArguments.ConcatListForPaths([a, Path.Combine(_dir, "b.mp4")]);
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain('\\', lines[0].Replace("'\\''", ""));
        Assert.Contains("日付 '\\''1'\\''/録画 #1.mp4", lines[0]);
        Assert.StartsWith("file '", lines[0]);
        Assert.EndsWith("'", lines[0]);
        Assert.Equal(".mp4'", lines[1][^5..]);
        Assert.Equal('\n', list[^1]);
        Assert.False(list.StartsWith('\uFEFF'));
    }

    [Fact]
    public void ConcatArgumentsCopyStreamsWithoutReencoding()
    {
        var args = FFmpegArguments.ConcatVideos("inputs", "out.mp4");
        Assert.Contains("concat", args);
        Assert.Equal("0", args[args.ToList().IndexOf("-safe") + 1]);
        Assert.Equal("copy", args[args.ToList().IndexOf("-c") + 1]);
        Assert.Contains("+faststart", args);
        Assert.Equal("out.mp4", args[^1]);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void SourcesFileRoundTripsAndOnlyMatchesItsOwnVideo()
    {
        var folder = Path.Combine(_dir, "j");
        Directory.CreateDirectory(folder);
        var file = new JoinSourcesFile
        {
            CreatedAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(9)),
            OutputFileName = "join_1.mp4",
            Sources = [new JoinSourceEntry { DisplayName = "初日", FileName = "rec_1.mp4", DurationSeconds = 12.5 }],
        };
        file.Save(folder);
        var json = File.ReadAllText(JoinSourcesFile.PathIn(folder));
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("初日", json);                // 日本語をそのまま書く
        Assert.DoesNotContain(_dir, json);           // 絶対パスを持たない
        Assert.False(File.Exists(JoinSourcesFile.PathIn(folder) + ".tmp"));

        var read = JoinSourcesFile.TryReadFor(Path.Combine(folder, "JOIN_1.mp4"));
        Assert.Equal("初日", Assert.Single(read!.Sources).DisplayName);
        Assert.Null(JoinSourcesFile.TryReadFor(Path.Combine(folder, "other.mp4")));
    }

    [Fact]
    public void BrokenOrMissingSourcesFileIsIgnored()
    {
        var folder = Path.Combine(_dir, "k");
        Directory.CreateDirectory(folder);
        Assert.Null(JoinSourcesFile.TryReadFor(Path.Combine(folder, "a.mp4")));
        File.WriteAllText(JoinSourcesFile.PathIn(folder), "{ not json");
        Assert.Null(JoinSourcesFile.TryReadFor(Path.Combine(folder, "a.mp4")));
        File.WriteAllText(JoinSourcesFile.PathIn(folder), """{ "schemaVersion": 99, "outputFileName": "a.mp4", "sources": [ {} ] }""");
        Assert.Null(JoinSourcesFile.TryReadFor(Path.Combine(folder, "a.mp4")));
    }

    [Fact]
    public void StaleWorkFoldersAreCleanedButRecentOnesAndProjectsStay()
    {
        var stale = Directory.CreateDirectory(Path.Combine(_dir, RecordingJoinService.WorkFolderPrefix + "old")).FullName;
        var recent = Directory.CreateDirectory(Path.Combine(_dir, RecordingJoinService.WorkFolderPrefix + "new")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(_dir, "20261020_1000_手順")).FullName;
        Directory.SetLastWriteTime(stale, DateTime.Now.AddDays(-3));
        RecordingJoinService.CleanStaleWorkFolders(_dir);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(project));
    }

    [Fact]
    public void WorkFoldersAreHiddenFromTheProjectList()
    {
        Directory.CreateDirectory(Path.Combine(_dir, RecordingJoinService.WorkFolderPrefix + "abc"));
        File.WriteAllBytes(Path.Combine(_dir, RecordingJoinService.WorkFolderPrefix + "abc", "output.mp4"), new byte[2048]);
        Assert.Null(ProjectIndexStore.ScanRecording(Path.Combine(_dir, RecordingJoinService.WorkFolderPrefix + "abc")));
    }

    [Fact]
    public void JoinedFolderLooksLikeAnOrdinaryRecordingFolder()
    {
        // 結合した動画のフォルダー: 動画と join-sources.json と通常のプロジェクト。録画の行として一覧に出る。
        var folder = Path.Combine(_dir, "20261020_1100_つなぎ");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "join_20261020_110000.mp4"), new byte[2048]);
        new JoinSourcesFile { OutputFileName = "join_20261020_110000.mp4", Sources = [new JoinSourceEntry { FileName = "a.mp4" }] }.Save(folder);
        var rec = ProjectIndexStore.ScanRecording(folder);
        Assert.NotNull(rec);
        Assert.Equal("join_20261020_110000.mp4", rec!.Video);
        Assert.Equal("つなぎ", rec.Name);
        Assert.Equal(Path.Combine(folder, "join_20261020_110000.mp4"), ProjectFolders.FindVideo(folder));
    }

    [Fact]
    public void ArchiveIncludesTheSourcesFileWithTheFolder()
    {
        var folder = Path.Combine(_dir, "20261020_1100_つなぎ");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "join_1.mp4"), new byte[2048]);
        new JoinSourcesFile { OutputFileName = "join_1.mp4", Sources = [new JoinSourceEntry { FileName = "a.mp4" }] }.Save(folder);
        Assert.Contains(JoinSourcesFile.FileName, ProjectArchive.EnumerateArchiveFiles(folder).Select(Path.GetFileName));
    }
}

public class RecordingJoinServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-joinsvc-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public RecordingJoinServiceTests()
    {
        _root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Runner : IProcessRunner
    {
        public Dictionary<string, string> Probe { get; } = [];
        public bool FfmpegFails { get; set; }
        public bool Cancel { get; set; }
        public string? List { get; private set; }
        public List<IReadOnlyList<string>> FfmpegCalls { get; } = [];
        public string? WorkDirDuringRun { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            if (Path.GetFileName(request.FileName).StartsWith("ffprobe", StringComparison.Ordinal))
            {
                var path = request.Arguments[^1];
                return Task.FromResult(Probe.TryGetValue(path, out var json)
                    ? new ProcessResult(0, json, "", TimeSpan.Zero)
                    : new ProcessResult(1, "", "No such file", TimeSpan.Zero));
            }
            FfmpegCalls.Add(request.Arguments);
            WorkDirDuringRun = request.WorkingDirectory;
            var listPath = request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1];
            List = File.ReadAllText(listPath);
            if (Cancel) throw new OperationCanceledException();
            if (FfmpegFails) return Task.FromResult(new ProcessResult(1, "", "Invalid data found", TimeSpan.Zero));
            File.WriteAllBytes(request.Arguments[^1], new byte[4096]);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class Meta(double duration, bool hasVideo = true) : IMediaMetadataService
    {
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
            => Task.FromResult(new VideoMetadata { Duration = duration, HasVideo = hasVideo, Width = 1280, Height = 720, Fps = 30 });
    }

    private string Clip(string folderName, string json, Runner runner, string file = "rec.mp4")
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        File.WriteAllBytes(path, new byte[3000]);
        runner.Probe[path] = json;
        return path;
    }

    private JoinRequest Request(IEnumerable<string> paths, string? name = "手順") => new("ffmpeg", "ffprobe", _root,
        paths.Select(p => new JoinInput(p, Path.GetFileName(Path.GetDirectoryName(p))!)).ToList(), name, new DateTime(2026, 10, 20, 11, 0, 0));

    private string[] RootEntries() => Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    [Fact]
    public async Task JoinsInOrderIntoANewFolderAndLeavesTheSourcesAlone()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(duration: 5), runner);
        var sizeA = new FileInfo(a).Length;
        var result = await new RecordingJoinService(runner, new Meta(15)).JoinAsync(Request([a, b]));

        Assert.Equal(Path.Combine(_root, "20261020_1100_手順"), result.Folder);
        Assert.StartsWith("join_20261020_110000", Path.GetFileName(result.VideoPath));
        Assert.True(File.Exists(result.VideoPath));
        Assert.True(result.SourcesSaved);
        Assert.Equal(15, result.Metadata.Duration);
        // 一覧ファイルは再生順で、/ 区切りの絶対パス。
        var lines = runner.List!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("20261019_1000_初日/rec.mp4", lines[0]);
        Assert.Contains("20261020_1000_追加/rec.mp4", lines[1]);
        Assert.Contains("-c", runner.FfmpegCalls[0]);
        // 元の動画は変更しない。作業フォルダーは残らない。
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal(sizeA, new FileInfo(a).Length);
        Assert.DoesNotContain(RootEntries(), e => e.StartsWith(RecordingJoinService.WorkFolderPrefix));
        // 結合元の記録（表示名・ファイル名・長さの順）。
        var sources = JoinSourcesFile.TryReadFor(result.VideoPath)!;
        Assert.Equal(["20261019_1000_初日", "20261020_1000_追加"], sources.Sources.Select(s => s.DisplayName));
        Assert.Equal([10, 5], sources.Sources.Select(s => s.DurationSeconds));
        Assert.DoesNotContain(_root.Replace("\\", "\\\\"), File.ReadAllText(JoinSourcesFile.PathIn(result.Folder)));
    }

    [Fact]
    public async Task ReJoinedVideoInheritsTheOriginalOrder()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_二日目", JoinJson.Make(duration: 5), runner);
        var first = await new RecordingJoinService(runner, new Meta(15)).JoinAsync(Request([a, b], "つなぎ1"));
        runner.Probe[first.VideoPath] = JoinJson.Make(duration: 15);
        var c = Clip("20261021_1000_三日目", JoinJson.Make(duration: 7), runner);

        var second = await new RecordingJoinService(runner, new Meta(22)).JoinAsync(Request([first.VideoPath, c], "つなぎ2"));
        var sources = JoinSourcesFile.TryReadFor(second.VideoPath)!;
        Assert.Equal(["20261019_1000_初日", "20261020_1000_二日目", "20261021_1000_三日目"], sources.Sources.Select(s => s.DisplayName));
        Assert.Equal(22, sources.Sources.Sum(s => s.DurationSeconds));
        // 前回の結合結果は変更しない。
        Assert.True(File.Exists(first.VideoPath));
        Assert.Equal(2, JoinSourcesFile.TryReadFor(first.VideoPath)!.Sources.Count);
    }

    [Fact]
    public async Task ReJoinStillWorksWhenThePreviousRecordIsLost()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_二日目", JoinJson.Make(duration: 5), runner);
        var first = await new RecordingJoinService(runner, new Meta(15)).JoinAsync(Request([a, b], "つなぎ1"));
        File.Delete(JoinSourcesFile.PathIn(first.Folder));
        runner.Probe[first.VideoPath] = JoinJson.Make(duration: 15);
        var c = Clip("20261021_1000_三日目", JoinJson.Make(duration: 7), runner);

        var second = await new RecordingJoinService(runner, new Meta(22)).JoinAsync(Request([first.VideoPath, c], "つなぎ2"));
        Assert.Equal(2, JoinSourcesFile.TryReadFor(second.VideoPath)!.Sources.Count); // 結合済み 1 件＋追加 1 件
        Assert.True(File.Exists(second.VideoPath));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("fps")]
    [InlineData("audio")]
    public async Task MismatchedConditionsAreRefusedWithTheFileAndItemAndNothingIsCreated(string what)
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(), runner);
        var b = Clip("20261020_1000_追加", what switch
        {
            "size" => JoinJson.Make(width: 1920, height: 1080),
            "fps" => JoinJson.Make(fps: "15/1"),
            _ => JoinJson.Make(audio: false),
        }, runner, "違う録画.mp4");
        var before = RootEntries();
        var ex = await Assert.ThrowsAsync<JoinException>(() => new RecordingJoinService(runner, new Meta(20)).JoinAsync(Request([a, b])));
        Assert.Contains(ex.Problems, p => p.StartsWith("違う録画.mp4:"));
        Assert.Empty(runner.FfmpegCalls);
        Assert.Equal(before, RootEntries());
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public async Task MissingInputIsReported()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(), runner);
        var gone = Path.Combine(_root, "消えた録画", "rec.mp4");
        var ex = await Assert.ThrowsAsync<JoinException>(() => new RecordingJoinService(runner, new Meta(20)).JoinAsync(Request([a, gone])));
        Assert.Contains("見つかりません", ex.Message);
        Assert.Empty(runner.FfmpegCalls);
    }

    [Fact]
    public async Task NeedsAtLeastTwoInputs()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(), runner);
        await Assert.ThrowsAsync<JoinException>(() => new RecordingJoinService(runner, new Meta(10)).JoinAsync(Request([a])));
    }

    [Fact]
    public async Task FfmpegFailureCleansTheWorkFolderAndKeepsEverythingElse()
    {
        var runner = new Runner { FfmpegFails = true };
        var a = Clip("20261019_1000_初日", JoinJson.Make(), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(), runner);
        var before = RootEntries();
        var ex = await Assert.ThrowsAsync<JoinException>(() => new RecordingJoinService(runner, new Meta(20)).JoinAsync(Request([a, b])));
        Assert.Contains("元の動画は変更していません", ex.Message);
        Assert.Contains("Invalid data", ex.Detail);
        Assert.Equal(before, RootEntries());
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public async Task CancelCleansTheWorkFolder()
    {
        var runner = new Runner { Cancel = true };
        var a = Clip("20261019_1000_初日", JoinJson.Make(), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(), runner);
        var before = RootEntries();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecordingJoinService(runner, new Meta(20)).JoinAsync(Request([a, b])));
        Assert.Equal(before, RootEntries());
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Theory]
    [InlineData(100)]   // 長さが想定とかけ離れている
    [InlineData(0)]     // 映像が読めない（長さ 0）
    public async Task SuspiciousOutputIsNotKept(double duration)
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(duration: 5), runner);
        var before = RootEntries();
        await Assert.ThrowsAsync<JoinException>(() => new RecordingJoinService(runner, new Meta(duration)).JoinAsync(Request([a, b])));
        Assert.Equal(before, RootEntries());
    }

    [Fact]
    public async Task SmallLengthDifferencesFromAudioPaddingAreAccepted()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(duration: 5), runner);
        var result = await new RecordingJoinService(runner, new Meta(15.3)).JoinAsync(Request([a, b]));
        Assert.True(File.Exists(result.VideoPath));
    }

    [Fact]
    public async Task EmptyNameMakesADateOnlyFolderAndSameNameGetsASuffix()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(duration: 5), runner);
        var svc = new RecordingJoinService(runner, new Meta(15));
        var first = await svc.JoinAsync(Request([a, b], ""));
        var second = await svc.JoinAsync(Request([a, b], ""));
        Assert.Equal(Path.Combine(_root, "20261020_1100"), first.Folder);
        Assert.Equal(Path.Combine(_root, "20261020_1100_2"), second.Folder);
    }

    [Fact]
    public async Task ProjectCanBeCreatedInTheJoinedFolderLikeAnyRecording()
    {
        var runner = new Runner();
        var a = Clip("20261019_1000_初日", JoinJson.Make(duration: 10), runner);
        var b = Clip("20261020_1000_追加", JoinJson.Make(duration: 5), runner);
        var result = await new RecordingJoinService(runner, new Meta(15)).JoinAsync(Request([a, b]));
        // MainViewModel が作るプロジェクト: recordingSettings は null（単一の録画ではない）。schemaVersion は変えない。
        var project = TestProjects.Create(15);
        project.SourceVideo = new SourceVideo { Path = result.VideoPath, FileSize = new FileInfo(result.VideoPath).Length };
        project.RecordingSettings = null;
        var path = Path.Combine(result.Folder, Path.GetFileName(result.Folder) + ProjectService.Extension);
        var service = new ProjectService();
        service.Save(project, path);
        var loaded = service.Load(path);
        Assert.True(loaded.VideoFound);
        Assert.Null(loaded.Project.RecordingSettings);
        Assert.Equal(ProjectDocument.CurrentSchemaVersion, loaded.Project.SchemaVersion);
        Assert.Empty(ProjectIndexStore.CheckProject(path));
        Assert.NotNull(ProjectIndexStore.ScanRecording(result.Folder));

        // 元のフォルダーを消しても、結合したプロジェクトは開ける（絶対パスに依存しない）。
        Directory.Delete(Path.GetDirectoryName(a)!, true);
        Directory.Delete(Path.GetDirectoryName(b)!, true);
        Assert.True(service.Load(path).VideoFound);
    }
}

/// <summary>v1.18: 録画画面の追加録画モード（固定する条件・設定を守る・完了画面の選択）。</summary>
public class RecorderAppendTests
{
    private sealed class Dialogs : IDialogService
    {
        public List<string> Messages { get; } = [];
        public void ShowInfo(string message, string title = "情報") => Messages.Add(message);
        public void ShowWarning(string message, string title = "注意") => Messages.Add(message);
        public void ShowError(string message, string? detail = null, string title = "エラー") => Messages.Add(message);
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    private sealed class Settings(AppSettings app) : ISettingsService
    {
        public AppSettings Current { get; } = app;
        public int Saves { get; private set; }
        public AppSettings Load() => Current;
        public void Save() => Saves++;
    }

    private sealed class Targets(int width, int height) : ICaptureTargetService
    {
        public IReadOnlyList<CaptureWindow> ListWindows() => [];
        public IReadOnlyList<CaptureMonitor> ListMonitors() => [];
        public CaptureRegion VirtualScreen() => new(0, 0, width, height);
        public CaptureRegion? GetWindowBounds(IntPtr handle) => null;
        public bool BringToFront(IntPtr handle) => false;
        public CaptureMonitor MonitorOf(CaptureRegion region) => throw new NotSupportedException();
    }

    private sealed class Audio : IAudioInputDeviceService
    {
        public Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public string? GetDefaultDeviceName() => null;
        public AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName) => null;
    }

    private sealed class Info : IFFmpegInfoService
    {
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Recorder(string? output) : IRecordingService
    {
        public RecordingState State { get; private set; } = RecordingState.Idle;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(12);
        public int SegmentCount => 1;
        public event EventHandler? StateChanged;
        public event EventHandler<string>? Interrupted { add { } remove { } }
        public void Begin() { State = RecordingState.Recording; StateChanged?.Invoke(this, EventArgs.Empty); }
        public Task StartAsync(RecordingStartRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            await File.WriteAllBytesAsync(output!, new byte[16], ct);
            State = RecordingState.Completed;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new RecordingResult(output!, TimeSpan.FromSeconds(12), 1, new RecordingInfo { RecordedAt = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(9)) });
        }
        public Task AbortAsync() => Task.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("mvs-append-");
        public Dialogs D { get; } = new();
        public Settings S { get; }
        public Recorder R { get; }
        public RecorderViewModel Vm { get; }
        public List<RecordingOutcome?> Closed { get; } = [];

        public Harness(AppendRecordingContext? append, AppendDoneAction action = AppendDoneAction.Join, string name = "手順", int screenW = 1920, int screenH = 1080, RecordingSettings? recording = null)
        {
            var app = new AppSettings();
            if (recording is not null) app.Recording = recording;
            S = new Settings(app);
            R = new Recorder(Path.Combine(Dir.FullName, "rec_20260924_100000.mp4"));
            var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new Info(), S, NullLogger<FFmpegStatus>.Instance);
            Vm = new RecorderViewModel(S, R, new Targets(screenW, screenH), new Audio(), ffmpeg, D, NullLogger<RecorderViewModel>.Instance)
            {
                OutputDirectory = Dir.FullName,
                AskAfterStop = _ => RecordingStopChoice.Save,
                AskStartEditing = (_, _) => new RecordingDoneChoice(action == AppendDoneAction.Single, name, action),
            };
            if (append is not null) Vm.ApplyAppend(append);
            Vm.CloseRequested += o => Closed.Add(o);
        }

        public void Dispose() => Dir.Delete(true);
    }

    private static AppendRecordingContext Context(int w = 800, int h = 600, int fps = 15, bool audio = false)
        => new("初日", @"C:\x\rec.mp4", new AppendRequirement(w, h, fps, audio, $"{w}x{h} / {fps} fps"));

    [Fact]
    public void AppendModeFixesTargetSizeFpsAndAudioToTheSource()
    {
        using var h = new Harness(Context(800, 600, 15, audio: false));
        Assert.True(h.Vm.IsAppendMode);
        Assert.False(h.Vm.IsNotAppend);
        Assert.Equal(RecordingMode.Region, h.Vm.Mode);
        Assert.Equal(15, h.Vm.Fps);
        Assert.Equal(800, h.Vm.Region.Width);
        Assert.Equal(600, h.Vm.Region.Height);
        Assert.True(h.Vm.AppendFits);
        Assert.False(h.Vm.CanChooseAudio);
        Assert.False(h.Vm.MicrophoneEnabled);
        Assert.Contains("800x600", h.Vm.AppendBanner);
        Assert.Contains("引き継ぎません", h.Vm.AppendBanner);
        Assert.Contains("録画しません", h.Vm.AppendBanner);
    }

    [Fact]
    public void NormalModeIsUnchanged()
    {
        using var h = new Harness(null);
        Assert.False(h.Vm.IsAppendMode);
        Assert.True(h.Vm.IsNotAppend);
        Assert.True(h.Vm.CanChooseAudio);
        Assert.Equal("", h.Vm.AppendBanner);
    }

    [Fact]
    public void MicrophoneIsNeverEnabledAutomaticallyEvenWhenTheSourceHasAudio()
    {
        using var h = new Harness(Context(audio: true));
        Assert.True(h.Vm.CanChooseAudio);
        Assert.False(h.Vm.MicrophoneEnabled);
        Assert.Contains("マイク", h.Vm.AppendBanner);
    }

    [Fact]
    public void ResizeAndSizeChangesAreIgnoredButMovingKeepsTheSize()
    {
        using var h = new Harness(Context(800, 600));
        var start = h.Vm.Region;
        h.Vm.ResizeRegionByDrag(start, 1, 1, 200, 200);
        Assert.Equal(start, h.Vm.Region);
        h.Vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        h.Vm.RegionWidthText = "300";
        h.Vm.RegionHeightText = "300";
        h.Vm.ApplyRegionSizeCommand.Execute(null);
        Assert.Equal(800, h.Vm.Region.Width);
        Assert.Equal(600, h.Vm.Region.Height);
        Assert.False(h.Vm.IsAspectLocked);

        h.Vm.MoveRegionByDrag(start, 50, 30);
        Assert.Equal(start.X + 50, h.Vm.Region.X);
        Assert.Equal(start.Y + 30, h.Vm.Region.Y);
        Assert.Equal(800, h.Vm.Region.Width);
        // 画面の外へは出ない（大きさは変わらない）。
        h.Vm.MoveRegionByDrag(start, 99999, 99999);
        Assert.Equal(800, h.Vm.Region.Width);
        Assert.Equal(600, h.Vm.Region.Height);
        Assert.True(h.Vm.Region.Right <= 1920 && h.Vm.Region.Bottom <= 1080);
    }

    [Fact]
    public void SizeBelowTheNormalMinimumIsStillKeptWhenMoving()
    {
        using var h = new Harness(Context(32, 32));
        h.Vm.MoveRegionByDrag(h.Vm.Region, 10, 10);
        Assert.Equal(32, h.Vm.Region.Width);
        Assert.Equal(32, h.Vm.Region.Height);
    }

    [Fact]
    public void FrameThatDoesNotFitTheScreenBlocksRecording()
    {
        using var h = new Harness(Context(3840, 2160), screenW: 1920, screenH: 1080);
        Assert.False(h.Vm.AppendFits);
        Assert.Contains("収まらない", h.Vm.StatusText);
        // 開始しようとしても録画は始まらず、理由が出る（FFmpeg の確認より後に判定するため、ここでは状態だけを見る）。
        Assert.True(h.Vm.IsIdle);
    }

    [Fact]
    public void SavedNormalRecordingSettingsAreNotOverwrittenByAppendRecording()
    {
        var saved = new RecordingSettings { Mode = RecordingMode.Monitor, Fps = 30, RegionX = 11, RegionY = 22, RegionWidth = 1280, RegionHeight = 720, RegionSizePreset = RegionSizePreset.Hd720 };
        using var h = new Harness(Context(800, 600, 15), recording: saved);
        h.Vm.DrawCursor = false;
        h.Vm.SaveSettings();
        Assert.Equal(RecordingMode.Monitor, saved.Mode);
        Assert.Equal(30, saved.Fps);
        Assert.Equal((11, 22, 1280, 720), (saved.RegionX, saved.RegionY, saved.RegionWidth, saved.RegionHeight));
        Assert.Equal(RegionSizePreset.Hd720, saved.RegionSizePreset);
        Assert.False(saved.DrawCursor);     // カーソルの有無は選べるので保存する
        Assert.True(h.S.Saves > 0);
    }

    [Fact]
    public void NormalRecordingStillSavesItsSettings()
    {
        var saved = new RecordingSettings { Fps = 30 };
        using var h = new Harness(null, recording: saved);
        h.Vm.Fps = 15;
        h.Vm.SaveSettings();
        Assert.Equal(15, saved.Fps);
    }

    [Fact]
    public async Task JoinChoiceKeepsTheAdditionalRecordingInItsOwnFolder()
    {
        using var h = new Harness(Context(), AppendDoneAction.Join, "手順");
        h.R.Begin();
        await h.Vm.StopCommand.ExecuteAsync(null);
        var o = Assert.Single(h.Closed)!;
        Assert.True(o.JoinWithSource);
        Assert.False(o.StartEditing);
        Assert.Equal("手順", o.ProjectName);
        Assert.Equal("20260924_1000_手順_追加録画", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
        Assert.True(File.Exists(o.OutputPath));
    }

    [Fact]
    public async Task JoinChoiceWithoutANameUsesAPlainFolderName()
    {
        using var h = new Harness(Context(), AppendDoneAction.Join, "");
        h.R.Begin();
        await h.Vm.StopCommand.ExecuteAsync(null);
        var o = Assert.Single(h.Closed)!;
        Assert.Equal("20260924_1000_追加録画", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }

    [Fact]
    public async Task SingleChoiceEditsTheAdditionalRecordingAlone()
    {
        using var h = new Harness(Context(), AppendDoneAction.Single, "単体");
        h.R.Begin();
        await h.Vm.StopCommand.ExecuteAsync(null);
        var o = Assert.Single(h.Closed)!;
        Assert.False(o.JoinWithSource);
        Assert.True(o.StartEditing);
        Assert.Equal("20260924_1000_単体", Path.GetFileName(Path.GetDirectoryName(o.OutputPath)));
    }
}

/// <summary>v1.18: プロジェクト一覧の［動画を追加して録画…］。</summary>
public class ProjectListAppendTests
{
    private sealed class Shell : IShellService
    {
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) { }
        public void OpenUrl(string url) { }
    }

    private static (ProjectListViewModel Vm, ProjectListViewModelTests.Dialogs D) Create(TempRoot t)
    {
        var d = new ProjectListViewModelTests.Dialogs();
        var vm = new ProjectListViewModel(new ProjectListViewModelTests.Settings(t.Root), d, new ProjectListViewModelTests.Windows(), new Shell(),
            new ProjectListViewModelTests.Trash(Path.Combine(t.Base, "trash")), NullLogger<ProjectListViewModel>.Instance);
        return (vm, d);
    }

    [Fact]
    public void RecordingRowAndEditRowBothStartAnAppend()
    {
        using var t = new TempRoot();
        var a = t.Recording("20260923_1015_a");
        var video = Path.Combine(a, "rec_20260923_101500.mp4");
        var edit = TempRoot.SaveProject(Path.Combine(a, "edits", "20260925_0930_b", "20260925_0930_b.mvsproj.json"), video);
        var (vm, _) = Create(t);
        vm.Load();
        ProjectListAction? closed = null;
        vm.CloseRequested += x => closed = x;

        vm.SetSelection([vm.Rows.Single(r => r.IsRecording)]);
        Assert.True(vm.AppendRecordingCommand.CanExecute(null));
        vm.AppendRecordingCommand.Execute(null);
        Assert.Equal(new ProjectListAction(ProjectListActionKind.AppendRecording, a), closed);

        vm.SetSelection([vm.Rows.Single(r => !r.IsRecording)]);
        vm.AppendRecordingCommand.Execute(null);
        Assert.Equal(new ProjectListAction(ProjectListActionKind.AppendRecording, edit), closed);
    }

    [Fact]
    public void NeedsExactlyOneRow()
    {
        using var t = new TempRoot();
        t.Recording("20260923_1015_a");
        t.Recording("20260920_0900_c", "rec_20260920_090000.mp4");
        var (vm, _) = Create(t);
        vm.Load();
        vm.SetSelection([]);
        Assert.False(vm.AppendRecordingCommand.CanExecute(null));
        vm.SetSelection(vm.Rows.ToList());
        Assert.False(vm.AppendRecordingCommand.CanExecute(null));
        vm.SetSelection([vm.Rows[0]]);
        Assert.True(vm.AppendRecordingCommand.CanExecute(null));
    }

    [Fact]
    public void RecordingWithoutAVideoIsRefusedWithAReason()
    {
        using var t = new TempRoot();
        var folder = Path.Combine(t.Root, "20260923_1015_a");
        var elsewhere = Path.Combine(t.Base, "外", "rec.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllBytes(elsewhere, new byte[2048]);
        TempRoot.SaveProject(Path.Combine(folder, "20260923_1015_a.mvsproj.json"), elsewhere);
        var (vm, d) = Create(t);
        vm.Load();
        ProjectListAction? closed = null;
        vm.CloseRequested += x => closed = x;
        vm.SetSelection([vm.Rows.Single(r => r.IsRecording)]);
        vm.AppendRecordingCommand.Execute(null);
        Assert.Null(closed);
        Assert.Contains(d.Messages, m => m.StartsWith("warn:") && m.Contains("動画が見つかりません"));
    }
}
