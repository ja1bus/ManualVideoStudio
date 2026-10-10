using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>注釈の直線（v1.6）。静止画・動画の図形で共通。</summary>
public class LineAnnotationTests
{
    [Fact]
    public void LineIsSavedAsTypeLineAndReadBack()
    {
        var p = new CapturePoint
        {
            Annotations = [new LineAnnotation { X1 = 1, Y1 = 2, X2 = 30, Y2 = 40, Color = AnnotationColor.Blue, StrokeWidth = 6 }],
        };
        var json = JsonSerializer.Serialize(p, JsonDefaults.Options);
        Assert.Contains("\"type\": \"line\"", json);
        var back = JsonSerializer.Deserialize<CapturePoint>(json, JsonDefaults.Options)!;
        var l = Assert.IsType<LineAnnotation>(Assert.Single(back.Annotations));
        Assert.Equal((1, 2, 30, 40), (l.X1, l.Y1, l.X2, l.Y2));
        Assert.Equal(AnnotationColor.Blue, l.Color);
        Assert.Equal(6, l.StrokeWidth);
        Assert.Equal(AnnotationKind.Line, l.Kind);
    }

    [Fact]
    public void ArrowIsStillSavedAsArrow()
    {
        var json = JsonSerializer.Serialize(new List<Annotation> { new ArrowAnnotation { X2 = 10 } }, JsonDefaults.Options);
        Assert.Contains("\"type\": \"arrow\"", json);
        Assert.DoesNotContain("\"line\"", json);
    }

    [Fact]
    public void HitTestHandlesAndDragWorkLikeArrow()
    {
        var line = new LineAnnotation { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0, StrokeWidth = 4 };
        static (double, double) M(TextAnnotation t, double s) => (0, 0);
        Assert.True(AnnotationGeometry.HitTest(line, new ImagePoint(50, 3), 2, 1, M));
        Assert.False(AnnotationGeometry.HitTest(line, new ImagePoint(50, 20), 2, 1, M));
        // 矢じりが無いので、終点の先の当たりは矢印より狭い。
        Assert.False(AnnotationGeometry.HitTest(line, new ImagePoint(112, 0), 2, 1, M));
        Assert.Equal(2, AnnotationGeometry.Handles(line).Count);

        var moved = (LineAnnotation)line.Clone();
        AnnotationGeometry.DragHandle(line, moved, AnnotationHandle.End, new ImagePoint(80, 60), 200, 200);
        Assert.Equal((0, 0, 80, 60), (moved.X1, moved.Y1, moved.X2, moved.Y2));
        Assert.Equal(new ImageRect(0, 0, 100, 0).Inflate(2), AnnotationGeometry.Bounds(line, 1, M));
    }

    [Fact]
    public void SessionChangesColorAndStrokeOfLine()
    {
        var s = new AnnotationSession([new LineAnnotation { X2 = 50, Y2 = 50 }], 200, 200);
        var id = s.Items[0].Id;
        Assert.True(s.SetColor(id, AnnotationColor.Black));
        Assert.True(s.SetStrokeWidth(id, AnnotationStyle.ThickStroke));
        var l = (LineAnnotation)s.Items[0];
        Assert.Equal(AnnotationColor.Black, l.Color);
        Assert.Equal(AnnotationStyle.ThickStroke, l.StrokeWidth);
        Assert.Equal("直線", AnnotationSession.KindLabel(AnnotationKind.Line));
        Assert.True(s.Remove(id));
        Assert.Equal("直線の削除", s.History.UndoLabel);
    }

    [Fact]
    public Task LineIsDrawnWithoutArrowHead() => StaThread.RunAsync(() =>
    {
        var white = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 200 * 100 * 4).ToArray(), 800);
        var result = AnnotationRenderer.Render(white,
            [new LineAnnotation { X1 = 20, Y1 = 50, X2 = 180, Y2 = 50, Color = AnnotationColor.Red, StrokeWidth = 4 }], 200, 100);
        var mid = ShapeImageTests.Pixel(result, 100, 50);
        Assert.True(mid.R > 200 && mid.G < 60, $"line {mid}");
        // 矢印なら終点の手前で矢じりが上下に広がるが、直線は線幅だけ。
        Assert.Equal((255, 255, 255), ShapeImageTests.Pixel(result, 170, 40));
    });
}

/// <summary>図形のまとまり（v1.6）の編集・Undo・段階。</summary>
public class ShapeGroupEditingTests
{
    private static ProjectEditor Confirmed(double duration = 60)
    {
        var e = new ProjectEditor(TestProjects.Create(duration));
        Assert.True(e.ConfirmCuts().Success);
        return e;
    }

    private static List<Annotation> Rect() => [new RectangleAnnotation { X = 10.04, Y = 20, Width = 100, Height = 50 }];

    [Fact]
    public void AddRequiresSubtitlePhase()
    {
        var e = new ProjectEditor(TestProjects.Create());
        var (r, g) = e.AddShapeGroup(1, 3, Rect(), 1920, 1080);
        Assert.False(r.Success);
        Assert.Null(g);
        Assert.Contains("カットを確定", r.Message);
    }

    [Fact]
    public void AddStoresRoundedCopyAndRecordsUndo()
    {
        var e = Confirmed();
        var shapes = Rect();
        var (r, g) = e.AddShapeGroup(1.0004, 3, shapes, 1920, 1080);
        Assert.True(r.Success);
        var saved = Assert.Single(e.Project.ShapeGroups);
        Assert.Equal(1.0, saved.Start);
        Assert.Equal(10.0, ((RectangleAnnotation)saved.Shapes[0]).X);          // 小数第 1 位
        Assert.NotSame(shapes[0], saved.Shapes[0]);
        Assert.Equal((1920, 1080), (saved.ImageWidth, saved.ImageHeight));
        Assert.Equal("図形の追加", e.History.UndoLabel);
        e.Undo();
        Assert.Empty(e.Project.ShapeGroups);
        e.Redo();
        Assert.Equal(g!.Id, Assert.Single(e.Project.ShapeGroups).Id);
    }

    [Theory]
    [InlineData(3, 3, "終了位置は開始位置より後")]
    [InlineData(1, 1.1, "表示時間が短すぎます")]
    [InlineData(59, 61, "最後まで")]
    [InlineData(-1, 2, "0 秒以上")]
    public void InvalidTimesAreRejected(double start, double end, string message)
    {
        var e = Confirmed();
        var (r, _) = e.AddShapeGroup(start, end, Rect(), 1920, 1080);
        Assert.False(r.Success);
        Assert.Contains(message, r.Message);
        Assert.Empty(e.Project.ShapeGroups);
    }

    [Fact]
    public void EmptyShapesCannotBeAdded()
    {
        var e = Confirmed();
        var (r, _) = e.AddShapeGroup(1, 3, [], 1920, 1080);
        Assert.False(r.Success);
        Assert.Contains("図形がありません", r.Message);
    }

    [Fact]
    public void GroupsMayOverlapInTime()
    {
        var e = Confirmed();
        Assert.True(e.AddShapeGroup(1, 5, Rect(), 1920, 1080).Result.Success);
        Assert.True(e.AddShapeGroup(2, 4, [new ArrowAnnotation { X2 = 50 }], 1920, 1080).Result.Success);
        Assert.Equal(2, e.Project.ShapeGroups.Count);
    }

    [Fact]
    public void UpdateLabelsAndNoOp()
    {
        var e = Confirmed();
        var g = e.AddShapeGroup(1, 3, Rect(), 1920, 1080).Group!;
        var before = e.History.UndoLabel;
        // 編集画面で何も変えずに OK（同じ図形の複製）→ 記録しない
        List<Annotation> Same() => e.Project.ShapeGroups[0].Shapes.Select(a => a.Clone()).ToList();
        Assert.True(e.UpdateShapeGroup(g.Id, 1, 3, Same(), 1920, 1080).Success);
        Assert.Equal(before, e.History.UndoLabel);

        Assert.True(e.UpdateShapeGroup(g.Id, 1, 60, Same(), 1920, 1080).Success);
        Assert.Equal("図形の表示時間の変更", e.History.UndoLabel);
        Assert.Equal(60, e.Project.ShapeGroups[0].End);

        Assert.True(e.UpdateShapeGroup(g.Id, 1, 60, [new LineAnnotation { X2 = 30 }], 1920, 1080).Success);
        Assert.Equal("図形の変更", e.History.UndoLabel);
        Assert.IsType<LineAnnotation>(Assert.Single(e.Project.ShapeGroups[0].Shapes));
        e.Undo();
        Assert.IsType<RectangleAnnotation>(Assert.Single(e.Project.ShapeGroups[0].Shapes));
    }

    [Fact]
    public void UpdateWithNoShapesRemovesGroup()
    {
        var e = Confirmed();
        var g = e.AddShapeGroup(1, 3, Rect(), 1920, 1080).Group!;
        Assert.True(e.UpdateShapeGroup(g.Id, 1, 3, [], 1920, 1080).Success);
        Assert.Empty(e.Project.ShapeGroups);
        Assert.Equal("図形の削除", e.History.UndoLabel);
    }

    [Fact]
    public void RemoveAndUndo()
    {
        var e = Confirmed();
        var a = e.AddShapeGroup(1, 3, Rect(), 1920, 1080).Group!;
        e.AddShapeGroup(5, 8, Rect(), 1920, 1080);
        Assert.True(e.RemoveShapeGroups([a.Id]).Success);
        Assert.Single(e.Project.ShapeGroups);
        e.Undo();
        Assert.Equal(2, e.Project.ShapeGroups.Count);
        Assert.False(e.RemoveShapeGroups(["none"]).Success);
    }

    [Fact]
    public void ReturnToCutsNeedsDiscardWhenShapesExistAndUndoRestoresThem()
    {
        var e = Confirmed();
        e.AddShapeGroup(1, 3, Rect(), 1920, 1080);
        var r = e.ReturnToCutEditing();
        Assert.False(r.Success);
        Assert.Equal(ProjectEditor.SubtitlesOrShapesExistMessage, r.Message);

        Assert.True(e.DiscardSubtitlesAndReturnToCutEditing().Success);
        Assert.Empty(e.Project.ShapeGroups);
        Assert.Equal(EditPhase.Cut, e.Project.EditPhase);
        Assert.Equal("字幕・図形の全破棄", e.History.UndoLabel);
        e.Undo();
        Assert.Single(e.Project.ShapeGroups);
        Assert.Equal(EditPhase.Subtitle, e.Project.EditPhase);
    }

    [Fact]
    public void DiscardLabelStaysForSubtitlesOnly()
    {
        var e = Confirmed();
        e.AddSubtitle(1, 3, "字幕");
        Assert.Equal(ProjectEditor.SubtitlesExistMessage, e.ReturnToCutEditing().Message);
        e.DiscardSubtitlesAndReturnToCutEditing();
        Assert.Equal("字幕の全破棄", e.History.UndoLabel);
    }

    [Fact]
    public void SummaryListsKindsInFixedOrder()
    {
        List<Annotation> shapes =
        [
            new MosaicAnnotation(), new ArrowAnnotation(), new RectangleAnnotation(), new ArrowAnnotation(), new LineAnnotation(),
            new TextAnnotation { Text = "a" },
        ];
        Assert.Equal("四角形・矢印 2・直線・文字・モザイク", ShapeGroup.SummaryOf(shapes));
        Assert.Equal("（図形なし）", ShapeGroup.SummaryOf([]));
    }
}

/// <summary>図形のまとまりの保存・読み込み（schemaVersion 6）。</summary>
public sealed class ShapeGroupPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-shapes-" + Guid.NewGuid().ToString("N"));

    public ShapeGroupPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void SaveAndLoadKeepsShapeGroups()
    {
        var project = TestProjects.Create();
        project.EditPhase = EditPhase.Subtitle;
        project.ShapeGroups.Add(new ShapeGroup
        {
            Start = 1.5, End = 4, ImageWidth = 1920, ImageHeight = 1080,
            Shapes =
            [
                new RectangleAnnotation { X = 1, Y = 2, Width = 30, Height = 40 },
                new LineAnnotation { X1 = 5, Y1 = 6, X2 = 70, Y2 = 80, Color = AnnotationColor.Black },
                new MosaicAnnotation { X = 100, Y = 100, Width = 50, Height = 20, BlockSize = 24 },
            ],
        });
        var path = Path.Combine(_dir, "p.mvsproj.json");
        new ProjectService().Save(project, path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 8", text);
        Assert.Contains("\"shapeGroups\"", text);
        Assert.Contains("\"type\": \"line\"", text);

        var back = new ProjectService().Load(path).Project;
        var g = Assert.Single(back.ShapeGroups);
        Assert.Equal((1.5, 4.0, 1920, 1080), (g.Start, g.End, g.ImageWidth, g.ImageHeight));
        Assert.Equal([AnnotationKind.Rectangle, AnnotationKind.Line, AnnotationKind.Mosaic], g.Shapes.Select(s => s.Kind));
        Assert.Equal(24, ((MosaicAnnotation)g.Shapes[2]).BlockSize);
    }

    [Fact]
    public void LoadDropsBrokenGroupsAndForcesSubtitlePhase()
    {
        var path = Path.Combine(_dir, "broken.mvsproj.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 6,
          "sourceVideo": { "path": "C:\\none\\a.mp4" },
          "editPhase": "cut",
          "shapeGroups": [
            null,
            { "id": "", "start": 5, "end": 6, "imageWidth": 1920, "imageHeight": 1080, "shapes": [ { "type": "rectangle", "x": 1, "y": 1, "width": 9, "height": 9 } ] },
            { "id": "b", "start": 3, "end": 2, "imageWidth": 1920, "imageHeight": 1080, "shapes": [ { "type": "line", "x2": 5 } ] },
            { "id": "c", "start": 1, "end": 2, "imageWidth": 1920, "imageHeight": 1080, "shapes": [] },
            { "id": "d", "start": 1, "end": 2, "imageWidth": 0, "imageHeight": 1080, "shapes": [ { "type": "line", "x2": 5 } ] }
          ]
        }
        """);
        var p = new ProjectService().Load(path).Project;
        var g = Assert.Single(p.ShapeGroups);
        Assert.False(string.IsNullOrEmpty(g.Id));
        Assert.Equal(EditPhase.Subtitle, p.EditPhase);
    }

    [Fact]
    public void OldSchemaWithoutShapesLoadsEmpty()
    {
        var path = Path.Combine(_dir, "old.mvsproj.json");
        File.WriteAllText(path, """{ "schemaVersion": 5, "sourceVideo": { "path": "C:\\none\\a.mp4" } }""");
        var p = new ProjectService().Load(path).Project;
        Assert.Empty(p.ShapeGroups);
        Assert.Equal(EditPhase.Cut, p.EditPhase);
    }
}

/// <summary>図形の書き出しの ffmpeg 引数（v1.6）。</summary>
public class ShapeFFmpegArgumentTests
{
    private static readonly VideoEncodeOptions Enc = new();
    private static VideoMetadata Meta(int w = 1920, int h = 1080, bool audio = true) => new() { Duration = 60, Width = w, Height = h, HasAudio = audio, HasVideo = true };
    private static IReadOnlyList<TimeRange> Identity => [new TimeRange(0, 60)];
    private static IReadOnlyList<TimeRange> Cut => [new TimeRange(0, 10), new TimeRange(20, 60)];

    private static ShapeFilterPlan Plan(int images = 1, bool mosaic = false) => new(
        Enumerable.Range(0, images).Select(i => new ShapeOverlayInput($"shape-{i}", 1 + i, 3.5 + i, 100 + i, 50)).ToList(),
        mosaic ? [new ShapeMosaic(2, 60, 301, 201, 97, 41, 24)] : []);

    private static string After(IReadOnlyList<string> args, string key) => args[args.ToList().IndexOf(key) + 1];

    [Fact]
    public void NoShapesKeepsPreviousArguments()
    {
        foreach (var keep in new[] { Identity, Cut })
        {
            var a = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass").Arguments;
            var b = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass", 0, null).Arguments;
            var c = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass", 0, new ShapeFilterPlan([], [])).Arguments;
            Assert.Equal(a, b);
            Assert.Equal(a, c);
        }
        Assert.Null(FFmpegArguments.ComposeVideoChain(null, null, 10, 10, null));
        Assert.Equal("a,b", FFmpegArguments.ComposeVideoChain("a", null, 10, 10, "b"));
    }

    [Fact]
    public void IdentityWithShapesUsesFilterComplexAndImageInputs()
    {
        var args = FFmpegArguments.Export("in.mp4", Identity, Meta(), "out.mp4", Enc, 9, "subtitles.ass", 0, Plan(2)).Arguments;
        Assert.DoesNotContain("-vf", args);
        var s = string.Join(' ', args);
        Assert.Contains("-i in.mp4 -f png_pipe -i shape-0 -f png_pipe -i shape-1 -filter_complex", s);
        var filter = After(args, "-filter_complex");
        Assert.StartsWith("[0:v:0]null[shp0];", filter);
        Assert.Contains("[shp0][1:v]overlay=100:50:enable='gte(t,1)*lt(t,3.5)'[shp1]", filter);
        Assert.Contains("[shp1][2:v]overlay=101:50:enable='gte(t,2)*lt(t,4.5)'[shp2]", filter);
        Assert.EndsWith("[shp2]subtitles=filename=subtitles.ass[outv]", filter); // 字幕は図形の上
        Assert.Contains("-map [outv] -map 0:a:0", s);
    }

    [Fact]
    public void CutWithShapesPutsShapesBetweenEvenCropAndSubtitles()
    {
        var args = FFmpegArguments.Export("in.mp4", Cut, Meta(1281, 721), "out.mp4", Enc, 9, "subtitles.ass", 0, Plan(1, mosaic: true)).Arguments;
        var filter = After(args, "-filter_complex");
        Assert.Contains("[vcat]crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0[shp0];", filter);
        // モザイク: 偶数へそろえた範囲（300,200 から 98x42）を 24px のマスにする。
        Assert.Contains("[shp0]split[shp0a][shp0b];[shp0b]crop=98:42:300:200,scale=5:2:flags=area,scale=120:48:flags=neighbor,crop=98:42:0:0[shpm0]", filter);
        Assert.Contains("[shp0a][shpm0]overlay=300:200:enable='gte(t,2)*lt(t,60)'[shp1]", filter);
        Assert.Contains("[shp1][1:v]overlay=100:50:enable='gte(t,1)*lt(t,3.5)'[shp2]", filter);
        Assert.EndsWith("[shp2]subtitles=filename=subtitles.ass[outv]", filter);
    }

    [Fact]
    public void MosaicOutsideFrameIsClippedOrSkipped()
    {
        var plan = new ShapeFilterPlan([], [new ShapeMosaic(0, 1, 1270, 710, 50, 50, 16), new ShapeMosaic(0, 1, 2000, 10, 30, 30, 16)]);
        var chain = FFmpegArguments.ComposeVideoChain(null, plan, 1280, 720, null)!;
        Assert.Contains("crop=10:10:1270:710", chain);
        Assert.DoesNotContain("2000", chain);
        Assert.EndsWith("[shp1]null", chain);
    }

    [Fact]
    public void GifPaletteInputFollowsShapeImages()
    {
        var palette = FFmpegArguments.GifPalette("in.mp4", Cut, Meta(), null, GifPreset.Standard, "palette", 9, 0, Plan(2));
        var s1 = string.Join(' ', palette.Arguments);
        Assert.Contains("-i in.mp4 -f png_pipe -i shape-0 -f png_pipe -i shape-1 -filter_complex", s1);
        Assert.Contains("[shp2]fps=", s1); // 図形の後に FPS・縮小

        var encode = FFmpegArguments.GifEncode("in.mp4", Cut, Meta(), null, GifPreset.Standard, "palette", "o.gif", 9, 0, Plan(2));
        var s2 = string.Join(' ', encode.Arguments);
        Assert.Contains("-f png_pipe -i shape-1 -f png_pipe -i palette", s2);
        Assert.Contains("[gifsrc][3:v]paletteuse", s2);

        var noShapes = string.Join(' ', FFmpegArguments.GifEncode("in.mp4", Cut, Meta(), null, GifPreset.Standard, "palette", "o.gif", 9).Arguments);
        Assert.Contains("[gifsrc][1:v]paletteuse", noShapes);
    }

    [Fact]
    public void MosaicsOfScalesToVideoSizeAndKeepsMinimumBlock()
    {
        var groups = new[]
        {
            new ShapeGroup
            {
                Start = 1, End = 2, ImageWidth = 960, ImageHeight = 540,
                Shapes = [new MosaicAnnotation { X = 10, Y = 20, Width = 100.2, Height = 50, BlockSize = 12 }, new RectangleAnnotation { Width = 5, Height = 5 }],
            },
        };
        var m = Assert.Single(VideoShapeLayers.MosaicsOf(groups, 1920, 1080));
        Assert.Equal((20, 40, 201, 100), (m.X, m.Y, m.Width, m.Height));
        Assert.Equal(24, m.BlockSize);
        Assert.Equal((1.0, 2.0), (m.Start, m.End));
        Assert.Equal(AnnotationStyle.MinMosaicBlock, VideoShapeLayers.MosaicsOf(groups, 960, 540)[0].BlockSize); // 12 → 最小 16
    }
}

/// <summary>図形の書き出し用の画像（WPF。STA スレッドで実行する）。</summary>
public class ShapeImageTests
{
    public static (byte R, byte G, byte B) Pixel(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    private static (byte A, byte R, byte G, byte B) Argb(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[3], px[2], px[1], px[0]);
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var d = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return d.Frames[0];
    }

    [Fact]
    public Task RenderCropsToShapesAndKeepsTransparency() => StaThread.RunAsync(() =>
    {
        var r = ShapeOverlayRenderer.Render(
            [new RectangleAnnotation { X = 100, Y = 200, Width = 300, Height = 100, Color = AnnotationColor.Red, StrokeWidth = 4 }],
            960, 540, 1920, 1080);
        Assert.NotNull(r);
        var (x, y, png) = r!.Value;
        // 基準 960x540 → 1920x1080 へ 2 倍。線の外側（線幅の半分と余白）を含めて切り出す。
        Assert.InRange(x, 190, 198);
        Assert.InRange(y, 390, 398);
        var img = Decode(png);
        Assert.InRange(img.PixelWidth, 600, 620);
        var edge = Argb(img, 200 - x + 300, 400 - y);
        Assert.True(edge.A > 200 && edge.R > 200 && edge.G < 60, $"edge {edge}");
        Assert.Equal(0, Argb(img, 200 - x + 300, 400 - y + 100).A); // 四角の中は透明
    });

    [Fact]
    public Task MosaicOnlyGroupHasNoImage() => StaThread.RunAsync(() =>
    {
        Assert.Null(ShapeOverlayRenderer.Render([new MosaicAnnotation { Width = 50, Height = 50 }], 100, 100, 100, 100));
        var layers = ShapeOverlayRenderer.Build(
        [
            new ShapeGroup { Start = 0, End = 1, ImageWidth = 100, ImageHeight = 100, Shapes = [new MosaicAnnotation { Width = 50, Height = 50 }] },
            new ShapeGroup { Start = 2, End = 3, ImageWidth = 100, ImageHeight = 100, Shapes = [new LineAnnotation { X1 = 10, Y1 = 10, X2 = 90, Y2 = 10 }] },
        ], 100, 100);
        var img = Assert.Single(layers.Images);
        Assert.Equal((2.0, 3.0), (img.Start, img.End));
        Assert.Single(layers.Mosaics);
        Assert.True(ShapeOverlayRenderer.Build([], 100, 100).IsEmpty);
    });
}

/// <summary>図形の編集画面（注釈編集画面の動画モード。v1.6）。</summary>
public class VideoShapeEditorTests
{
    private sealed class Dialogs : IDialogService
    {
        public List<string> Errors { get; } = [];
        public void ShowInfo(string message, string title = "情報") { }
        public void ShowWarning(string message, string title = "注意") { }
        public void ShowError(string message, string? detail = null, string title = "エラー") => Errors.Add(message);
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "") => null;
    }

    private static BitmapSource Frame()
    {
        var b = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 180 * 4], 320 * 4);
        b.Freeze();
        return b;
    }

    private static (ImageEditorViewModel Vm, List<(IReadOnlyList<Annotation> Shapes, double Start, double End)> Commits) Open(
        Func<double, double, EditResult>? result = null, IReadOnlyList<Annotation>? shapes = null)
    {
        var commits = new List<(IReadOnlyList<Annotation>, double, double)>();
        var request = new VideoShapeEditRequest("動画の図形 - 追加", Frame(), shapes ?? [], 320, 180, 1, 4, 30, "背景",
            (s, w, h, start, end) =>
            {
                commits.Add((s, start, end));
                return result?.Invoke(start, end) ?? EditResult.Ok();
            });
        return (new ImageEditorViewModel(request, new Dialogs(), null!, NullLogger.Instance), commits);
    }

    [Fact]
    public Task VideoModeShowsTimesAndCommitsOnOk() => StaThread.RunAsync(() =>
    {
        var (vm, commits) = Open();
        Assert.True(vm.IsVideoMode);
        Assert.Equal("00:00:01.000", vm.StartText);
        Assert.Equal("00:00:04.000", vm.EndText);
        Assert.Equal("OK（動画に反映）", vm.SaveButtonText);
        Assert.False(vm.IsDirty);

        vm.SetToolCommand.Execute(EditorTool.Line);
        vm.CommitShape(new ImagePoint(10, 10), new ImagePoint(100, 10));
        Assert.IsType<LineAnnotation>(Assert.Single(vm.Session.Items));
        vm.SetEndToLastCommand.Execute(null);
        Assert.Equal("00:00:30.000", vm.EndText);
        Assert.True(vm.IsDirty);

        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SaveCommand.Execute(null);
        var c = Assert.Single(commits);
        Assert.Equal((1.0, 30.0), (c.Start, c.End));
        Assert.IsType<LineAnnotation>(Assert.Single(c.Shapes));
        Assert.True(closed);
        Assert.False(vm.IsDirty);
    });

    [Fact]
    public Task TimeChangeAloneIsDirtyAndBadTimeIsReported() => StaThread.RunAsync(() =>
    {
        var (vm, commits) = Open(shapes: [new RectangleAnnotation { Width = 10, Height = 10 }]);
        vm.StartText = "abc";
        Assert.True(vm.IsDirty);
        Assert.False(vm.TrySave());
        Assert.Contains("開始", vm.TimeError);
        Assert.Empty(commits);

        vm.StartText = "0:02";
        Assert.True(vm.TrySave());
        Assert.Equal(2.0, Assert.Single(commits).Start);
    });

    [Fact]
    public Task CommitFailureStaysOpenWithMessage() => StaThread.RunAsync(() =>
    {
        var (vm, _) = Open(result: (_, _) => EditResult.Fail("終了位置が編集後の動画の長さを超えています。"),
            shapes: [new RectangleAnnotation { Width = 10, Height = 10 }]);
        vm.EndText = "0:40";
        var closed = false;
        vm.CloseRequested += () => closed = true;
        vm.SaveCommand.Execute(null);
        Assert.False(closed);
        Assert.Contains("超えています", vm.TimeError);
        Assert.True(vm.IsDirty);
    });
}
