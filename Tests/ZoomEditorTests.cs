using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>拡大の範囲の作成・変形の計算（v1.21。縦横比の固定・幅の制限・画像の中）。</summary>
public class ZoomGeometryTests
{
    private const int W = 320, H = 180;

    private static void AssertAspect(ZoomAnnotation z) => Assert.Equal(z.Width * H / W, z.Height, 6);

    [Fact]
    public void DragLocksTheAspectToTheImageAndUsesTheLargerSide()
    {
        var z = ZoomGeometry.FromDrag(new(10, 10), new(170, 20), W, H)!;
        Assert.Equal((10.0, 10.0, 160.0, 90.0), (z.X, z.Y, z.Width, z.Height));
        Assert.Equal(2.0, z.FactorFor(W), 6);

        // 縦に長くドラッグすると高さに合わせる（左上へのドラッグは終点側に広げる）。
        var up = ZoomGeometry.FromDrag(new(300, 170), new(290, 80), W, H)!;
        AssertAspect(up);
        Assert.Equal(90, up.Height, 6);
        Assert.Equal((300 - 160.0, 170 - 90.0), (up.X, up.Y));
    }

    [Fact]
    public void DragClampsTheWidthBetween25And90PercentAndStaysInside()
    {
        var small = ZoomGeometry.FromDrag(new(10, 10), new(30, 15), W, H)!;
        Assert.Equal(80, small.Width, 6); // 25% = ×4
        AssertAspect(small);

        var large = ZoomGeometry.FromDrag(new(0, 0), new(500, 500), W, H)!;
        Assert.Equal(288, large.Width, 6); // 90% ≒ ×1.1
        AssertAspect(large);

        // 右下の端の近くから描いても、最小の大きさを保ったまま画像の中へずらす。
        var edge = ZoomGeometry.FromDrag(new(310, 170), new(319, 179), W, H)!;
        Assert.Equal(80, edge.Width, 6);
        Assert.True(edge.X + edge.Width <= W + 1e-9 && edge.Y + edge.Height <= H + 1e-9);
        Assert.True(ShapeGroupValidator.ValidateShapes([edge], W, H).IsValid);

        Assert.Null(ZoomGeometry.FromDrag(new(10, 10), new(11, 12), W, H)); // ほとんど動かしていない
    }

    [Fact]
    public void ResizeKeepsTheAspectTheLimitsAndTheOppositeCorner()
    {
        var original = new ZoomAnnotation { X = 20, Y = 20, Width = 160, Height = 90 };
        var t = (ZoomAnnotation)original.Clone();
        AnnotationGeometry.DragHandle(original, t, AnnotationHandle.BottomRight, new(400, 400), W, H);
        AssertAspect(t);
        Assert.Equal(20, t.X, 6); // 左上は固定
        Assert.Equal(20, t.Y, 6);
        Assert.True(t.X + t.Width <= W + 1e-9 && t.Y + t.Height <= H + 1e-9);
        Assert.True(ShapeGroupValidator.ValidateShapes([t], W, H).IsValid);

        // 小さくしすぎても ×4 で止まる。対角（右下）は固定。
        AnnotationGeometry.DragHandle(original, t, AnnotationHandle.TopLeft, new(178, 108), W, H);
        Assert.Equal(80, t.Width, 6);
        AssertAspect(t);
        Assert.Equal(180, t.X + t.Width, 6);
        Assert.Equal(110, t.Y + t.Height, 6);
    }

    [Fact]
    public void ZoomIsHitOnlyNearItsBorderAndHasBoxBounds()
    {
        var z = new ZoomAnnotation { X = 20, Y = 20, Width = 160, Height = 90 };
        Assert.True(AnnotationGeometry.HitTest(z, new(20, 60), 2, 1, (_, _) => (0, 0)));
        Assert.False(AnnotationGeometry.HitTest(z, new(100, 60), 2, 1, (_, _) => (0, 0)));
        Assert.Equal(new ImageRect(20, 20, 160, 90), AnnotationGeometry.Bounds(z, 1, (_, _) => (0, 0)));
        Assert.Equal(2.0, ZoomGeometry.DisplayFactor(z, W));
    }
}

/// <summary>拡大は 1 つのまとまりに 1 つ（描き直すと置き換え、元に戻すは 1 回）。</summary>
public class ZoomSessionTests
{
    [Fact]
    public void AddingASecondZoomReplacesTheFirstInOneUndoStep()
    {
        var s = new AnnotationSession([], 320, 180);
        var first = new ZoomAnnotation { X = 0, Y = 0, Width = 160, Height = 90 };
        s.Add(first);
        s.Add(new RectangleAnnotation { X = 10, Y = 10, Width = 50, Height = 50 });
        var second = new ZoomAnnotation { X = 100, Y = 50, Width = 80, Height = 45 };
        s.Add(second);

        Assert.Equal(2, s.Items.Count);
        Assert.Single(s.Items.OfType<ZoomAnnotation>());
        Assert.IsType<ZoomAnnotation>(s.Items[0]); // いちばん奥に置く
        Assert.Equal(second.Id, s.Zoom!.Id);
        Assert.Equal("拡大の置き換え", s.History.UndoLabel);

        Assert.True(s.Undo());
        Assert.Equal(first.Id, s.Zoom!.Id);
        Assert.Equal(2, s.Items.Count);
        Assert.True(s.Undo());
        Assert.True(s.Undo());
        Assert.Null(s.Zoom);
        Assert.True(s.Redo());
        Assert.Equal("拡大の追加", s.History.UndoLabel);
    }

    [Fact]
    public void ZoomCanBeDeletedAndIgnoresColorAndStroke()
    {
        var s = new AnnotationSession([], 320, 180);
        s.Add(new ZoomAnnotation { X = 0, Y = 0, Width = 160, Height = 90 });
        var id = s.Zoom!.Id;
        Assert.False(s.SetColor(id, AnnotationColor.Blue));
        Assert.False(s.SetStrokeWidth(id, 8));
        Assert.True(s.Remove(id));
        Assert.Null(s.Zoom);
        Assert.Equal("拡大の削除", s.History.UndoLabel);
    }
}

/// <summary>図形の編集画面の［拡大］（動画モードだけ・OK での検証）。</summary>
public class ZoomEditorViewModelTests
{
    private sealed class Dialogs : IDialogService
    {
        public void ShowInfo(string message, string title = "情報") { }
        public void ShowWarning(string message, string title = "注意") { }
        public void ShowError(string message, string? detail = null, string title = "エラー") { }
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "") => null;
    }

    private static BitmapSource Frame(int w = 320, int h = 180)
    {
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        b.Freeze();
        return b;
    }

    private static (ImageEditorViewModel Vm, List<IReadOnlyList<Annotation>> Commits) OpenVideo(IReadOnlyList<Annotation>? shapes = null)
    {
        var commits = new List<IReadOnlyList<Annotation>>();
        var request = new VideoShapeEditRequest("動画の図形 - 追加", Frame(), shapes ?? [], 320, 180, 1, 4, 30, "背景",
            (s, _, _, _, _) => { commits.Add(s); return EditResult.Ok(); });
        return (new ImageEditorViewModel(request, new Dialogs(), null!, NullLogger.Instance), commits);
    }

    [Fact]
    public Task ZoomToolDrawsAndReplacesInVideoMode() => StaThread.RunAsync(() =>
    {
        var (vm, _) = OpenVideo();
        vm.SetToolCommand.Execute(EditorTool.Zoom);
        Assert.Equal(EditorTool.Zoom, vm.Tool);
        Assert.Contains("拡大", vm.StatusText);
        Assert.IsType<ZoomAnnotation>(vm.CreateShape(new(10, 10), new(170, 20)));

        vm.CommitShape(new(10, 10), new(170, 20));
        Assert.Equal("拡大 ×2.0", vm.ZoomLabel(vm.Session.Zoom!));
        Assert.Contains("×2.0", vm.StatusText);
        vm.CommitShape(new(0, 0), new(80, 45));
        Assert.Single(vm.Session.Items);
        Assert.Equal("×4.0", vm.ZoomFactorText(vm.Session.Zoom!));
        Assert.Contains("置き換え", vm.StatusText);
        Assert.Equal("選択中: 拡大", SelectThen(vm));
        Assert.Contains("Z 拡大", vm.KeyHelpText);
    });

    private static string SelectThen(ImageEditorViewModel vm)
    {
        vm.Session.Select(vm.Session.Zoom!.Id);
        return vm.SelectionText;
    }

    [Fact]
    public Task ZoomToolIsNotAvailableInImageMode() => StaThread.RunAsync(() =>
    {
        var point = new CapturePoint { Id = 1, Sequence = 1, Caption = "a", AnnotationImageWidth = 320, AnnotationImageHeight = 180 };
        var request = new ImageEditorRequest(point, Frame(), null, ImageFormat.Png, 90, (_, _, _) => EditResult.Ok());
        var vm = new ImageEditorViewModel(request, new Dialogs(), null!, NullLogger.Instance);
        vm.SetToolCommand.Execute(EditorTool.Zoom);
        Assert.NotEqual(EditorTool.Zoom, vm.Tool);
        Assert.DoesNotContain("Z 拡大", vm.KeyHelpText);
    });

    [Fact]
    public Task OkValidatesTheZoomAndKeepsTheWindowOpen() => StaThread.RunAsync(() =>
    {
        // 倍率 ×16（幅 20px）の拡大は［OK］で止める（エラーを赤字の欄に出し、反映しない）。
        var (vm, commits) = OpenVideo([new ZoomAnnotation { X = 0, Y = 0, Width = 20, Height = 11.25 }]);
        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.StartText = "00:00:01.500";
        vm.SaveCommand.Execute(null);
        Assert.Empty(commits);
        Assert.False(closed);
        Assert.Equal(ShapeGroupValidator.ZoomFactorMessage, vm.TimeError);

        // 描き直せば反映できる。
        vm.SetToolCommand.Execute(EditorTool.Zoom);
        vm.CommitShape(new(10, 10), new(170, 20));
        Assert.Equal("", vm.TimeError);
        vm.SaveCommand.Execute(null);
        Assert.True(closed);
        var z = Assert.IsType<ZoomAnnotation>(Assert.Single(Assert.Single(commits)));
        Assert.Equal(160, z.Width, 1);
    });
}

/// <summary>拡大は画像に描かない（動画に重ねる図形の画像・手順書画像の注釈）。</summary>
public class ZoomRenderingTests
{
    private static readonly ZoomAnnotation Zoom = new() { X = 0, Y = 0, Width = 50, Height = 50 };

    [Fact]
    public Task OverlayIgnoresTheZoomForDrawingAndBounds() => StaThread.RunAsync(() =>
    {
        var rect = new RectangleAnnotation { X = 60, Y = 60, Width = 20, Height = 20, Color = AnnotationColor.Red, StrokeWidth = 4 };
        var alone = ShapeOverlayRenderer.Render([rect], 100, 100, 100, 100)!.Value;
        var withZoom = ShapeOverlayRenderer.Render([Zoom, rect], 100, 100, 100, 100)!.Value;
        Assert.Equal((alone.X, alone.Y), (withZoom.X, withZoom.Y));
        Assert.Equal(alone.Png, withZoom.Png);
    });

    [Fact]
    public Task ZoomOnlyGroupHasNoOverlayImage() => StaThread.RunAsync(() =>
    {
        Assert.Null(ShapeOverlayRenderer.Render([Zoom], 100, 100, 100, 100));
        var layers = ShapeOverlayRenderer.Build(
            [new ShapeGroup { Start = 0, End = 1, ImageWidth = 100, ImageHeight = 100, Shapes = [Zoom] }], 100, 100);
        Assert.Empty(layers.Images);
        Assert.Empty(layers.Mosaics);
    });

    [Fact]
    public Task AnnotatedImageIgnoresTheZoom() => StaThread.RunAsync(() =>
    {
        var px = Enumerable.Repeat((byte)0x80, 100 * 100 * 4).ToArray();
        var b = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Bgra32, null, px, 400);
        byte[] Pixels(BitmapSource image)
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var outPx = new byte[100 * 100 * 4];
            bgra.CopyPixels(outPx, 400, 0);
            return outPx;
        }
        Assert.Equal(Pixels(AnnotationRenderer.Render(b, [], 100, 100)), Pixels(AnnotationRenderer.Render(b, [Zoom], 100, 100)));
    });
}

/// <summary>クリック位置の強調の輪の画像（v1.21）。</summary>
public class ClickRingRendererTests
{
    private static (BitmapSource Image, byte[] Pixels) Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var frame = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        bgra.CopyPixels(px, frame.PixelWidth * 4, 0);
        return (frame, px);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(28)]
    [InlineData(56 * 2160 / 1080.0)]
    [InlineData(13.3)]
    public Task RingIsASquareEvenTransparentImageWithTheRingCentred(double radius) => StaThread.RunAsync(() =>
    {
        var png = ClickRingRenderer.RenderPng(radius);
        Assert.Equal((ClickRingRenderer.SizeFor(radius), ClickRingRenderer.SizeFor(radius)), Core.Export.VideoShapeLayers.PngSize(png));
        var (img, px) = Decode(png);
        int side = img.PixelWidth;
        Assert.Equal(side, img.PixelHeight);
        Assert.Equal(0, side % 2);
        Assert.InRange(side, 2 * (radius + ClickRingRenderer.StrokeFor(radius)) + 2, 2 * (radius + ClickRingRenderer.StrokeFor(radius)) + 4);

        byte A(int x, int y) => px[(y * side + x) * 4 + 3];
        // 四隅は透明
        foreach (var (x, y) in new[] { (0, 0), (side - 1, 0), (0, side - 1), (side - 1, side - 1) }) Assert.Equal(0, A(x, y));
        // 輪の線の上（中心から半径の位置）はおよそ 70% の黄色。透明度を掛けていない色で保存する。
        int c = side / 2, r = (int)Math.Round(radius);
        int i = (c * side + c + r) * 4;
        Assert.InRange((int)px[i + 3], 150, 200);
        Assert.True(px[i + 2] > 240 && px[i + 1] > 190 && px[i] < 60, $"ring {px[i + 2]},{px[i + 1]},{px[i]}");
        // 中は淡い塗り（約 20%）
        Assert.InRange((int)A(c, c), 35, 70);
        // 上下左右で対称（中心が画像の中心）
        Assert.InRange(Math.Abs(A(c + r, c) - A(c - r - 1, c)), 0, 30);
        Assert.InRange(Math.Abs(A(c, c + r) - A(c, c - r - 1)), 0, 30);
    });

    [Fact]
    public void SizeAndDrawingHelpers()
    {
        Assert.Equal(0, ClickRingRenderer.SizeFor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClickRingRenderer.RenderPng(0));
        Assert.Equal(40 * 0.18, ClickRingRenderer.StrokeFor(40), 6);
        var d = ClickRingRenderer.CreateDrawing(40);
        Assert.True(d.IsFrozen);
        // 線の中心が半径の位置（外側は半径＋線の太さの半分）。
        Assert.Equal(-(40 + 40 * 0.18 / 2), d.Bounds.X, 1);
    }
}
