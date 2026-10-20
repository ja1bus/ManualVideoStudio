using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Join;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// v1.18: 動画を追加して録画（連結）を実 ffmpeg で確認する。
/// 録画と同じ条件の動画（H.264 ultrafast・crf 20・yuv420p・cfr・音声は AAC 48 kHz ステレオ）を lavfi で作り、
/// 録画の区間連結と同じ手順（mkv → MP4 へコピー）で MP4 にする。パスには空白・日本語・' ・# を含める。
/// </summary>
public sealed class JoinIntegrationTests(MediaFixture fx, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private string NewRoot()
    {
        var root = Path.Combine(fx.Dir, "保存先 " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>録画と同じ作りの MP4 を、root の下の「日時_名前」フォルダーに作る。</summary>
    private async Task<string> MakeRecordingAsync(string root, string folder, double seconds, string color, int fps = 30, bool audio = true,
        string size = "320x240", int hz = 440)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var mkv = Path.Combine(dir, "seg001.mkv");
        List<string> args = ["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c={color}:s={size}:r={fps}:d={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"];
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={hz}:sample_rate=48000:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"]);
        args.AddRange(["-map", "0:v:0"]);
        if (audio) args.AddRange(["-map", "1:a:0"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-r", fps.ToString(), "-fps_mode", "cfr", "-g", (fps * 2).ToString()]);
        if (audio) args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ac", "2", "-ar", "48000"]);
        else args.Add("-an");
        args.AddRange(["-f", "matroska", mkv]);
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, args));
        Assert.True(r.Succeeded, r.ErrorTail());

        var mp4 = Path.Combine(dir, $"rec_{folder[..8]}_100000.mp4");
        var remux = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, FFmpegArguments.ConcatRecordingSegments(["seg001.mkv"], "unused", mp4))
        {
            WorkingDirectory = dir,
        });
        Assert.True(remux.Succeeded, remux.ErrorTail());
        File.Delete(mkv);
        return mp4;
    }

    private RecordingJoinService Service() => new(fx.Runner, new MediaMetadataService(fx.Runner));

    private JoinRequest Request(string root, string name, params string[] videos) => new(Ffmpeg, Ffprobe, root,
        videos.Select(v => new JoinInput(v, Path.GetFileName(Path.GetDirectoryName(v))!)).ToList(), name, new DateTime(2026, 10, 20, 11, 0, 0));

    private async Task<(double Video, double Audio)> StreamDurationsAsync(string path)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffprobe, ["-v", "error", "-print_format", "json", "-show_streams", path]));
        using var doc = System.Text.Json.JsonDocument.Parse(r.StandardOutput);
        double v = 0, a = 0;
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var d = double.Parse(s.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            if (s.GetProperty("codec_type").GetString() == "video") v = d; else a = d;
        }
        return (v, a);
    }

    /// <summary>time 秒の画面の中央の色（R, G, B）。</summary>
    private async Task<(int R, int G, int B)> CenterColorAsync(string video, double time)
    {
        var png = Path.Combine(fx.Dir, $"frame-{Guid.NewGuid():N}.png");
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, FFmpegArguments.FrameExtraction(video, time, png, ImageFormat.Png)));
        Assert.True(r.Succeeded, r.ErrorTail());
        var frame = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bmp.CopyPixels(new System.Windows.Int32Rect(bmp.PixelWidth / 2, bmp.PixelHeight / 2, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    [Fact]
    public async Task SilentRecordingsJoinInOrderWithTheSumOfTheLengths()
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_日 付 '初日' #1", 3, "red", audio: false);
        var b = await MakeRecordingAsync(root, "20261020_1000_追加録画", 2, "blue", audio: false);
        var sizeA = new FileInfo(a).Length;

        var result = await Service().JoinAsync(Request(root, "つなぎ 手順", a, b));

        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, result.VideoPath);
        Assert.InRange(meta.Duration, 4.8, 5.2);
        Assert.Equal(320, meta.Width);
        Assert.Equal(240, meta.Height);
        Assert.False(meta.HasAudio);
        Assert.Equal(30, meta.Fps, 1);
        Assert.Equal(Path.Combine(root, "20261020_1100_つなぎ 手順"), result.Folder);
        // 順序（前半は赤、後半は青）。
        var first = await CenterColorAsync(result.VideoPath, 1.5);
        var second = await CenterColorAsync(result.VideoPath, 4.0);
        Assert.True(first.R > 150 && first.B < 100, $"前半は赤のはず: {first}");
        Assert.True(second.B > 150 && second.R < 100, $"後半は青のはず: {second}");
        // 元は変更しない・作業フォルダーは残らない。
        Assert.Equal(sizeA, new FileInfo(a).Length);
        Assert.DoesNotContain(Directory.GetDirectories(root), d => Path.GetFileName(d).StartsWith(RecordingJoinService.WorkFolderPrefix));
        Assert.Equal(2, JoinSourcesFile.TryReadFor(result.VideoPath)!.Sources.Count);
    }

    [Fact]
    public async Task RecordingsWithAudioJoinAndThirdDayAddsAfterTheJoinedVideo()
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_初日", 3, "red", hz: 440);
        var b = await MakeRecordingAsync(root, "20261020_1000_二日目", 2, "green", hz: 660);
        var c = await MakeRecordingAsync(root, "20261021_1000_三日目", 2.5, "blue", hz: 880);
        var svc = Service();

        var first = await svc.JoinAsync(Request(root, "二日分", a, b));
        var firstMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, first.VideoPath);
        Assert.True(firstMeta.HasAudio);
        Assert.InRange(firstMeta.Duration, 4.8, 5.3);

        // 3 日目: 結合した動画の後ろへ追加（結合結果を再度入力にする）。
        var second = await svc.JoinAsync(new JoinRequest(Ffmpeg, Ffprobe, root,
            [new JoinInput(first.VideoPath, "二日分"), new JoinInput(c, "三日目")], "三日分", new DateTime(2026, 10, 21, 12, 0, 0)));
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, second.VideoPath);
        Assert.InRange(meta.Duration, 7.3, 7.9);
        Assert.True(meta.HasAudio);

        // 順序と長さ: 赤 → 緑 → 青。
        var c1 = await CenterColorAsync(second.VideoPath, 1.5);
        var c2 = await CenterColorAsync(second.VideoPath, 4.0);
        var c3 = await CenterColorAsync(second.VideoPath, 6.5);
        Assert.True(c1.R > 150 && c1.G < 100, $"{c1}");
        Assert.True(c2.G > 100 && c2.R < 100 && c2.B < 100, $"{c2}");
        Assert.True(c3.B > 150 && c3.R < 100, $"{c3}");

        // 元素材の順序を引き継ぐ。
        var sources = JoinSourcesFile.TryReadFor(second.VideoPath)!;
        Assert.Equal(["20261019_1000_初日", "20261020_1000_二日目", "三日目"], sources.Sources.Select(s => s.DisplayName));
        Assert.InRange(sources.Sources.Sum(s => s.DurationSeconds), 7.3, 7.9);

        // 映像と音声の長さの差（継ぎ目ごとの音声の端数）が目立たない。
        var (video, audio) = await StreamDurationsAsync(second.VideoPath);
        output.WriteLine($"3 本連結: 映像 {video:0.000} 秒 / 音声 {audio:0.000} 秒 / 全体 {meta.Duration:0.000} 秒（入力の合計 7.500）");
        Assert.InRange(Math.Abs(video - audio), 0, 0.35);

        // 前回の結合結果・元の録画は変更されない。
        Assert.True(File.Exists(first.VideoPath));
        Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(c));
    }

    [Fact]
    public async Task JoinedVideoCanBeCutAndExported()
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_初日", 3, "red");
        var b = await MakeRecordingAsync(root, "20261020_1000_二日目", 3, "blue");
        var joined = await Service().JoinAsync(Request(root, "書き出し用", a, b));

        var output = Path.Combine(joined.Folder, "完成 動画.mp4");
        var cuts = new List<CutSegment> { new() { Start = 2, End = 4, Action = SegmentAction.Remove } };
        var result = await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion,
            joined.VideoPath, joined.Metadata, cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }), null, default);
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.InRange(meta.Duration, result.OutputDuration - 0.2, result.OutputDuration + 0.2);
        Assert.InRange(meta.Duration, 3.7, 4.3);
        Assert.True(meta.HasAudio);
        // カット後も、つなぎ目の前後（赤 → 青）が出る。
        Assert.True((await CenterColorAsync(output, 0.5)).R > 150);
        Assert.True((await CenterColorAsync(output, meta.Duration - 0.5)).B > 150);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("fps")]
    [InlineData("audio")]
    public async Task MismatchedRecordingsAreRefusedBeforeConcatenatingAndNothingIsCreated(string what)
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_初日", 2, "red");
        var b = await MakeRecordingAsync(root, "20261020_1000_違う", 2, "blue",
            fps: what == "fps" ? 15 : 30, audio: what != "audio", size: what == "size" ? "640x360" : "320x240");
        var before = Directory.GetDirectories(root).Order().ToArray();

        var ex = await Assert.ThrowsAsync<JoinException>(() => Service().JoinAsync(Request(root, "失敗", a, b)));
        Assert.NotEmpty(ex.Problems);
        Assert.All(ex.Problems, p => Assert.StartsWith(Path.GetFileName(b) + ":", p));
        Assert.Equal(before, Directory.GetDirectories(root).Order().ToArray());
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public async Task ThisAppsRecordingIsAppendableWithItsOwnConditions()
    {
        var root = NewRoot();
        var withAudio = await MakeRecordingAsync(root, "20261019_1000_音声あり", 2, "red", fps: 15, size: "480x270");
        var req = JoinCompatibility.CheckAppendable(await JoinProbe.ProbeAsync(fx.Runner, Ffprobe, withAudio), out var problems);
        Assert.Empty(problems);
        Assert.Equal(new AppendRequirement(480, 270, 15, true, req!.Description), req);

        var silent = await MakeRecordingAsync(root, "20261019_1100_音声なし", 2, "red", audio: false);
        var req2 = JoinCompatibility.CheckAppendable(await JoinProbe.ProbeAsync(fx.Runner, Ffprobe, silent), out _);
        Assert.False(req2!.HasAudio);

        // 本アプリの録画ではない動画（HEVC など）は、追加録画の対象外。
        var other = Path.Combine(fx.Dir, "別の動画 " + Guid.NewGuid().ToString("N")[..4] + ".mp4");
        var made = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=1", "-c:v", "libx264", "-pix_fmt", "yuv444p", other]));
        Assert.True(made.Succeeded, made.ErrorTail());
        Assert.Null(JoinCompatibility.CheckAppendable(await JoinProbe.ProbeAsync(fx.Runner, Ffprobe, other), out var otherProblems));
        Assert.NotEmpty(otherProblems);
    }

    [Fact]
    public async Task CorruptInputFailsWithoutLeavingFiles()
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_初日", 2, "red");
        var dir = Path.Combine(root, "20261020_1000_壊れた");
        Directory.CreateDirectory(dir);
        var broken = Path.Combine(dir, "rec.mp4");
        await File.WriteAllBytesAsync(broken, new byte[4096]);
        var before = Directory.GetDirectories(root).Order().ToArray();
        await Assert.ThrowsAsync<JoinException>(() => Service().JoinAsync(Request(root, "壊れ", a, broken)));
        Assert.Equal(before, Directory.GetDirectories(root).Order().ToArray());
    }

    [Fact]
    public async Task CanceledJoinLeavesNoWorkFolderOrProject()
    {
        var root = NewRoot();
        var a = await MakeRecordingAsync(root, "20261019_1000_初日", 2, "red");
        var b = await MakeRecordingAsync(root, "20261020_1000_二日目", 2, "blue");
        var before = Directory.GetDirectories(root).Order().ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().JoinAsync(Request(root, "取消", a, b), cts.Token));
        Assert.Equal(before, Directory.GetDirectories(root).Order().ToArray());
    }
}
