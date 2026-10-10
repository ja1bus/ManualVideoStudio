using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.Tests;

/// <summary>注釈の描画・焼き込み・上書き保存（WPF の描画を STA スレッドで実行する）。</summary>
public sealed class ImagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-imaging-" + Guid.NewGuid().ToString("N"));

    public ImagingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Task Sta(Action action) => StaThread.RunAsync(action);

    /// <summary>白地に 1px 市松模様の帯（文字の細部に相当）を置いた画像。</summary>
    private static BitmapSource CreateBase(int w, int h, bool checker = true)
    {
        var stride = w * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                byte v = checker && y >= 40 && y < 80 && (x + y) % 2 == 0 ? (byte)0 : (byte)255;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    private static (byte R, byte G, byte B) Pixel(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    [Fact]
    public Task RectangleIsDrawnAsOutlineWithColor() => Sta(() =>
    {
        var result = AnnotationRenderer.Render(CreateBase(200, 120, checker: false),
            [new RectangleAnnotation { X = 20, Y = 20, Width = 100, Height = 60, Color = AnnotationColor.Red, StrokeWidth = 4 }], 200, 120);
        Assert.Equal((200, 120), (result.PixelWidth, result.PixelHeight));
        Assert.Equal(PixelFormats.Bgr24, result.Format);
        var edge = Pixel(result, 70, 20);
        Assert.True(edge.R > 200 && edge.G < 60 && edge.B < 60, $"edge {edge}");
        Assert.Equal((255, 255, 255), Pixel(result, 70, 50));    // 塗りつぶしなし
        Assert.Equal((255, 255, 255), Pixel(result, 5, 5));      // 範囲外は元のまま
    });

    [Fact]
    public Task ArrowAndTextUseSelectedColors() => Sta(() =>
    {
        var result = AnnotationRenderer.Render(CreateBase(300, 200, checker: false),
        [
            new ArrowAnnotation { X1 = 20, Y1 = 150, X2 = 200, Y2 = 150, Color = AnnotationColor.Blue, StrokeWidth = 6 },
            new TextAnnotation { X = 10, Y = 10, Text = "設定", FontSize = 24, Color = AnnotationColor.Black },
        ], 300, 200);
        var shaft = Pixel(result, 100, 150);
        Assert.True(shaft.B > 150 && shaft.R < 60, $"shaft {shaft}");
        var tip = Pixel(result, 195, 150);                       // 矢じり
        Assert.True(tip.B > 150 && tip.R < 60, $"tip {tip}");
        bool dark = false;
        for (int y = 10; y < 45 && !dark; y++)
            for (int x = 10; x < 70 && !dark; x++)
                if (Pixel(result, x, y) is { R: < 60, G: < 60, B: < 60 }) dark = true;
        Assert.True(dark, "文字が描かれていない");
    });

    [Fact]
    public Task MosaicIsBakedIntoPixels() => Sta(() =>
    {
        var baseImage = CreateBase(160, 120);
        var result = AnnotationRenderer.Render(baseImage, [new MosaicAnnotation { X = 0, Y = 40, Width = 160, Height = 40, BlockSize = 16 }], 160, 120);
        // 市松模様の帯（1px 単位で白黒）が 16px マスの単色になり、元の模様は残らない
        for (int by = 40; by < 80; by += 16)
            for (int bx = 0; bx < 160; bx += 16)
            {
                var first = Pixel(result, bx, by);
                Assert.Equal(first, Pixel(result, bx + 1, by));
                Assert.Equal(first, Pixel(result, bx, Math.Min(by + 1, 79)));
                Assert.NotEqual((byte)0, first.R);
                Assert.NotEqual((byte)255, first.R);
            }
        Assert.Equal((255, 255, 255), Pixel(result, 10, 10));    // 範囲外は変わらない
    });

    [Fact]
    public Task AnnotationsScaleWhenImageSizeDiffers() => Sta(() =>
    {
        // 基準 100x60 で (10,10)-(60,40) の四角形 → 実画像 200x120 では (20,20)-(120,80)
        var result = AnnotationRenderer.Render(CreateBase(200, 120, checker: false),
            [new RectangleAnnotation { X = 10, Y = 10, Width = 50, Height = 30, Color = AnnotationColor.Red, StrokeWidth = 2 }], 100, 60);
        Assert.True(Pixel(result, 70, 20).R > 200 && Pixel(result, 70, 20).G < 80);
        Assert.Equal((255, 255, 255), Pixel(result, 70, 10));
    });

    [Fact]
    public Task TextIsDrawnWithoutOutline() => Sta(() =>
    {
        // 濃い背景（青）に赤い文字: 縁取りをしないので、文字の周りに白い画素が出ない
        var stride = 200 * 4;
        var px = new byte[stride * 80];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255; }
        var blue = BitmapSource.Create(200, 80, 96, 96, PixelFormats.Bgra32, null, px, stride);
        foreach (var bold in new[] { false, true })
        {
            var result = AnnotationRenderer.Render(blue, [new TextAnnotation { X = 10, Y = 10, Text = "設定を開く", FontSize = 24, Color = AnnotationColor.Red, Bold = bold }], 200, 80);
            bool red = false;
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 200; x++)
                {
                    var p = Pixel(result, x, y);
                    Assert.False(p.R > 200 && p.G > 200 && p.B > 200, $"白い画素 ({x},{y}) bold={bold}");
                    if (p.R > 200 && p.B < 60) red = true;
                }
            Assert.True(red, "文字が描かれていない");
        }
    });

    [Fact]
    public Task BoldIsWiderThanNormal() => Sta(() =>
    {
        var normal = AnnotationDrawing.MeasureText(new TextAnnotation { Text = "Windowsの設定を開く", FontSize = 20 }, 1);
        var bold = AnnotationDrawing.MeasureText(new TextAnnotation { Text = "Windowsの設定を開く", FontSize = 20, Bold = true }, 1);
        Assert.True(bold.Width > normal.Width, $"{bold.Width} <= {normal.Width}");
    });

    [Fact]
    public Task MeasureTextGrowsWithFontSize() => Sta(() =>
    {
        var small = AnnotationDrawing.MeasureText(new TextAnnotation { Text = "手順", FontSize = 12 }, 1);
        var large = AnnotationDrawing.MeasureText(new TextAnnotation { Text = "手順", FontSize = 24 }, 1);
        var scaled = AnnotationDrawing.MeasureText(new TextAnnotation { Text = "手順", FontSize = 12 }, 2);
        Assert.True(large.Width > small.Width * 1.6 && large.Height > small.Height * 1.6);
        Assert.Equal(large.Width, scaled.Width, 3);
    });

    [Fact]
    public Task SaveAtomicOverwritesSameFileKeepingFormat() => Sta(() =>
    {
        var png = Path.Combine(_dir, "001_設定を開く.png");
        var jpg = Path.Combine(_dir, "002_確認.jpg");
        SavePng(CreateBase(64, 48, checker: false), png);
        File.WriteAllBytes(jpg, [0xFF, 0xD8, 0xFF]);

        var edited = AnnotationRenderer.Render(CreateBase(64, 48, checker: false), [new RectangleAnnotation { X = 5, Y = 5, Width = 30, Height = 20 }], 64, 48);
        ImageFiles.SaveAtomic(edited, png, ImageFormat.Png);
        ImageFiles.SaveAtomic(edited, jpg, ImageFormat.Jpeg, 90);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(png)[..4]);  // PNG のまま
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(jpg)[..2]);              // JPEG のまま
        Assert.True(Pixel(ImageFiles.Load(png), 20, 5).R > 200);                           // 注釈が焼き込まれた
        Assert.Equal((64, 48), (ImageFiles.Load(jpg).PixelWidth, ImageFiles.Load(jpg).PixelHeight));
        // 別名の画像・バックアップ・一時ファイルを残さない
        Assert.Equal(["001_設定を開く.png", "002_確認.jpg"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order().ToArray());
    });

    [Fact]
    public Task FailedSaveKeepsOriginalAndRemovesTemp() => Sta(() =>
    {
        var png = Path.Combine(_dir, "001_ロック中.png");
        SavePng(CreateBase(32, 32, checker: false), png);
        var before = File.ReadAllBytes(png);
        var edited = AnnotationRenderer.Render(CreateBase(32, 32, checker: false), [new RectangleAnnotation { X = 2, Y = 2, Width = 20, Height = 20 }], 32, 32);
        using (new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.None)) // 他のアプリが開いている状態
        {
            var ex = Assert.ThrowsAny<Exception>(() => ImageFiles.SaveAtomic(edited, png, ImageFormat.Png));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
        }
        Assert.Equal(before, File.ReadAllBytes(png));
        Assert.Single(Directory.GetFiles(_dir));
    });

    [Fact]
    public Task LoadDoesNotLockFile() => Sta(() =>
    {
        var png = Path.Combine(_dir, "001_a.png");
        SavePng(CreateBase(16, 16, checker: false), png);
        var img = ImageFiles.Load(png);
        File.Delete(png);
        Assert.Equal(16, img.PixelWidth);
    });

    [Fact]
    public async Task RendererWritesAnnotatedFileFromBaseImage()
    {
        var basePath = Path.Combine(_dir, "base.png");
        var output = Path.Combine(_dir, "001_出力.png");
        await Sta(() => SavePng(CreateBase(120, 100), basePath));
        await new AnnotatedImageRenderer().RenderToFileAsync(basePath,
            [new MosaicAnnotation { X = 0, Y = 40, Width = 120, Height = 40, BlockSize = 20 }], 120, 100, output, ImageFormat.Png, 2, default);
        await Sta(() =>
        {
            var img = ImageFiles.Load(output);
            var a = Pixel(img, 0, 40);
            for (int x = 0; x < 20; x++) Assert.Equal(a, Pixel(img, x, 45));
        });
        Assert.True(File.Exists(basePath));                  // 元画像は変えない
    }
}
