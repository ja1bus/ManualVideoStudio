using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Tests;

/// <summary>拡大の動き（v1.21。ZoomMath）。</summary>
public class ZoomMathTests
{
    private static readonly ZoomWindow Two = new(10, 15, 2, 400, 200);

    [Fact]
    public void ProgressRampsInAndOutOverPointThreeSeconds()
    {
        Assert.Equal(0, ZoomMath.Progress(Two, 9.9));
        Assert.Equal(0, ZoomMath.Progress(Two, 10));
        Assert.Equal(0.5, ZoomMath.Progress(Two, 10.15), 6);
        Assert.Equal(1, ZoomMath.Progress(Two, 10.3), 6);
        Assert.Equal(1, ZoomMath.Progress(Two, 12.5), 6);
        Assert.Equal(0.5, ZoomMath.Progress(Two, 14.85), 6);
        Assert.Equal(0, ZoomMath.Progress(Two, 15), 6);
        Assert.Equal(0, ZoomMath.Progress(Two, 16));
    }

    [Fact]
    public void ShortWindowUsesThirds()
    {
        var w = new ZoomWindow(1, 1.6, 2, 0, 0);
        Assert.Equal(0.2, ZoomMath.Ramp(w), 6);
        Assert.Equal(0.5, ZoomMath.Progress(w, 1.1), 6);
        Assert.Equal(1, ZoomMath.Progress(w, 1.3), 6);
        Assert.Equal(0.5, ZoomMath.Progress(w, 1.5), 6);
        Assert.Equal(0.3, ZoomMath.Ramp(new ZoomWindow(0, 0.9, 2, 0, 0)), 6);
    }

    [Fact]
    public void AtGivesFullZoomAndRectInTheMiddleAndIdentityOutside()
    {
        var mid = ZoomMath.At(Two, 12);
        Assert.Equal(2, mid.Zoom, 6);
        Assert.Equal(400, mid.X, 6);
        Assert.Equal(200, mid.Y, 6);
        Assert.True(ZoomMath.At(Two, 5).IsIdentity);
        Assert.Equal(0, ZoomMath.At(Two, 5).X);
        Assert.True(ZoomMath.At(Two, 15).IsIdentity);
        Assert.True(ZoomMath.At(Array.Empty<ZoomWindow>(), 3).IsIdentity);
    }

    [Fact]
    public void FocusPointStaysStillWhileZooming()
    {
        // 拡大後の範囲（左上 400,200・大きさ W/2 × H/2）の中の点は、寄る間も画面上の同じ位置にある。
        const double w = 1920, h = 1080, z = 2;
        double fx = 400 + 400 * (w / z) / (w - w / z), fy = 200 + 200 * (h / z) / (h - h / z); // 注目点（拡大で動かない点）
        double? sx = null, sy = null;
        foreach (var t in new[] { 10.05, 10.1, 10.2, 10.3, 12.0, 14.8 })
        {
            var s = ZoomMath.At(Two, t);
            double screenX = (fx - s.X) * s.Zoom, screenY = (fy - s.Y) * s.Zoom;
            sx ??= screenX;
            sy ??= screenY;
            Assert.Equal(sx.Value, screenX, 6);
            Assert.Equal(sy.Value, screenY, 6);
        }
        Assert.Equal(fx, sx!.Value, 6); // 等倍でも同じ位置
    }

    [Fact]
    public void FactorNearOneIsIgnored()
    {
        var w = new ZoomWindow(0, 5, 1.00001, 10, 10);
        Assert.False(ZoomMath.IsEffective(w));
        Assert.True(ZoomMath.At(w, 2).IsIdentity);
        Assert.Null(ZoomMath.ZoomPanFilter([w], 1920, 1080, "30"));
    }

    [Fact]
    public void ExpressionForOneZoom()
    {
        var f = ZoomMath.ZoomPanFilter([Two], 1920, 1080, "30");
        Assert.Equal("zoompan=z='1+1*(clip((it-10)/0.3,0,1)-clip((it-14.7)/0.3,0,1))'"
            + ":x='(400*between(it,10,15)/(1-1/2))*(1-1/zoom)':y='(200*between(it,10,15)/(1-1/2))*(1-1/zoom)'"
            + ":d=1:s=1920x1080:fps=30", f);
    }

    [Fact]
    public void ExpressionForTwoZoomsSumsTerms()
    {
        var a = new ZoomWindow(20, 20.6, 4, 960, 540.5);
        var f = ZoomMath.ZoomPanFilter([a, Two], 1280, 720, "30000/1001")!;
        Assert.Contains("z='1+1*(clip((it-10)/0.3,0,1)-clip((it-14.7)/0.3,0,1))+3*(clip((it-20)/0.2,0,1)-clip((it-20.4)/0.2,0,1))'", f);
        Assert.Contains("x='(400*between(it,10,15)/(1-1/2)+960*between(it,20,20.6)/(1-1/4))*(1-1/zoom)'", f);
        Assert.Contains("y='(200*between(it,10,15)/(1-1/2)+540.5*between(it,20,20.6)/(1-1/4))*(1-1/zoom)'", f);
        Assert.EndsWith(":d=1:s=1280x720:fps=30000/1001", f);
    }

    [Fact]
    public void ZoomsOfScalesGroupCoordinatesToVideo()
    {
        var g = new ShapeGroup
        {
            Start = 1, End = 4, ImageWidth = 960, ImageHeight = 540,
            Shapes = [new RectangleAnnotation { X = 1, Y = 1, Width = 5, Height = 5 }, new ZoomAnnotation { X = 100, Y = 50, Width = 480, Height = 270 }],
        };
        var z = Assert.Single(VideoShapeLayers.ZoomsOf([g, new ShapeGroup { Start = 0, End = 1, ImageWidth = 960, ImageHeight = 540, Shapes = [] }], 1920, 1080));
        Assert.Equal(new ZoomWindow(1, 4, 2, 200, 100), z);
    }
}

/// <summary>クリックの強調（v1.21）の時刻の写し方。</summary>
public class ClickMarkTests
{
    [Fact]
    public void ClicksAtRemovedPositionsAreDroppedAndTimesMapped()
    {
        IReadOnlyList<TimeRange> keep = [new TimeRange(0, 10), new TimeRange(20, 30)];
        var clicks = VideoShapeLayers.ClicksOf([(25.0, 100, 100), (5.0, 10, 20), (15.0, 50, 50), (21.0, 2000, 10), (21.0, -1, 10)], keep, 1920, 1080);
        Assert.Equal([new ClickMark(5, 10, 20), new ClickMark(15, 100, 100)], clicks);
    }

    [Fact]
    public void WindowsAreTruncatedBeforeTheNextClick()
    {
        var w = VideoShapeLayers.ClickWindowsOf([new ClickMark(0.02, 1, 1), new ClickMark(1, 2, 2), new ClickMark(1.2, 3, 3), new ClickMark(1.2, 4, 4)]);
        Assert.Equal(new ClickWindow(0, 0.47, 1, 1), w[0]);
        Assert.Equal(new ClickWindow(0.95, 1.15, 2, 2), w[1]);
        // 同じ時刻の 2 つ目: 1 つ目は長さ 0 になり除かれる。
        Assert.Equal(3, w.Count);
        Assert.Equal(new ClickWindow(1.15, 1.65, 4, 4), w[2]);
    }

    [Fact]
    public void RadiusScalesWithHeight()
    {
        Assert.Equal(40, VideoShapeLayers.ClickRadiusFor(ClickHighlightSize.Medium, 1080));
        Assert.Equal(14, VideoShapeLayers.ClickRadiusFor(ClickHighlightSize.Small, 540));
        Assert.Equal(56 * 2, VideoShapeLayers.ClickRadiusFor(ClickHighlightSize.Large, 2160));
    }

    [Fact]
    public void LayersEmptinessIncludesZoomsAndClicks()
    {
        Assert.True(VideoShapeLayers.Empty.IsEmpty);
        Assert.False((VideoShapeLayers.Empty with { Zooms = [new ZoomWindow(0, 1, 2, 0, 0)] }).IsEmpty);
        // 輪の画像が無いクリックは使わない。
        Assert.True((VideoShapeLayers.Empty with { Clicks = [new ClickMark(1, 1, 1)] }).IsEmpty);
        Assert.False((VideoShapeLayers.Empty with { Clicks = [new ClickMark(1, 1, 1)], ClickRingPng = [1] }).IsEmpty);
    }

    [Fact]
    public void PngSizeReadsHeader()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 90, 0, 0, 0, 80, 8, 6, 0, 0, 0];
        Assert.Equal((90, 80), VideoShapeLayers.PngSize(png));
        Assert.Null(VideoShapeLayers.PngSize([1, 2, 3]));
    }
}

/// <summary>拡大・クリックの強調の ffmpeg 引数（v1.21）。</summary>
public class ZoomClickFFmpegArgumentTests
{
    private static readonly VideoEncodeOptions Enc = new();
    private static VideoMetadata Meta(int w = 1920, int h = 1080) => new() { Duration = 60, Width = w, Height = h, HasAudio = true, HasVideo = true, Fps = 30 };
    private static IReadOnlyList<TimeRange> Identity => [new TimeRange(0, 60)];
    private static IReadOnlyList<TimeRange> Cut => [new TimeRange(0, 10), new TimeRange(20, 60)];
    private static string After(IReadOnlyList<string> args, string key) => args[args.ToList().IndexOf(key) + 1];

    private static ShapeFilterPlan ImagePlan() => new([new ShapeOverlayInput("shape-0", 1, 3.5, 100, 50)], []);

    private static ShapeFilterPlan Clicks(int count, int images = 0) => new(
        Enumerable.Range(0, images).Select(i => new ShapeOverlayInput($"shape-{i}", 1, 2, 0, 0)).ToList(), [])
    {
        Clicks = Enumerable.Range(0, count).Select(i => new ClickWindow(i, i + 0.5, 100 + i, 30)).ToList(),
        ClickRingFile = "click-ring", ClickRingWidth = 80, ClickRingHeight = 80,
    };

    [Fact]
    public void WithoutZoomAndClicksArgumentsAreUnchanged()
    {
        // 1.20 と同じ文字列（拡大・クリックを足す前の出力をそのまま書いたもの）。
        Assert.Equal("null[shp0];[shp0][1:v]overlay=100:50:enable='gte(t,1)*lt(t,3.5)'[shp1];[shp1]subtitles=filename=subtitles.ass",
            FFmpegArguments.ComposeVideoChain(null, ImagePlan(), 1920, 1080, "subtitles=filename=subtitles.ass", 30));
        foreach (var keep in new[] { Identity, Cut })
        {
            var a = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass").Arguments;
            var b = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass", 0,
                new ShapeFilterPlan([], []) { Zooms = [], Clicks = [] }).Arguments;
            Assert.Equal(a, b);
            // 輪の画像が無いクリックは無いものとして扱う。
            var c = FFmpegArguments.Export("in.mp4", keep, Meta(), "out.mp4", Enc, 9, "subtitles.ass", 0,
                new ShapeFilterPlan([], []) { Clicks = [new ClickWindow(0, 1, 1, 1)] }).Arguments;
            Assert.Equal(a, c);
        }
        Assert.Equal("[0:v:0]trim=start=0:end=10,setpts=PTS-STARTPTS[v0];[0:a:0]atrim=start=0:end=10,asetpts=PTS-STARTPTS[a0];"
            + "[0:v:0]trim=start=20:end=60,setpts=PTS-STARTPTS[v1];[0:a:0]atrim=start=20:end=60,asetpts=PTS-STARTPTS[a1];"
            + "[v0][a0][v1][a1]concat=n=2:v=1:a=1[vcat][outa];[vcat]subtitles=filename=subtitles.ass[outv]",
            After(FFmpegArguments.Export("in.mp4", Cut, Meta(), "out.mp4", Enc, 9, "subtitles.ass").Arguments, "-filter_complex"));
    }

    [Fact]
    public void ZoomComesAfterShapesAndBeforeSubtitles()
    {
        var plan = ImagePlan() with { Zooms = [new ZoomWindow(5, 8, 2, 100, 50)] };
        var args = FFmpegArguments.Export("in.mp4", Cut, Meta(1281, 721), "out.mp4", Enc, 9, "subtitles.ass", 0, plan).Arguments;
        var filter = After(args, "-filter_complex");
        Assert.Contains("[vcat]crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0[shp0];[shp0][1:v]overlay=100:50:enable='gte(t,1)*lt(t,3.5)'[shp1];[shp1]zoompan=z='", filter);
        Assert.Contains(":d=1:s=1280x720:fps=30[shp2];[shp2]subtitles=filename=subtitles.ass[outv]", filter);
    }

    [Fact]
    public void ZoomOnlyWithoutCutsUsesFilterComplex()
    {
        var plan = new ShapeFilterPlan([], []) { Zooms = [new ZoomWindow(5, 8, 2, 100, 50)] };
        var args = FFmpegArguments.Export("in.mp4", Identity, Meta(), "out.mp4", Enc, 9, null, 0, plan).Arguments;
        Assert.StartsWith("[0:v:0]null[shp0];[shp0]zoompan=", After(args, "-filter_complex"));
        Assert.EndsWith("[shp1]null[outv]", After(args, "-filter_complex"));
        Assert.Equal(1, args.Count(a => a == "-i"));
    }

    [Fact]
    public void ClickRingIsInputAfterShapeImagesAndOverlaidWithTimeExpressions()
    {
        var args = FFmpegArguments.Export("in.mp4", Identity, Meta(), "out.mp4", Enc, 9, null, 0, Clicks(2, images: 1)).Arguments;
        var s = string.Join(' ', args);
        Assert.Contains("-i in.mp4 -f png_pipe -i shape-0 -f png_pipe -i click-ring -filter_complex", s);
        var filter = After(args, "-filter_complex");
        Assert.Contains("[shp1][2:v]overlay=x='60*gte(t,0)*lt(t,0.5)+61*gte(t,1)*lt(t,1.5)':y='(-10)*gte(t,0)*lt(t,0.5)+(-10)*gte(t,1)*lt(t,1.5)'"
            + ":enable='gte(t,0)*lt(t,0.5)+gte(t,1)*lt(t,1.5)'[shp2];[shp2]null[outv]", filter);
        Assert.DoesNotContain("split", filter);
    }

    [Fact]
    public void MoreThanEightyClicksAreSplitIntoSeveralOverlays()
    {
        var chain = FFmpegArguments.ComposeVideoChain(null, Clicks(170), 1920, 1080, null)!;
        Assert.Contains("[1:v]split=3[ring0][ring1][ring2]", chain);
        Assert.Contains("[shp0][ring0]overlay=", chain);
        Assert.Contains("[shp1][ring1]overlay=", chain);
        Assert.Contains("[shp2][ring2]overlay=", chain);
        Assert.EndsWith("[shp3]null", chain);
        // 1 つ目の overlay は 80 件、3 つ目は 10 件。
        var parts = chain.Split(";[shp");
        var first = parts.First(p => p.StartsWith("0][ring0]"));
        var third = parts.First(p => p.StartsWith("2][ring2]"));
        Assert.Equal(80 * 3, first.Split("gte(t,").Length - 1);   // x・y・enable に 1 件ずつ
        Assert.Equal(10 * 3, third.Split("gte(t,").Length - 1);
        Assert.Contains("lt(t,79.5)'", first);
        Assert.DoesNotContain("lt(t,80.5)", first);
        Assert.Contains("lt(t,169.5)'", third);
    }

    [Fact]
    public void TitleAndPaletteIndexesFollowTheRingInput()
    {
        var title = new TitleFilterPlan("title-image", 3, 0, 30);
        var args = FFmpegArguments.Export("in.mp4", Cut, Meta(), "out.mp4", Enc, 9, null, 0, Clicks(1, images: 1), title).Arguments;
        Assert.Contains("-f png_pipe -i click-ring -f png_pipe -i title-image", string.Join(' ', args));
        Assert.Contains("[3:v]loop=", After(args, "-filter_complex"));

        var encode = FFmpegArguments.GifEncode("in.mp4", Cut, Meta(), null, GifPreset.Standard, "palette", "o.gif", 9, 0, Clicks(1, images: 2));
        var s = string.Join(' ', encode.Arguments);
        Assert.Contains("-f png_pipe -i click-ring -f png_pipe -i palette", s);
        Assert.Contains("[gifsrc][4:v]paletteuse", s);
    }

    [Fact]
    public void GifAppliesZoomBeforeFpsAndScale()
    {
        var plan = new ShapeFilterPlan([], []) { Zooms = [new ZoomWindow(1, 3, 2, 10, 10)] };
        var filter = FFmpegArguments.BuildGifFilter(Cut, Meta(), null, GifPreset.Standard, "palettegen", 0, plan);
        Assert.Contains("[shp0]zoompan=", filter);
        Assert.Contains(":d=1:s=1920x1080:fps=30[shp1];[shp1]fps=", filter);
    }

    [Fact]
    public void LongClickFiltersGoToScriptFile()
    {
        var cmd = FFmpegArguments.Export("in.mp4", Identity, Meta(), "out.mp4", Enc, 9, null, 0, Clicks(300));
        Assert.NotNull(cmd.FilterScript);
        Assert.Contains("-/filter_complex", cmd.Arguments);
    }
}

/// <summary>拡大の検証（v1.21）。</summary>
public class ZoomValidationTests
{
    private static ZoomAnnotation Zoom(double x, double y, double w, double h) => new() { X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void FactorMustBeWithinBounds()
    {
        Assert.True(ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 960, 540)], 1920, 1080).IsValid);     // ×2
        Assert.True(ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 480, 270)], 1920, 1080).IsValid);     // ×4
        Assert.True(ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 1728, 972)], 1920, 1080).IsValid);    // 90%
        var tooSmall = ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 400, 225)], 1920, 1080);
        Assert.False(tooSmall.IsValid);
        Assert.Contains("拡大の倍率は ×1.1～×4 にしてください。", tooSmall.Message);
        Assert.False(ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 1800, 1012)], 1920, 1080).IsValid);
    }

    [Fact]
    public void OnlyOneZoomPerGroupAndRectInsideImage()
    {
        var two = ShapeGroupValidator.ValidateShapes([Zoom(0, 0, 960, 540), Zoom(10, 10, 960, 540)], 1920, 1080);
        Assert.Contains(ShapeGroupValidator.TooManyZoomsMessage, two.Message);
        var outside = ShapeGroupValidator.ValidateShapes([Zoom(1000, 0, 960, 540)], 1920, 1080);
        Assert.Contains(ShapeGroupValidator.ZoomOutsideMessage, outside.Message);
        Assert.True(ShapeGroupValidator.ValidateShapes([new RectangleAnnotation { X = -50, Width = 10, Height = 10 }], 1920, 1080).IsValid);
        // 時刻の検証と合わせた版。
        Assert.False(ShapeGroupValidator.Validate(1, 3, 60, 1920, 1080, [Zoom(1000, 0, 960, 540)]).IsValid);
        Assert.True(ShapeGroupValidator.Validate(1, 3, 60, 1920, 1080, [Zoom(960, 540, 960, 540)]).IsValid);
    }

    private static ProjectEditor Confirmed()
    {
        var e = new ProjectEditor(TestProjects.Create(60));
        Assert.True(e.ConfirmCuts().Success);
        return e;
    }

    [Fact]
    public void EditorRejectsOverlappingZoomGroups()
    {
        var e = Confirmed();
        Assert.True(e.AddShapeGroup(1, 5, [Zoom(0, 0, 960, 540)], 1920, 1080).Result.Success);
        var (r, _) = e.AddShapeGroup(4, 8, [Zoom(0, 0, 960, 540)], 1920, 1080);
        Assert.False(r.Success);
        Assert.Equal("拡大の表示時間が、ほかの拡大と重なっています。", r.Message);
        // 拡大の無いまとまりは重ねてよい。接するだけの拡大もよい。
        Assert.True(e.AddShapeGroup(4, 8, [new RectangleAnnotation { Width = 10, Height = 10 }], 1920, 1080).Result.Success);
        var (ok, g) = e.AddShapeGroup(5, 9, [Zoom(0, 0, 960, 540)], 1920, 1080);
        Assert.True(ok.Success);
        // 変更で重ねるのも拒否（自分自身とは比べない）。
        Assert.False(e.UpdateShapeGroup(g!.Id, 3, 9, [Zoom(0, 0, 960, 540)], 1920, 1080).Success);
        Assert.True(e.UpdateShapeGroup(g.Id, 6, 9, [Zoom(10, 10, 960, 540)], 1920, 1080).Success);
        // 倍率の検証も効く。
        Assert.False(e.AddShapeGroup(20, 25, [Zoom(0, 0, 100, 56)], 1920, 1080).Result.Success);
    }
}
