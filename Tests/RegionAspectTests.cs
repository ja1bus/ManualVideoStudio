using System.Text.Json;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>v1.16: 録画範囲の縦横比の固定の計算（Core の RegionAspect）。</summary>
public class RegionAspectTests
{
    private static readonly CaptureRegion Fhd = new(0, 0, 1920, 1080);
    private const RegionOrientation L = RegionOrientation.Landscape;
    private const RegionOrientation P = RegionOrientation.Portrait;

    /// <summary>横は 32x18、縦は 18x32 の整数倍で、最小サイズ（64）以上。</summary>
    private static void AssertLocked(CaptureRegion r, RegionOrientation o)
    {
        Assert.True(RegionAspect.IsValidSize(r.Width, r.Height, o), $"{r} は {o} の固定サイズではない");
        Assert.True(r.Width >= RecordingSettings.MinRegionSize && r.Height >= RecordingSettings.MinRegionSize, r.ToString());
        Assert.Equal(r, r.ToEvenSize()); // H.264 用の偶数化で変わらない
        if (o == L) Assert.Equal(16 * r.Height, 9 * r.Width);
        else Assert.Equal(9 * r.Height, 16 * r.Width);
    }

    private static void AssertInside(CaptureRegion r, CaptureRegion bounds)
        => Assert.True(r.X >= bounds.X && r.Y >= bounds.Y && r.Right <= bounds.Right && r.Bottom <= bounds.Bottom, $"{r} が {bounds} からはみ出した");

    [Fact]
    public void PresetSizesFollowOrientation()
    {
        Assert.Equal((1280, 720), RegionAspect.PresetSize(RegionSizePreset.Hd720, L));
        Assert.Equal((1920, 1080), RegionAspect.PresetSize(RegionSizePreset.Hd1080, L));
        Assert.Equal((720, 1280), RegionAspect.PresetSize(RegionSizePreset.Hd720, P));
        Assert.Equal((1080, 1920), RegionAspect.PresetSize(RegionSizePreset.Hd1080, P));
        Assert.Equal((0, 0), RegionAspect.PresetSize(RegionSizePreset.Free, L));
        // 最小は 128x72 / 72x128（最小サイズ 64 を満たす最小の倍率）。
        Assert.Equal(4, RegionAspect.MinScale(L));
        Assert.Equal(4, RegionAspect.MinScale(P));
    }

    [Fact]
    public void ApplyPresetKeepsCenterAndExactSize()
    {
        var current = new CaptureRegion(400, 300, 500, 300); // 中心 (650, 450)
        var (r, shrunk) = RegionAspect.ApplyPreset(current, RegionSizePreset.Hd720, L, new CaptureRegion(0, 0, 2560, 1440));
        Assert.False(shrunk);
        Assert.Equal(new CaptureRegion(10, 90, 1280, 720), r);
        AssertLocked(r, L);
    }

    [Fact]
    public void ApplyPresetShrinksProportionallyWhenMonitorIsTooSmall()
    {
        // 縦 1080x1920 は 1920x1080 のモニターに収まらない → 高さ 1080 以下の 32 の倍数（1056）。比率は 9:16 のまま。
        var (r, shrunk) = RegionAspect.ApplyPreset(new CaptureRegion(660, 240, 600, 600), RegionSizePreset.Hd1080, P, Fhd);
        Assert.True(shrunk);
        Assert.Equal(594, r.Width);
        Assert.Equal(1056, r.Height);
        AssertLocked(r, P);
        AssertInside(r, Fhd);
        Assert.Equal(960, r.X + r.Width / 2); // 中心の x を保つ（y は画面内へ寄せる）

        // 横 1920x1080 は 1366x768 に収まらない → 1344x756。
        var small = new CaptureRegion(0, 0, 1366, 768);
        var (r2, shrunk2) = RegionAspect.ApplyPreset(new CaptureRegion(100, 100, 200, 200), RegionSizePreset.Hd1080, L, small);
        Assert.True(shrunk2);
        Assert.Equal((1344, 756), (r2.Width, r2.Height));
        AssertInside(r2, small);
    }

    [Fact]
    public void ApplyPresetOnMonitorWithNegativeCoordinates()
    {
        // 左側に縦置きのモニター（1440x2560、負の座標）。中心 (-1300, 150) を保とうとして、モニターの左上へ寄せる。
        var left = new CaptureRegion(-1440, -200, 1440, 2560);
        var (r, shrunk) = RegionAspect.ApplyPreset(new CaptureRegion(-1500, 0, 400, 300), RegionSizePreset.Hd720, P, left);
        Assert.False(shrunk);
        Assert.Equal(new CaptureRegion(-1440, -200, 720, 1280), r);
        AssertLocked(r, P);
        AssertInside(r, left);
    }

    [Fact]
    public void RotateSwapsWidthAndHeightAroundCenter()
    {
        var start = new CaptureRegion(100, 100, 640, 360); // 中心 (420, 280)
        var (r, shrunk) = RegionAspect.Rotate(start, P, new CaptureRegion(0, 0, 2560, 1440));
        Assert.False(shrunk);
        Assert.Equal(new CaptureRegion(240, 0, 360, 640), r); // 中心 x を保ち、上端は画面内へ寄せる
        AssertLocked(r, P);
        var (back, _) = RegionAspect.Rotate(r, L, new CaptureRegion(0, 0, 2560, 1440));
        Assert.Equal((640, 360), (back.Width, back.Height));
        AssertLocked(back, L);
    }

    [Fact]
    public void RotateShrinksWhenSwappedSizeDoesNotFit()
    {
        var (r, shrunk) = RegionAspect.Rotate(new CaptureRegion(0, 0, 1920, 1080), P, Fhd);
        Assert.True(shrunk);
        Assert.Equal((594, 1056), (r.Width, r.Height));
        AssertLocked(r, P);
        AssertInside(r, Fhd);
    }

    [Fact]
    public void NormalizeKeepsAdjustedValidSizeAndSnapsOthers()
    {
        // 調整済み（規格サイズ以外の k）はそのまま。
        var adjusted = new CaptureRegion(10, 20, 32 * 25, 18 * 25);
        Assert.Equal(adjusted, RegionAspect.Normalize(adjusted, L, Fhd));
        // 比率の崩れた値は中心を保って最も近い k にする。
        var bad = new CaptureRegion(100, 100, 1000, 500);
        var n = RegionAspect.Normalize(bad, L, Fhd);
        AssertLocked(n, L);
        AssertInside(n, Fhd);
        // 画面より大きければ比例縮小。
        var big = RegionAspect.Normalize(new CaptureRegion(0, 0, 3840, 2160), L, Fhd);
        Assert.Equal(new CaptureRegion(0, 0, 1920, 1080), big);
        // 画面外の位置は内側へ寄せる（大きさは変えない）。
        var outside = RegionAspect.Normalize(new CaptureRegion(5000, -300, 1280, 720), L, Fhd);
        Assert.Equal(new CaptureRegion(640, 0, 1280, 720), outside);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, -1)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    public void CornerDragKeepsRatioAndOppositeCorner(int dx, int dy)
    {
        var start = new CaptureRegion(400, 300, 640, 360);
        foreach (var (mx, my) in new[] { (100, 7), (-50, -90), (13, 200), (-300, -10), (37, 41) })
        {
            var r = RegionAspect.Resize(start, dx, dy, mx, my, L, Fhd);
            AssertLocked(r, L);
            AssertInside(r, Fhd);
            // 反対側の角は動かない。
            Assert.Equal(dx > 0 ? start.X : start.Right, dx > 0 ? r.X : r.Right);
            Assert.Equal(dy > 0 ? start.Y : start.Bottom, dy > 0 ? r.Y : r.Bottom);
        }
    }

    [Fact]
    public void CornerDragFollowsTheAxisMovedMore()
    {
        var start = new CaptureRegion(0, 0, 640, 360); // k = 20
        Assert.Equal(new CaptureRegion(0, 0, 32 * 25, 18 * 25), RegionAspect.Resize(start, 1, 1, 160, 0, L, Fhd)); // 幅 +160 → k 25
        Assert.Equal(new CaptureRegion(0, 0, 32 * 25, 18 * 25), RegionAspect.Resize(start, 1, 1, 0, 90, L, Fhd));  // 高さ +90 → k 25
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void SideEdgeDragChangesHeightEvenlyAroundCenter(int dx)
    {
        var start = new CaptureRegion(400, 300, 640, 360); // 中心 y = 480
        var r = RegionAspect.Resize(start, dx, 0, dx * 160, 55, L, Fhd);
        Assert.Equal((800, 450), (r.Width, r.Height));
        Assert.Equal(480, r.Y + r.Height / 2);
        Assert.Equal(dx > 0 ? start.X : start.Right, dx > 0 ? r.X : r.Right);
        AssertLocked(r, L);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void TopBottomEdgeDragChangesWidthEvenlyAroundCenter(int dy)
    {
        var start = new CaptureRegion(400, 300, 640, 360); // 中心 x = 720
        var r = RegionAspect.Resize(start, 0, dy, 99, -dy * 90, L, Fhd);
        Assert.Equal((480, 270), (r.Width, r.Height));
        Assert.Equal(720, r.X + r.Width / 2);
        Assert.Equal(dy > 0 ? start.Y : start.Bottom, dy > 0 ? r.Y : r.Bottom);
        AssertLocked(r, L);
    }

    [Fact]
    public void DragStopsAtScreenEdgeWithoutBreakingRatio()
    {
        var start = new CaptureRegion(1000, 500, 640, 360);
        // 右下へ大きく広げる: 右端（1920）・下端（1080）のうち先に当たる方で止まる。
        var r = RegionAspect.Resize(start, 1, 1, 5000, 5000, L, Fhd);
        AssertLocked(r, L);
        AssertInside(r, Fhd);
        Assert.Equal(new CaptureRegion(1000, 500, 32 * 28, 18 * 28), r); // 幅 920 / 32 = 28、高さ 580 / 18 = 32 → 28
        // 左右の辺: 上下に均等に広げるので、近い方の上下端で止まる。
        var side = RegionAspect.Resize(new CaptureRegion(200, 100, 640, 360), 1, 0, 3000, 0, L, Fhd); // 中心 y 280 → 高さ 560 まで
        AssertLocked(side, L);
        AssertInside(side, Fhd);
        Assert.Equal(18 * 31, side.Height);
        // 縦向きの上下の辺。
        var portrait = RegionAspect.Resize(new CaptureRegion(800, 100, 360, 640), 0, 1, 0, 5000, P, Fhd);
        AssertLocked(portrait, P);
        AssertInside(portrait, Fhd);
    }

    [Fact]
    public void DragStopsAtMinimumSize()
    {
        var start = new CaptureRegion(400, 300, 640, 360);
        foreach (var (dx, dy) in new[] { (1, 1), (-1, -1), (1, 0), (0, 1), (-1, 0), (0, -1) })
        {
            var r = RegionAspect.Resize(start, dx, dy, -dx * 5000, -dy * 5000, L, Fhd);
            Assert.Equal((128, 72), (r.Width, r.Height));
            AssertLocked(r, L);
        }
        var p = RegionAspect.Resize(new CaptureRegion(400, 300, 360, 640), -1, -1, 5000, 5000, P, Fhd);
        Assert.Equal((72, 128), (p.Width, p.Height));
    }

    [Fact]
    public void DragWorksOnNegativeCoordinates()
    {
        var screen = new CaptureRegion(-1920, -1080, 3840, 2160);
        var start = new CaptureRegion(-800, -600, 640, 360);
        var r = RegionAspect.Resize(start, -1, -1, -320, 10, L, screen);
        Assert.Equal(new CaptureRegion(-800 + 640 - 960, -600 + 360 - 540, 960, 540), r);
        AssertLocked(r, L);
    }

    [Fact]
    public void FreeResizeMatchesPreviousBehavior()
    {
        var start = new CaptureRegion(100, 100, 640, 360);
        Assert.Equal(new CaptureRegion(100, 100, 741, 360), RegionAspect.ResizeFree(start, 1, 0, 101, 77));
        Assert.Equal(new CaptureRegion(150, 130, 590, 330), RegionAspect.ResizeFree(start, -1, -1, 50, 30));
        Assert.Equal(new CaptureRegion(100, 100, 64, 64), RegionAspect.ResizeFree(start, 1, 1, -5000, -5000));
        Assert.Equal(new CaptureRegion(676, 396, 64, 64), RegionAspect.ResizeFree(start, -1, -1, 5000, 5000));
    }

    /// <summary>ドラッグ・規格サイズ・向き・復元のどの経路でも、同じ条件（単位の整数倍・画面内）になる。</summary>
    [Fact]
    public void AllPathsProduceConsistentLockedSizes()
    {
        var rnd = new Random(1234);
        var screens = new[] { Fhd, new CaptureRegion(-1280, -300, 3200, 1380), new CaptureRegion(0, 0, 1366, 768) };
        foreach (var screen in screens)
            foreach (var o in new[] { L, P })
                for (int i = 0; i < 300; i++)
                {
                    var any = new CaptureRegion(screen.X + rnd.Next(screen.Width), screen.Y + rnd.Next(screen.Height), rnd.Next(1, 3000), rnd.Next(1, 3000));
                    var n = RegionAspect.Normalize(any, o, screen);
                    AssertLocked(n, o);
                    AssertInside(n, screen);
                    var d = RegionAspect.Resize(n, rnd.Next(-1, 2), rnd.Next(-1, 2), rnd.Next(-2000, 2000), rnd.Next(-2000, 2000), o, screen);
                    AssertLocked(d, o);
                    AssertInside(d, screen);
                    var other = o == L ? P : L;
                    var (rot, _) = RegionAspect.Rotate(d, other, screen);
                    AssertLocked(rot, other);
                    AssertInside(rot, screen);
                    var (pre, _) = RegionAspect.ApplyPreset(rot, rnd.Next(2) == 0 ? RegionSizePreset.Hd720 : RegionSizePreset.Hd1080, other, screen);
                    AssertLocked(pre, other);
                    AssertInside(pre, screen);
                    // 録画開始時の確定（Normalize → 偶数化）でも変わらない。
                    Assert.Equal(pre, RegionAspect.Normalize(pre, other, screen).ClampTo(screen).ToEvenSize());
                }
    }
}

/// <summary>v1.16: 録画画面の大きさの種類・向き・固定中のドラッグ・数値入力・設定の保存と復元。</summary>
public class RecorderAspectViewModelTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public void ShowInfo(string message, string title = "情報") { }
        public void ShowWarning(string message, string title = "注意") { }
        public void ShowError(string message, string? detail = null, string title = "エラー") { }
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    private sealed class FakeSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;
        public int Saves { get; private set; }
        public AppSettings Load() => Current;
        public void Save() => Saves++;
    }

    /// <summary>2 枚のモニター: 主 1920x1080 (0,0) と、左側の 1280x1024 (-1280,-200)。</summary>
    private sealed class TwoMonitors : ICaptureTargetService
    {
        public static readonly CaptureMonitor Main = new(1, @"\\.\DISPLAY1", new CaptureRegion(0, 0, 1920, 1080), true);
        public static readonly CaptureMonitor Left = new(2, @"\\.\DISPLAY2", new CaptureRegion(-1280, -200, 1280, 1024), false);
        public IReadOnlyList<CaptureWindow> ListWindows() => [];
        public IReadOnlyList<CaptureMonitor> ListMonitors() => [Main, Left];
        public CaptureRegion VirtualScreen() => new(-1280, -200, 3200, 1280);
        public CaptureRegion? GetWindowBounds(IntPtr handle) => null;
        public bool BringToFront(IntPtr handle) => false;
        public CaptureMonitor MonitorOf(CaptureRegion region)
        {
            int cx = region.X + region.Width / 2, cy = region.Y + region.Height / 2;
            return ListMonitors().FirstOrDefault(m => cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom) ?? Main;
        }
    }

    private sealed class FakeAudio : IAudioInputDeviceService
    {
        public Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public string? GetDefaultDeviceName() => null;
        public AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName) => null;
    }

    private sealed class FakeInfo : IFFmpegInfoService
    {
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeRecorder : IRecordingService
    {
        public RecordingState State { get; private set; } = RecordingState.Idle;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public int SegmentCount => 1;
        public event EventHandler? StateChanged;
        public event EventHandler<string>? Interrupted { add { } remove { } }
        public void SetState(RecordingState s) { State = s; StateChanged?.Invoke(this, EventArgs.Empty); }
        public Task StartAsync(RecordingStartRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RecordingResult> StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AbortAsync() => Task.CompletedTask;
    }

    private static (RecorderViewModel Vm, FakeSettings Settings, FakeRecorder Recorder) Create(RecordingSettings? recording = null)
    {
        var app = new AppSettings();
        if (recording is not null) app.Recording = recording;
        var settings = new FakeSettings(app);
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new FakeInfo(), settings, NullLogger<FFmpegStatus>.Instance);
        var recorder = new FakeRecorder();
        var vm = new RecorderViewModel(settings, recorder, new TwoMonitors(), new FakeAudio(), ffmpeg, new FakeDialogs(), NullLogger<RecorderViewModel>.Instance)
        {
            OutputDirectory = Path.Combine(Path.GetTempPath(), "mvs-aspect-unused"),
        };
        return (vm, settings, recorder);
    }

    private static RecordingSettings Saved(int x, int y, int w, int h, RegionSizePreset preset = RegionSizePreset.Free, RegionOrientation o = RegionOrientation.Landscape)
        => new() { RegionX = x, RegionY = y, RegionWidth = w, RegionHeight = h, RegionSizePreset = preset, RegionOrientation = o };

    [Fact]
    public void DefaultsAreFreeLandscapeAndFreeDragUnchanged()
    {
        var (vm, _, _) = Create(Saved(100, 100, 1001, 503));
        Assert.Equal(RegionSizePreset.Free, vm.SizePreset);
        Assert.Equal(RegionOrientation.Landscape, vm.Orientation);
        Assert.False(vm.IsAspectLocked);
        Assert.False(vm.CanChangeOrientation);
        Assert.Equal(new CaptureRegion(100, 100, 1001, 503), vm.Region);
        Assert.Equal("1001×503・自由サイズ", vm.FrameLabel);
        // 自由サイズのドラッグは従来どおり（幅・高さを別々に）。
        vm.ResizeRegionByDrag(vm.Region, 1, 0, 50, 999);
        Assert.Equal(new CaptureRegion(100, 100, 1051, 503), vm.Region);
        vm.ResizeRegionByDrag(vm.Region, -1, -1, 5000, 5000);
        Assert.Equal((64, 64), (vm.Region.Width, vm.Region.Height));
        Assert.Equal("1280×720", vm.Preset720Label);
        Assert.Equal("1920×1080", vm.Preset1080Label);
    }

    [Fact]
    public void PresetLocksRatioAndReclickRestoresPresetSize()
    {
        var (vm, _, _) = Create(Saved(300, 200, 800, 600)); // 中心 (700, 500)
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        Assert.Equal(new CaptureRegion(60, 140, 1280, 720), vm.Region);
        Assert.True(vm.IsAspectLocked && vm.IsPreset720 && vm.CanChangeOrientation);
        Assert.Equal("1280", vm.RegionWidthText);
        Assert.Equal("720", vm.RegionHeightText);
        Assert.Equal("1280×720・横16:9", vm.FrameLabel);
        Assert.Equal("", vm.StatusText);

        // 固定中のドラッグでは種類も固定も変わらず、比率を保つ。
        vm.ResizeRegionByDrag(vm.Region, 1, 1, -320, -10);
        Assert.Equal((960, 540), (vm.Region.Width, vm.Region.Height));
        Assert.Equal(RegionSizePreset.Hd720, vm.SizePreset);
        Assert.Equal("960", vm.RegionWidthText);
        Assert.Equal("960×540・横16:9", vm.FrameLabel);
        vm.ResizeRegionByDrag(vm.Region, 0, -1, 0, -90); // 上の辺を上へ
        Assert.Equal((1120, 630), (vm.Region.Width, vm.Region.Height));
        // 移動では大きさが変わらない。
        var size = (vm.Region.Width, vm.Region.Height);
        vm.MoveRegionByDrag(vm.Region, 123, -45);
        Assert.Equal(size, (vm.Region.Width, vm.Region.Height));
        vm.MoveRegionByDrag(vm.Region, 99999, 99999); // 画面端で止まり、大きさは変わらない
        Assert.Equal(size, (vm.Region.Width, vm.Region.Height));
        Assert.Equal(1920, vm.Region.Right);

        // 同じボタンをもう一度押すと規格サイズに戻る。
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        Assert.Equal((1280, 720), (vm.Region.Width, vm.Region.Height));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd1080);
        Assert.Equal(new CaptureRegion(0, 0, 1920, 1080), vm.Region);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void PortraitPresetShrinksOnLandscapeMonitorWithMessage()
    {
        var (vm, _, _) = Create(Saved(600, 300, 600, 400));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        vm.SelectOrientationCommand.Execute(RegionOrientation.Portrait);
        // 1280x720 を入れ替えた 720x1280 は高さ 1080 に収まらない → 594x1056（比率 9:16 のまま）。
        Assert.Equal((594, 1056), (vm.Region.Width, vm.Region.Height));
        Assert.Contains("594×1056", vm.StatusText);
        Assert.Equal("720×1280", vm.Preset720Label);
        Assert.Equal("1080×1920", vm.Preset1080Label);
        Assert.Equal("594×1056・縦9:16", vm.FrameLabel);
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd1080);
        Assert.Equal((594, 1056), (vm.Region.Width, vm.Region.Height));
        Assert.Contains("収まらない", vm.StatusText);
        // 横へ戻すと幅と高さを入れ替える。
        vm.SelectOrientationCommand.Execute(RegionOrientation.Landscape);
        Assert.Equal((1056, 594), (vm.Region.Width, vm.Region.Height));
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void PresetUsesTheMonitorOfTheFrame()
    {
        // 左のモニター（1280x1024、負の座標）にある枠で 1920 系 → 1280x720 に縮める。
        var (vm, _, _) = Create(Saved(-1000, 0, 400, 300));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd1080);
        Assert.Equal((1280, 720), (vm.Region.Width, vm.Region.Height));
        Assert.Equal(-1280, vm.Region.X);
        Assert.Contains("1280×720", vm.StatusText);
    }

    [Fact]
    public void FreeSizeKeepsFrameAndOrientationIsRetained()
    {
        var (vm, _, _) = Create(Saved(600, 300, 600, 400));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        vm.SelectOrientationCommand.Execute(RegionOrientation.Portrait);
        var region = vm.Region;
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Free);
        Assert.Equal(region, vm.Region);
        Assert.False(vm.IsAspectLocked);
        Assert.False(vm.CanChangeOrientation);
        Assert.Equal(RegionOrientation.Portrait, vm.Orientation); // 内部では最後の向きを保持
        // 自由サイズでは向きの切り替えは効かない。
        vm.SelectOrientationCommand.Execute(RegionOrientation.Landscape);
        Assert.Equal(RegionOrientation.Portrait, vm.Orientation);
        Assert.Equal(region, vm.Region);
        // 再び規格サイズを選ぶと、保持していた向き（縦）で固定する。
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        Assert.True(RegionAspect.IsValidSize(vm.Region.Width, vm.Region.Height, RegionOrientation.Portrait));
    }

    [Fact]
    public void NumericApplySwitchesToFreeButInputErrorsDoNot()
    {
        var (vm, _, _) = Create(Saved(300, 200, 800, 600));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
        // 入力途中（表示だけ）では固定を解除しない。
        vm.RegionWidthText = "100";
        Assert.True(vm.IsAspectLocked);
        // 入力エラーでは解除しない。
        vm.RegionWidthText = "abc";
        vm.ApplyRegionSizeCommand.Execute(null);
        Assert.True(vm.IsAspectLocked);
        Assert.Equal((1280, 720), (vm.Region.Width, vm.Region.Height));
        Assert.Contains("整数", vm.StatusText);
        // ドラッグ（プログラムによる表示の更新）は数値入力の適用ではない。
        vm.ResizeRegionByDrag(vm.Region, 1, 1, -320, 0);
        Assert.True(vm.IsAspectLocked);
        Assert.Equal("960", vm.RegionWidthText);
        // 適用は入力値をそのまま使い、自由サイズになる。
        vm.RegionWidthText = "1000";
        vm.RegionHeightText = "333";
        vm.ApplyRegionSizeCommand.Execute(null);
        Assert.False(vm.IsAspectLocked);
        Assert.Equal(RegionSizePreset.Free, vm.SizePreset);
        Assert.Equal((1000, 333), (vm.Region.Width, vm.Region.Height));
    }

    [Fact]
    public void SettingsRoundTripKeepsAdjustedRegion()
    {
        var (vm, settings, _) = Create(Saved(300, 200, 800, 600));
        vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd1080);
        vm.SelectOrientationCommand.Execute(RegionOrientation.Portrait);
        vm.ResizeRegionByDrag(vm.Region, -1, -1, 180, 320); // 縮める
        var adjusted = vm.Region;
        vm.SaveSettings();
        var s = settings.Current.Recording;
        Assert.Equal(RegionSizePreset.Hd1080, s.RegionSizePreset);
        Assert.Equal(RegionOrientation.Portrait, s.RegionOrientation);
        Assert.Equal((adjusted.X, adjusted.Y, adjusted.Width, adjusted.Height), (s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight));

        // JSON で保存して読み直す（開き直しても規格サイズに戻さない）。
        var json = JsonSerializer.Serialize(settings.Current, JsonDefaults.Options);
        Assert.Contains("\"regionSizePreset\": \"hd1080\"", json);
        Assert.Contains("\"regionOrientation\": \"portrait\"", json);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)!;
        loaded.Recording.NormalizeOnLoad();
        var (vm2, _, _) = Create(loaded.Recording);
        Assert.Equal(adjusted, vm2.Region);
        Assert.Equal(RegionSizePreset.Hd1080, vm2.SizePreset);
        Assert.Equal(RegionOrientation.Portrait, vm2.Orientation);
        Assert.True(vm2.CanChangeOrientation);
    }

    [Fact]
    public void OldSettingsAreFreeLandscapeEvenIfSizeLooksLikePreset()
    {
        // v1.15 までの設定（新しい項目なし）。1280x720 でも固定とは判断しない。
        const string json = """{ "recording": { "mode": "region", "regionX": 10, "regionY": 20, "regionWidth": 1280, "regionHeight": 720 } }""";
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)!;
        loaded.Recording.NormalizeOnLoad();
        Assert.Equal(RegionSizePreset.Free, loaded.Recording.RegionSizePreset);
        Assert.Equal(RegionOrientation.Landscape, loaded.Recording.RegionOrientation);
        var (vm, _, _) = Create(loaded.Recording);
        Assert.False(vm.IsAspectLocked);
        Assert.Equal(new CaptureRegion(10, 20, 1280, 720), vm.Region);
    }

    [Fact]
    public void InvalidSavedValuesAreNormalized()
    {
        var s = Saved(100, 100, 1000, 500, (RegionSizePreset)99, (RegionOrientation)42);
        s.NormalizeOnLoad();
        Assert.Equal(RegionSizePreset.Free, s.RegionSizePreset);
        Assert.Equal(RegionOrientation.Landscape, s.RegionOrientation);
        // 固定の設定なのに比率の崩れた大きさ（手で書き換えた等）は、比率を保って最も近い大きさにする。
        var (vm, _, _) = Create(Saved(100, 100, 1000, 500, RegionSizePreset.Hd720));
        Assert.True(RegionAspect.IsValidSize(vm.Region.Width, vm.Region.Height, RegionOrientation.Landscape));
    }

    [Fact]
    public void ChangesAreRejectedWhileRecordingOrPaused()
    {
        foreach (var state in new[] { RecordingState.Recording, RecordingState.Paused })
        {
            var (vm, _, recorder) = Create(Saved(300, 200, 800, 600));
            vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd720);
            var region = vm.Region;
            recorder.SetState(state);
            Assert.False(vm.IsIdle);
            Assert.False(vm.CanChangeOrientation);
            vm.SelectSizePresetCommand.Execute(RegionSizePreset.Hd1080);
            vm.SelectSizePresetCommand.Execute(RegionSizePreset.Free);
            vm.SelectOrientationCommand.Execute(RegionOrientation.Portrait);
            vm.ResizeRegionByDrag(vm.Region, 1, 1, 100, 100);
            vm.MoveRegionByDrag(vm.Region, 100, 100);
            vm.RegionWidthText = "500";
            vm.ApplyRegionSizeCommand.Execute(null);
            Assert.Equal(region, vm.Region);
            Assert.Equal(RegionSizePreset.Hd720, vm.SizePreset);
            Assert.Equal(RegionOrientation.Landscape, vm.Orientation);
        }
    }
}
