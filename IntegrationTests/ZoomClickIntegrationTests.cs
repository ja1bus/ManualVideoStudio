using System.Diagnostics;
using System.Globalization;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 拡大（zoompan）とクリックの強調（v1.21）を実 ffmpeg で書き出して確認する。
/// 拡大はテスト動画（640x360・12 秒・30fps。0～3 秒は testsrc2）で、クリックは灰色の長い動画を別に作って 300 件を重ねる。
/// コマは rawvideo（rgb24）で切り出して画素を比べる。
/// </summary>
public sealed class ZoomClickIntegrationTests(MediaFixture fx, Xunit.Abstractions.ITestOutputHelper log) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private sealed record Rgb(byte[] Data, int W, int H)
    {
        public (int R, int G, int B) At(int x, int y) { int i = (y * W + x) * 3; return (Data[i], Data[i + 1], Data[i + 2]); }

        /// <summary>全画素の平均の差（0～255）。</summary>
        public double MeanDiff(Rgb o) { long s = 0; for (int i = 0; i < Data.Length; i++) s += Math.Abs(Data[i] - o.Data[i]); return (double)s / Data.Length; }
    }

    private async Task<Rgb> FrameAsync(string video, double time, int w, int h)
    {
        var raw = Path.Combine(fx.Dir, $"raw-{Guid.NewGuid():N}");
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-ss", time.ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw]));
        Assert.True(r.Succeeded, r.ErrorTail());
        var data = await File.ReadAllBytesAsync(raw);
        Assert.Equal(w * h * 3, data.Length);
        return new Rgb(data, w, h);
    }

    private async Task<int> FrameCountAsync(string video)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", video]));
        Assert.True(r.Succeeded, r.ErrorTail());
        return int.Parse(r.StandardOutput.Trim(), CultureInfo.InvariantCulture);
    }

    private async Task<VideoExportResult> ExportAsync(string input, VideoMetadata meta, string output, IReadOnlyList<CutSegment> cuts, VideoShapeLayers? layers)
        => await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, input, meta,
            cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }, Shapes: layers), null, CancellationToken.None);

    [Fact]
    public void FFmpegHasZoomPan() => Assert.True(fx.Capabilities.HasZoomPan);

    [Fact]
    public async Task ZoomKeepsFrameCountAndOnlyChangesFramesInsideTheWindow()
    {
        // 出力 0.5～2.5 秒に ×2（左上 160,90。範囲 320x180）。
        var group = new ShapeGroup
        {
            Start = 0.5, End = 2.5, ImageWidth = 640, ImageHeight = 360,
            Shapes = [new ZoomAnnotation { X = 160, Y = 90, Width = 320, Height = 180 }],
        };
        var layers = VideoShapeLayers.Empty with { Zooms = VideoShapeLayers.ZoomsOf([group], 640, 360) };
        var zoomed = Path.Combine(fx.Dir, "拡大 あり.mp4");
        var plain = Path.Combine(fx.Dir, "拡大 なし.mp4");
        await ExportAsync(fx.VideoPath, fx.Metadata, zoomed, [], layers);
        await ExportAsync(fx.VideoPath, fx.Metadata, plain, [], null);

        Assert.Equal(await FrameCountAsync(plain), await FrameCountAsync(zoomed));
        var zm = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, zoomed);
        Assert.Equal(fx.Metadata.Duration, zm.Duration, 0.1);

        // 1.5 秒（拡大の最中）: 画面は元の (160 + x/2, 90 + y/2) を映す。
        var z = await FrameAsync(zoomed, 1.5, 640, 360);
        var p = await FrameAsync(plain, 1.5, 640, 360);
        var expected = new byte[p.Data.Length];
        for (int y = 0; y < 360; y++)
            for (int x = 0; x < 640; x++)
            {
                var c = p.At(160 + x / 2, 90 + y / 2);
                int i = (y * 640 + x) * 3;
                (expected[i], expected[i + 1], expected[i + 2]) = ((byte)c.R, (byte)c.G, (byte)c.B);
            }
        double diffPlain = z.MeanDiff(p), diffExpected = z.MeanDiff(new Rgb(expected, 640, 360));
        log.WriteLine($"1.5s: diff(plain)={diffPlain:0.0} diff(expected crop)={diffExpected:0.0}");
        Assert.True(diffPlain > 15, $"拡大されていない（差 {diffPlain:0.0}）");
        Assert.True(diffExpected < diffPlain / 2, $"拡大の範囲が違う（期待との差 {diffExpected:0.0}、元との差 {diffPlain:0.0}）");

        // 表示時間の外（0.2 秒・10.5 秒）は元と同じ。
        foreach (var t in new[] { 0.2, 10.5 })
        {
            var d = (await FrameAsync(zoomed, t, 640, 360)).MeanDiff(await FrameAsync(plain, t, 640, 360));
            log.WriteLine($"{t}s: diff={d:0.00}");
            Assert.True(d < 3, $"{t} 秒のコマが違う（差 {d:0.0}）");
        }
    }

    [Fact]
    public async Task ThreeHundredClicksExportWithinReasonableTime()
    {
        // 灰色 180 秒（640x360・30fps）。0.6 秒ごとに 300 クリック、輪の代わりに黄色の 40x40 の四角。
        var input = Path.Combine(fx.Dir, "クリック 300.mp4");
        var gen = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=30:d=180",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", input]));
        Assert.True(gen.Succeeded, gen.ErrorTail());
        var ringFile = Path.Combine(fx.Dir, "ring");
        var ring = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=yellow:s=40x40",
            "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2", ringFile]));
        Assert.True(ring.Succeeded, ring.ErrorTail());
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, input);

        var source = Enumerable.Range(0, 300).Select(i => (SourceTime: 0.3 + i * 0.6, X: 40.0 + (i % 14) * 40, Y: 60.0 + (i % 6) * 40)).ToList();
        var keep = EditTimelineCalculator.ComputeKeepRanges(meta.Duration, []);
        var clicks = VideoShapeLayers.ClicksOf(source, keep, 640, 360);
        Assert.Equal(300, clicks.Count);
        var layers = VideoShapeLayers.Empty with { Clicks = clicks, ClickRingPng = await File.ReadAllBytesAsync(ringFile), ClickRadius = 20 };

        var plan = new ShapeFilterPlan([], []) { Clicks = VideoShapeLayers.ClickWindowsOf(clicks), ClickRingFile = "click-ring", ClickRingWidth = 40, ClickRingHeight = 40 };
        var filterLength = FFmpegArguments.ComposeVideoChain(null, plan, 640, 360, null, meta.Fps)!.Length;

        var plainOut = Path.Combine(fx.Dir, "クリック なし.mp4");
        var sw = Stopwatch.StartNew();
        await ExportAsync(input, meta, plainOut, [], null);
        var plainTime = sw.Elapsed;
        var output = Path.Combine(fx.Dir, "クリック あり.mp4");
        sw.Restart();
        await ExportAsync(input, meta, output, [], layers);
        var clickTime = sw.Elapsed;
        log.WriteLine($"300 clicks: filter length={filterLength} chars, export={clickTime.TotalSeconds:0.0}s (without clicks {plainTime.TotalSeconds:0.0}s)");
        Assert.True(clickTime < TimeSpan.FromSeconds(Math.Max(60, plainTime.TotalSeconds * 10)), $"書き出しに時間がかかりすぎる（{clickTime.TotalSeconds:0.0} 秒）");

        Assert.Equal(await FrameCountAsync(plainOut), await FrameCountAsync(output));
        // 輪が出る時刻・位置: クリック 0（0.3 秒、40,60）・クリック 250（150.3 秒、x=40+(250%14)*40=520, y=60+(250%6)*40=220）。
        static bool IsYellow((int R, int G, int B) c) => c.R > 200 && c.G > 200 && c.B < 80;
        foreach (var (i, t) in new[] { (0, 0.4), (250, 150.4), (299, 179.8) })
        {
            var f = await FrameAsync(output, t, 640, 360);
            var (x, y) = ((int)source[i].X, (int)source[i].Y);
            Assert.True(IsYellow(f.At(x, y)), $"クリック {i} の輪が出ていない {f.At(x, y)}");
            Assert.False(IsYellow(f.At(x + 30, y + 30)), "輪の外まで塗られている");
            // 次のクリックまでの間（クリックの 0.5 秒後）は出ていない。
            if (i < 299) Assert.False(IsYellow((await FrameAsync(output, source[i].SourceTime + 0.5, 640, 360)).At(x, y)), $"クリック {i} の輪が残っている");
        }
    }
}
