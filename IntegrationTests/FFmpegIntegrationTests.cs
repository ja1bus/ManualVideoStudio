using System.Diagnostics;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 実際の ffmpeg / ffprobe（設定または PATH）を使う統合試験。Unit Test とは別に明示実行する。
/// テスト動画は lavfi で生成する: 0-3 秒 動く映像+音、3-9 秒 静止画面+無音、9-12 秒 動く映像+音。
/// パスには空白・日本語・記号を含める。
/// </summary>
public sealed class MediaFixture : IAsyncLifetime
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mvs 統合試験 & (" + Guid.NewGuid().ToString("N")[..6] + ")");
    public string VideoPath => Path.Combine(Dir, "操作 動画 #1.mp4");
    public FFmpegLocation Location { get; private set; } = null!;
    public FFmpegCapabilities Capabilities { get; private set; } = null!;
    public VideoMetadata Metadata { get; private set; } = null!;
    public ProcessRunner Runner { get; } = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Dir);
        Location = new FFmpegLocator().Locate(Environment.GetEnvironmentVariable("MVS_FFMPEG"));
        if (!Location.IsAvailable)
            throw new InvalidOperationException("統合試験には ffmpeg.exe / ffprobe.exe が必要です（PATH または環境変数 MVS_FFMPEG）。");
        Capabilities = await new FFmpegInfoService(Runner).DetectAsync(Location);

        string[] args =
        [
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3",
            "-f", "lavfi", "-i", "color=c=navy:s=640x360:r=30:d=6",
            "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3",
            "-f", "lavfi", "-i", "aevalsrc=0.5*sin(440*2*PI*t):s=48000:d=3",
            "-f", "lavfi", "-i", "aevalsrc=0:s=48000:d=6",
            "-f", "lavfi", "-i", "aevalsrc=0.5*sin(660*2*PI*t):s=48000:d=3",
            "-filter_complex", "[0:v][3:a][1:v][4:a][2:v][5:a]concat=n=3:v=1:a=1[vc][a]",
            "-map", "[vc]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", VideoPath,
        ];
        var r = await Runner.RunAsync(new ProcessRequest(Location.Ffmpeg.Path!, args));
        if (!r.Succeeded) throw new InvalidOperationException("テスト動画を生成できません: " + r.ErrorTail());
        Metadata = await new MediaMetadataService(Runner).ProbeAsync(Location.Ffprobe.Path!, VideoPath);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

public sealed class FFmpegIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;

    [Fact]
    public void CapabilitiesDetected()
    {
        Assert.False(string.IsNullOrEmpty(fx.Capabilities.FfmpegVersion));
        Assert.True(fx.Capabilities.HasFreezeDetect);
        Assert.True(fx.Capabilities.HasSilenceDetect);
        Assert.True(fx.Capabilities.HasLibx264);
        Assert.Empty(fx.Capabilities.MissingFeatures());
    }

    [Fact]
    public void ProbeMetadata()
    {
        var m = fx.Metadata;
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(640, m.Width);
        Assert.Equal(360, m.Height);
        Assert.Equal(30, m.Fps, 1);
        Assert.InRange(m.Duration, 11.9, 12.2);
        Assert.Equal("yuv420p", m.PixelFormat);
        Assert.True(m.FileSize > 0);
    }

    [Fact]
    public async Task ExtractPngAndJpegWithJapaneseName()
    {
        var svc = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache"));
        var png = Path.Combine(fx.Dir, "out", "001_設定: を開く？.png".Replace(":", "：").Replace("？", "？"));
        await svc.ExtractAsync(Ffmpeg, fx.VideoPath, 1.5, png, ImageFormat.Png, 2, 0, default);
        Assert.True(File.Exists(png));
        var header = File.ReadAllBytes(png)[..8];
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(png)!, "*.partial-*"));

        var jpg = Path.Combine(fx.Dir, "out", "002_x.jpg");
        await svc.ExtractAsync(Ffmpeg, fx.VideoPath, 5, jpg, ImageFormat.Jpeg, 2, 0, default);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(jpg)[..2]);
    }

    [Fact]
    public async Task ThumbnailIsCached()
    {
        var svc = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache2"));
        var a = await svc.GetThumbnailAsync(Ffmpeg, fx.VideoPath, 2.0, 160, default);
        var t = File.GetLastWriteTimeUtc(a);
        var b = await svc.GetThumbnailAsync(Ffmpeg, fx.VideoPath, 2.0, 160, default);
        Assert.Equal(a, b);
        Assert.Equal(t, File.GetLastWriteTimeUtc(b));
        // キャッシュは拡張子なし（.jpg / .png は置いた瞬間に暗号化される環境があるため。v1.4.2）。中身は JPEG / PNG。
        Assert.Equal("", Path.GetExtension(a));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(a)[..2]);
        var full = await svc.GetFullFrameAsync(Ffmpeg, fx.VideoPath, 2.0, default);
        Assert.Equal("", Path.GetExtension(full));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(full)[..4]);
    }

    [Fact]
    public async Task ImageExportWithMetadata()
    {
        var frames = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache3"));
        var points = new List<CapturePoint>
        {
            new() { Id = 1, Sequence = 1, Timestamp = 1, SelectedFrameTime = 1.2, Caption = "Windowsの設定画面を開く" },
            new() { Id = 2, Sequence = 2, Timestamp = 10, SelectedFrameTime = 10, Caption = "Wi-Fi設定を開く" },
        };
        var dir = Path.Combine(fx.Dir, "images");
        var plan = ImageExportService.Plan(points, dir, ImageFormat.Png);
        var result = await new ImageExportService(frames).ExportAsync(Ffmpeg, fx.VideoPath, plan, overwrite: true, 2, writeMetadata: true, null, null, default);
        Assert.Equal(2, result.Written);
        Assert.True(File.Exists(Path.Combine(dir, "001_Windowsの設定画面を開く.png")));
        Assert.True(File.Exists(Path.Combine(dir, "002_Wi-Fi設定を開く.png")));
        Assert.True(File.Exists(result.MetadataPath));

        // 上書きしない指定では既存をスキップ
        var plan2 = ImageExportService.Plan(points, dir, ImageFormat.Png);
        var r2 = await new ImageExportService(frames).ExportAsync(Ffmpeg, fx.VideoPath, plan2, overwrite: false, 2, false, null, null, default);
        Assert.Equal(2, r2.Skipped);
    }

    /// <summary>注釈付きの画像: 実 ffmpeg のフレームにモザイク・四角形を焼き込み、同じ名前・形式で出力する。</summary>
    [Theory]
    [InlineData(ImageFormat.Png, "png")]
    [InlineData(ImageFormat.Jpeg, "jpg")]
    public async Task ImageExportBakesAnnotations(ImageFormat format, string ext)
    {
        var frames = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache-ann-" + ext));
        var plain = new CapturePoint { Id = 1, Sequence = 1, Timestamp = 1, SelectedFrameTime = 1, Caption = "注釈なし" };
        var annotated = new CapturePoint
        {
            Id = 2, Sequence = 2, Timestamp = 1, SelectedFrameTime = 1, Caption = "注釈あり",
            AnnotationImageWidth = 640, AnnotationImageHeight = 360,
            Annotations =
            [
                new MosaicAnnotation { X = 100, Y = 100, Width = 160, Height = 64, BlockSize = 16 },
                new RectangleAnnotation { X = 400, Y = 50, Width = 200, Height = 100, Color = AnnotationColor.Red, StrokeWidth = 6 },
            ],
        };
        var dir = Path.Combine(fx.Dir, "annotated " + ext);
        var plan = ImageExportService.Plan([plain, annotated], dir, format);
        var result = await new ImageExportService(frames, new AnnotatedImageRenderer())
            .ExportAsync(Ffmpeg, fx.VideoPath, plan, overwrite: true, 2, writeMetadata: false, null, null, default);
        Assert.Equal(2, result.Written);
        var plainPath = Path.Combine(dir, $"001_注釈なし.{ext}");
        var annPath = Path.Combine(dir, $"002_注釈あり.{ext}");
        Assert.Equal([Path.GetFileName(plainPath), Path.GetFileName(annPath)], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order().ToArray());
        var header = File.ReadAllBytes(annPath)[..2];
        Assert.Equal(format == ImageFormat.Png ? new byte[] { 0x89, 0x50 } : new byte[] { 0xFF, 0xD8 }, header);

        await StaThread.RunAsync(() =>
        {
            var a = Pixels(ImageFiles.Load(annPath));
            var p = Pixels(ImageFiles.Load(plainPath));
            Assert.Equal((640, 360), (a.W, a.H));
            // テスト画像（testsrc2）の模様がモザイク範囲ではマス単位の 1 色になる（JPEG は圧縮誤差を許容）
            int tol = format == ImageFormat.Png ? 0 : 24;
            for (int by = 100; by < 164; by += 16)
                for (int bx = 100; bx < 260; bx += 16)
                {
                    var c = a.At(bx + 8, by + 8);
                    for (int y = by + 3; y < by + 13; y++)
                        for (int x = bx + 3; x < bx + 13; x++)
                            Assert.True(Near(a.At(x, y), c, tol), $"({x},{y}) {a.At(x, y)} vs {c}");
                }
            Assert.True(Differs(p, 100, 100, 160, 64), "元のフレームのモザイク範囲に模様がない（試験条件の不備）");
            // 赤枠
            var edge = a.At(500, 50);
            Assert.True(edge.R > 180 && edge.G < 90 && edge.B < 90, $"edge {edge}");
            // 範囲外は注釈なしの画像と同じ
            Assert.True(Near(a.At(20, 300), p.At(20, 300), tol));
        });
    }

    private sealed record Bgra(byte[] Data, int W, int H)
    {
        public (int R, int G, int B) At(int x, int y) { int i = (y * W + x) * 4; return (Data[i + 2], Data[i + 1], Data[i]); }
    }

    private static Bgra Pixels(System.Windows.Media.Imaging.BitmapSource img)
    {
        var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var data = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(data, c.PixelWidth * 4, 0);
        return new Bgra(data, c.PixelWidth, c.PixelHeight);
    }

    private static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tol)
        => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    private static bool Differs(Bgra img, int x0, int y0, int w, int h)
    {
        var first = img.At(x0, y0);
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (!Near(img.At(x, y), first, 40)) return true;
        return false;
    }

    [Fact]
    public async Task ExportWithRemoveAndShorten()
    {
        var output = Path.Combine(fx.Dir, "出力 動画 (編集).mp4");
        var cuts = new List<CutSegment>
        {
            new() { Start = 3.5, End = 8.5, Action = SegmentAction.Shorten, KeepSeconds = 1 },
            new() { Start = 10, End = 11, Action = SegmentAction.Remove },
        };
        double last = 0;
        var progress = new Progress<VideoExportProgress>(p => last = p.Ratio);
        var result = await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion,
            fx.VideoPath, fx.Metadata, cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }), progress, default);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(fx.Dir, "出力 動画 (編集).partial.mp4")));
        var expected = fx.Metadata.Duration - 4 - 1;
        Assert.Equal(expected, result.OutputDuration, 2);
        var outMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, output);
        Assert.InRange(outMeta.Duration, expected - 0.15, expected + 0.15);
        Assert.Equal(640, outMeta.Width);
        Assert.True(outMeta.HasAudio);
    }

    [Fact]
    public async Task ExportWithManyCutsUsesExtensionlessFilterScript()
    {
        // カットが多くフィルタが長いと、フィルタを拡張子なしの一時ファイルに書いて渡す（v1.4.1。.txt は暗号化される環境があるため）。
        var input = Path.Combine(fx.Dir, "長い動画.mp4");
        var made = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=60", "-f", "lavfi", "-i", "sine=f=440:d=60",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", input]));
        Assert.True(made.Succeeded, made.ErrorTail());
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, input);
        var cuts = Enumerable.Range(0, 300).Select(i => new CutSegment { Start = i * 0.2 + 0.1, End = i * 0.2 + 0.15, Action = SegmentAction.Remove }).ToList();
        var keep = EditTimelineCalculator.ComputeKeepRanges(meta.Duration, cuts);
        var command = FFmpegArguments.Export(input, keep, meta, "o.mp4", new VideoEncodeOptions(), fx.Capabilities.MajorVersion);
        Assert.NotNull(command.FilterScript);
        Assert.Equal("", Path.GetExtension(FFmpegArguments.NewFilterScriptPath()));

        var output = Path.Combine(fx.Dir, "多数のカット.mp4");
        var result = await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion,
            input, meta, cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }), null, default);
        var outMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, output);
        Assert.InRange(outMeta.Duration, result.OutputDuration - 0.2, result.OutputDuration + 0.2);

        var gif = Path.Combine(fx.Dir, "多数のカット.gif");
        await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, input, meta,
            cuts, [], new SubtitleStyle(), GifQuality.Light, gif), null, CancellationToken.None);
        Assert.True(File.Exists(gif));
    }

    [Fact]
    public async Task ExportOddSizeVideo()
    {
        var odd = Path.Combine(fx.Dir, "odd.mp4");
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=s=641x361:r=30:d=2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv444p", odd]));
        Assert.True(r.Succeeded, r.ErrorTail());
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, odd);
        var output = Path.Combine(fx.Dir, "odd_edited.mp4");
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, odd, meta,
            [new CutSegment { Start = 0.5, End = 1 }], output, new VideoEncodeOptions { Preset = "ultrafast" }), null, default);
        var outMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, output);
        Assert.Equal(640, outMeta.Width);
        Assert.Equal(360, outMeta.Height);
    }

    [Fact]
    public async Task ExportCancelRemovesPartialAndProcess()
    {
        var output = Path.Combine(fx.Dir, "cancel.mp4");
        // 長めの重い処理にするため、ループ入力の代わりに同じ区間を多数残す編集を使う。
        var cuts = Enumerable.Range(0, 11).Select(i => new CutSegment { Start = i + 0.4, End = i + 0.6 }).ToList();
        int before = Process.GetProcessesByName("ffmpeg").Length;
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var progress = new Progress<VideoExportProgress>(_ => started.TrySetResult());
        var task = new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion,
            fx.VideoPath, fx.Metadata, cuts, output, new VideoEncodeOptions { Preset = "placebo" }), progress, cts.Token);
        await Task.WhenAny(started.Task, Task.Delay(5000));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(fx.Dir, "cancel.partial.mp4")));
        await Task.Delay(500);
        Assert.True(Process.GetProcessesByName("ffmpeg").Length <= before);
    }

    [Fact]
    public async Task LocalAnalysisFindsFreezeAndSilence()
    {
        var settings = AnalysisSettings.FromPreset(AnalysisPreset.Standard);
        var result = await new LocalAnalysisService(fx.Runner).AnalyzeAsync(Ffmpeg, fx.VideoPath, fx.Metadata, settings, null, default);
        var freeze = Assert.Single(result.Freezes);
        Assert.InRange(freeze.Start, 2.9, 3.3);
        Assert.InRange(freeze.End, 8.8, 9.2);
        Assert.Contains(result.Silences, s => s.Start < 3.3 && s.End > 8.7);
        var c = Assert.Single(result.Candidates);
        Assert.Equal(CandidateKind.FreezeAndSilence, c.Kind);
        Assert.Contains("静止＋無音", c.Reason);
        Assert.Contains(result.SceneChanges, t => Math.Abs(t - 3) < 0.2);
        Assert.Contains(result.SceneChanges, t => Math.Abs(t - 9) < 0.2);
    }

    [Fact]
    public async Task ProcessRunnerDrainsLargeOutputWithoutDeadlock()
    {
        // -filters 等では出力が小さいため、stderr に大量の info ログを出す処理で確認する。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            ["-hide_banner", "-nostdin", "-i", fx.VideoPath, "-vf", "showinfo", "-f", "null", "-"]), cts.Token);
        Assert.True(r.Succeeded);
        Assert.Contains("showinfo", r.StandardError);
    }
}
