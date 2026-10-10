using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// v1.16: 縦横比を固定した範囲の実録画（gdigrab）で、計算した大きさと録画した動画の大きさが一致すること、
/// 縦長（9:16）の動画でタイトル・字幕・カット・MP4 / GIF の書き出しが破綻しないことを実 ffmpeg で確かめる。
/// 録画の試験は実際のデスクトップを録るため、対話セッション（画面のある PC。ロック中は不可）で実行すること。
/// </summary>
public sealed class PortraitIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private RecordingService CreateRecorder() => new(new FFmpegRecordingProcessFactory(), fx.Runner, new MediaMetadataService(fx.Runner));

    [Theory]
    [InlineData(RegionOrientation.Portrait, 12)]   // 216x384
    [InlineData(RegionOrientation.Landscape, 12)]  // 384x216
    [InlineData(RegionOrientation.Portrait, 4)]    // 最小 72x128
    public async Task LockedRegionRecordsExactlyTheShownSize(RegionOrientation orientation, int k)
    {
        // 録画画面の「録画開始」と同じ確定処理（Normalize → 画面内 → 偶数化）を通す。
        var screen = new CaptureRegion(0, 0, 1280, 720);
        var (uw, uh) = RegionAspect.Unit(orientation);
        var shown = new CaptureRegion(16, 16, uw * k, uh * k);
        var region = RegionAspect.Normalize(shown, orientation, screen).ClampTo(screen).ToEvenSize();
        Assert.Equal(shown, region);

        var output = Path.Combine(fx.Dir, $"rec-{orientation}-{k}.mp4");
        var svc = CreateRecorder();
        await svc.StartAsync(new RecordingStartRequest(Ffmpeg, Ffprobe, new ScreenRecordingOptions(region, 30, false, null), output, new RecordingInfo()));
        await Task.Delay(1500);
        await svc.StopAsync();

        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.Equal((shown.Width, shown.Height), (meta.Width, meta.Height));
        if (orientation == RegionOrientation.Portrait) Assert.Equal(9 * meta.Height, 16 * meta.Width);
        else Assert.Equal(16 * meta.Height, 9 * meta.Width);
    }

    /// <summary>360x640・6 秒・音声あり（0～2 秒 testsrc2 + 440Hz / 2～6 秒 紺 + 無音）の縦長動画を作る。</summary>
    private async Task<(string Path, VideoMetadata Meta)> PortraitSourceAsync()
    {
        var path = Path.Combine(fx.Dir, "縦長 元動画.mp4");
        if (!File.Exists(path))
        {
            var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            [
                "-hide_banner", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=360x640:r=30:d=2",
                "-f", "lavfi", "-i", "color=c=navy:s=360x640:r=30:d=4",
                "-f", "lavfi", "-i", "aevalsrc=0.5*sin(440*2*PI*t):s=48000:d=2",
                "-f", "lavfi", "-i", "aevalsrc=0:s=48000:d=4",
                "-filter_complex", "[0:v][2:a][1:v][3:a]concat=n=2:v=1:a=1[v][a]",
                "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", path,
            ]));
            Assert.True(r.Succeeded, r.ErrorTail());
        }
        return (path, await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, path));
    }

    private async Task<(byte[] Px, int W, int H)> FrameAsync(string video, double time)
    {
        var png = Path.Combine(fx.Dir, $"frame-{Guid.NewGuid():N}");
        var svc = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache-portrait"));
        await svc.ExtractAsync(Ffmpeg, video, time, png, ImageFormat.Png, 2, 0, default);
        using var stream = File.OpenRead(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return (px, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static int White((byte[] Px, int W, int H) f, double y0, double y1)
    {
        int n = 0;
        for (int y = (int)(f.H * y0); y < (int)(f.H * y1); y++)
            for (int x = 0; x < f.W; x++)
            {
                int i = (y * f.W + x) * 4;
                if (f.Px[i] > 200 && f.Px[i + 1] > 200 && f.Px[i + 2] > 200) n++;
            }
        return n;
    }

    private async Task<VideoTitleImage> TitleImageAsync(VideoTitle title, int w, int h, double main)
    {
        VideoTitleImage? image = null;
        string? error = null;
        await StaThread.RunAsync(() => (image, error) = TitleRenderer.BuildExportImage(title, w, h, main));
        Assert.Null(error);
        return image!;
    }

    [Fact]
    public async Task PortraitMp4WithTitleSubtitleAndCutKeepsSizeAndTiming()
    {
        var (src, meta) = await PortraitSourceAsync();
        Assert.Equal((360, 640), (meta.Width, meta.Height));
        // 0～1 秒を削除（本編 5 秒）。本編 2～3.5 秒（元 3～4.5 秒 = 紺）に字幕。タイトル 2 秒（2 行・縦長の幅に折り返す）。
        CutSegment[] cuts = [new CutSegment { Start = 0, End = 1, Action = SegmentAction.Remove }];
        SubtitleSegment[] subs = [new SubtitleSegment { Start = 2.0, End = 3.5, Text = "縦長の字幕", FontSize = 48 }];
        var title = new VideoTitle { Text = "縦長動画の\n操作説明", Seconds = 2, FadeIn = true };
        var image = await TitleImageAsync(title, 360, 640, 5);
        var output = Path.Combine(fx.Dir, "縦長 出力.mp4");
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, src, meta, cuts, output,
            new VideoEncodeOptions { Preset = "ultrafast" }, subs, new SubtitleStyle(), 0, Title: image), null, CancellationToken.None);

        var outMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.Equal((360, 640), (outMeta.Width, outMeta.Height));
        Assert.InRange(outMeta.Duration, 6.8, 7.3); // 本編 5 + タイトル 2
        Assert.True(outMeta.HasAudio);

        var titleFrame = await FrameAsync(output, 1.0);
        Assert.True(White(titleFrame, 0.3, 0.6) > 100, "タイトルの文字（高さの 1/3 から）");
        Assert.Equal(0, White(titleFrame, 0, 0.3));
        Assert.Equal(0, White(titleFrame, 0.7, 1.0)); // タイトル区間に字幕は出ない
        // 字幕は本編 2～3.5 秒 → 完成動画 4～5.5 秒。紺の背景に白い文字。
        Assert.True(White(await FrameAsync(output, 4.8), 0.7, 1.0) > 50, "字幕");
        Assert.Equal(0, White(await FrameAsync(output, 6.5), 0.7, 1.0));
    }

    [Fact]
    public async Task PortraitGifKeepsAspectWithoutUpscaling()
    {
        var (src, meta) = await PortraitSourceAsync();
        var image = await TitleImageAsync(new VideoTitle { Text = "縦長 GIF", Seconds = 1 }, 360, 640, 6);
        var output = Path.Combine(fx.Dir, "縦長.gif");
        var result = await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, src, meta,
            [], [new SubtitleSegment { Start = 3, End = 4, Text = "字幕", FontSize = 48 }], new SubtitleStyle(), GifQuality.Light, output, Title: image),
            null, CancellationToken.None);
        var gif = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, result.OutputPath);
        // 軽量は 800x600 まで: 360x640 は高さ 600 に合わせて縮める（拡大しない・比率 9:16 のまま）。
        Assert.True(gif.Height <= 600 && gif.Width <= 360, $"{gif.Width}x{gif.Height}");
        Assert.InRange(gif.Width / (double)gif.Height, 9.0 / 16 - 0.01, 9.0 / 16 + 0.01);
        Assert.InRange(result.OutputDuration, 6.99, 7.01);
    }
}
