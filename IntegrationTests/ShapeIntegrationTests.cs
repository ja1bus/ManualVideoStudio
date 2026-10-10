using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 動画に重ねる図形（v1.6）を実 ffmpeg で書き出して確認する。テスト動画は 640x360・12 秒（0～3 秒 testsrc2 / 3～9 秒 紺 / 9～12 秒 testsrc2）。
/// 書き出した動画から 1 コマを切り出し、図形の色・モザイクのマス・表示時間を画素で調べる。
/// </summary>
public sealed class ShapeIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;

    private sealed record Bgra(byte[] Data, int W, int H)
    {
        public (int R, int G, int B) At(int x, int y) { int i = (y * W + x) * 4; return (Data[i + 2], Data[i + 1], Data[i]); }
    }

    private static readonly ShapeGroup Rectangle = new()
    {
        Start = 3.5, End = 8, ImageWidth = 640, ImageHeight = 360,
        Shapes =
        [
            new RectangleAnnotation { X = 100, Y = 100, Width = 200, Height = 100, Color = AnnotationColor.Red, StrokeWidth = 6 },
            new LineAnnotation { X1 = 360, Y1 = 300, X2 = 600, Y2 = 300, Color = AnnotationColor.Blue, StrokeWidth = 6 },
        ],
    };

    private static readonly ShapeGroup Mosaic = new()
    {
        Start = 0, End = 2, ImageWidth = 640, ImageHeight = 360,
        Shapes = [new MosaicAnnotation { X = 96, Y = 96, Width = 160, Height = 64, BlockSize = 16 }],
    };

    private static async Task<VideoShapeLayers> LayersAsync(params ShapeGroup[] groups)
    {
        VideoShapeLayers? layers = null;
        await StaThread.RunAsync(() => layers = ShapeOverlayRenderer.Build(groups, 640, 360));
        return layers!;
    }

    private async Task<Bgra> FrameAsync(string video, double time)
    {
        var png = Path.Combine(fx.Dir, $"frame-{Guid.NewGuid():N}");
        var svc = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache-shape"));
        await svc.ExtractAsync(Ffmpeg, video, time, png, ImageFormat.Png, 2, 0, default);
        using var stream = File.OpenRead(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return new Bgra(px, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static bool IsRed((int R, int G, int B) c) => c.R > 170 && c.G < 90 && c.B < 90;
    private static bool IsBlue((int R, int G, int B) c) => c.B > 150 && c.R < 80;

    /// <summary>範囲内の各マス（16px）の内側が 1 色か（圧縮の誤差は許容）。</summary>
    private static bool BlocksAreFlat(Bgra f, int x0, int y0, int w, int h, int block = 16, int tol = 24)
    {
        for (int by = y0; by + block <= y0 + h; by += block)
            for (int bx = x0; bx + block <= x0 + w; bx += block)
            {
                var c = f.At(bx + block / 2, by + block / 2);
                for (int y = by + 4; y < by + block - 4; y++)
                    for (int x = bx + 4; x < bx + block - 4; x++)
                    {
                        var p = f.At(x, y);
                        if (Math.Abs(p.R - c.R) > tol || Math.Abs(p.G - c.G) > tol || Math.Abs(p.B - c.B) > tol) return false;
                    }
            }
        return true;
    }

    private async Task<string> ExportMp4Async(string name, IReadOnlyList<CutSegment> cuts, VideoShapeLayers layers, IReadOnlyList<SubtitleSegment>? subs = null)
    {
        var output = Path.Combine(fx.Dir, name);
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }, subs, new SubtitleStyle(), Shapes: layers), null, CancellationToken.None);
        Assert.True(File.Exists(output));
        return output;
    }

    [Fact]
    public async Task Mp4WithoutCutsShowsShapesOnlyDuringTheirTime()
    {
        var output = await ExportMp4Async("図形 編集なし.mp4", [], await LayersAsync(Rectangle, Mosaic));

        var during = await FrameAsync(output, 5);
        Assert.True(IsRed(during.At(200, 100)), $"赤枠 {during.At(200, 100)}");
        Assert.True(IsBlue(during.At(480, 300)), $"青い直線 {during.At(480, 300)}");
        Assert.False(IsRed(during.At(200, 150)), "四角の中は塗らない");

        var after = await FrameAsync(output, 8.5);
        Assert.False(IsRed(after.At(200, 100)), $"表示時間の後 {after.At(200, 100)}");

        // モザイク（0～2 秒）: 1 秒ではマス単位の 1 色、2.5 秒（表示時間の後）では元の模様。
        Assert.True(BlocksAreFlat(await FrameAsync(output, 1), 96, 96, 160, 64), "モザイクがかかっていない");
        Assert.False(BlocksAreFlat(await FrameAsync(output, 2.5), 96, 96, 160, 64), "表示時間の後もモザイクが残っている");
    }

    [Fact]
    public async Task Mp4WithCutUsesOutputTimeAndKeepsSubtitlesOnTop()
    {
        // 3～9 秒（紺）を削除 → 出力 6 秒。図形は出力 3.5～5 秒（元動画 9.5～11 秒）に出す。
        var group = new ShapeGroup
        {
            Start = 3.5, End = 5, ImageWidth = 640, ImageHeight = 360,
            Shapes = [new RectangleAnnotation { X = 20, Y = 20, Width = 600, Height = 320, Color = AnnotationColor.Red, StrokeWidth = 6 }],
        };
        var subs = new[] { new SubtitleSegment { Start = 3.5, End = 5, Text = "字幕" } };
        var output = await ExportMp4Async("図形 カットあり.mp4", [new CutSegment { Start = 3, End = 9, Action = SegmentAction.Remove }],
            await LayersAsync(group), subs);
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, output);
        Assert.Equal(6, meta.Duration, 0.15);
        Assert.True(IsRed((await FrameAsync(output, 4)).At(320, 20)));
        Assert.False(IsRed((await FrameAsync(output, 2)).At(320, 20)));
        Assert.False(IsRed((await FrameAsync(output, 5.5)).At(320, 20)));
    }

    [Fact]
    public async Task GifIncludesShapes()
    {
        var group = new ShapeGroup
        {
            Start = 0, End = 12, ImageWidth = 640, ImageHeight = 360,
            Shapes = [new RectangleAnnotation { X = 100, Y = 100, Width = 200, Height = 100, Color = AnnotationColor.Red, StrokeWidth = 6 }],
        };
        var output = Path.Combine(fx.Dir, "図形.gif");
        var result = await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            [], [], new SubtitleStyle(), GifQuality.Standard, output, Shapes: await LayersAsync(group, Mosaic)), null, CancellationToken.None);
        Assert.True(result.FileSize > 0);
        await StaThread.RunAsync(() =>
        {
            using var stream = File.OpenRead(output);
            var first = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var bmp = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
            // 標準の GIF は幅 1280px まで縮小しないため、元の大きさのまま。
            Assert.Equal((640, 360), (bmp.PixelWidth, bmp.PixelHeight));
            var px = new byte[4];
            bmp.CopyPixels(new Int32Rect(200, 100, 1, 1), px, 4, 0);
            Assert.True(px[2] > 170 && px[1] < 90 && px[0] < 90, $"GIF の赤枠 R{px[2]} G{px[1]} B{px[0]}");
        });
    }
}
