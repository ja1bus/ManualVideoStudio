using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using Xunit.Abstractions;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 録画の時計と実際の映像のずれの実測（v1.21 設計 2.2・要確認 9-1）。
/// 画面の左上に色が黒 ⇔ 白に変わるだけの小窓（録画に写す。操作の入力は送らない）を出し、実際の RecordingService で録画しながら
/// 既知の時刻に色を変え、その時刻を区間の時計（IRecordingClock.TryNow）で記録する。完成した動画から色の変わったコマを探し、
/// 先頭合わせ（Start）と末尾合わせ（End）のそれぞれで「記録した時刻を動画の秒へ直した値 − 実際に変わったコマの時刻」を出す。
/// 結果は試験の出力（dotnet test --logger "console;verbosity=detailed"）に表で出す。
/// 対話セッション（画面のある PC）で実行すること。小窓は前面に出るが入力は奪わない（ShowActivated = false）。
/// </summary>
public sealed class RecordingTimeAlignmentIntegrationTests(MediaFixture fx, ITestOutputHelper log) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private const int Toggles = 6;
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1100);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockStampsMatchTheFramesWhereTheColourChanges(bool withPause)
    {
        using var flasher = ColourWindow.Show();
        var rect = flasher.PhysicalRect();
        // 窓の中央の 64x64 を録画する（DPI の扱いが違っても窓の内側に収まるよう、窓は大きく左上に置く）。
        var region = new CaptureRegion(rect.X + rect.Width / 2 - 32, rect.Y + rect.Height / 2 - 32, 64, 64);
        var output = Path.Combine(fx.Dir, $"align-{(withPause ? "pause" : "single")}.mp4");
        var svc = new RecordingService(new FFmpegRecordingProcessFactory(), fx.Runner, new MediaMetadataService(fx.Runner));
        var stamps = new List<Stamp>();

        await svc.StartAsync(new RecordingStartRequest(Ffmpeg, Ffprobe,
            new ScreenRecordingOptions(region, 30, false, null), output, new RecordingInfo
            {
                X = region.X, Y = region.Y, Width = region.Width, Height = region.Height,
            }, LogInput: false));
        double? seg0Clock = null;
        await Task.Delay(Lead);
        for (int i = 0; i < Toggles; i++)
        {
            if (withPause && i == Toggles / 2)
            {
                // 色を変えない間に一時停止・再開して区間を 2 つにする。
                await svc.PauseAsync();
                seg0Clock = svc.Elapsed.TotalSeconds;
                await Task.Delay(800);
                await svc.ResumeAsync();
                await Task.Delay(Lead);
            }
            Assert.True(svc.TryNow(out _, out var before));
            flasher.SetWhite(i % 2 == 0);
            Assert.True(svc.TryNow(out var seg, out var after));
            svc.Events!.AddMarker();
            stamps.Add(new Stamp(seg, before, after));
            await Task.Delay(Gap);
        }
        var result = await svc.StopAsync();
        double totalClock = svc.Elapsed.TotalSeconds;
        double actual = result.RecordedDuration.TotalSeconds;

        var frames = await FrameLuma.ReadAsync(fx.Runner, Ffmpeg, output);
        var changes = FrameLuma.Transitions(frames);
        log.WriteLine($"region={region} segments={result.SegmentCount} clockTotal={totalClock:0.000}s video={actual:0.000}s frames={frames.Count}");
        Assert.True(changes.Count == Toggles,
            $"色の変化のコマが {Toggles} 個ではなく {changes.Count} 個でした（他のウィンドウが重なった・画面が無い等）。" +
            string.Join(", ", changes.Select(c => c.ToString("0.000", CultureInfo.InvariantCulture))));

        // 区間の時計の長さ。区間が 1 つなら停止後の経過時間、2 つなら一時停止の時点の経過時間で分ける。
        var clockLengths = withPause ? new List<double> { seg0Clock!.Value, totalClock - seg0Clock.Value } : [totalClock];
        var markers = result.Events?.Markers ?? [];
        log.WriteLine($"clockLengths=[{string.Join(", ", clockLengths.Select(c => c.ToString("0.000", CultureInfo.InvariantCulture)))}] default={RecordingTimeMap.DefaultAlignment}");
        log.WriteLine("i  seg  stamp(s)  change(s)  Start-err  End-err  default-marker-err  (err = mapped − change; stamp は色を変えて画面に出た直後)");
        var startErr = new List<double>();
        var endErr = new List<double>();
        var defaultErr = new List<double>();
        for (int i = 0; i < Toggles; i++)
        {
            var s = stamps[i];
            double change = changes[i];
            double? st = null, en = null;
            if (!withPause)
            {
                st = RecordingTimeMap.ToVideoTime(0, s.After, clockLengths, [actual], [true], RecordingClockAlignment.Start, 0);
                en = RecordingTimeMap.ToVideoTime(0, s.After, clockLengths, [actual], [true], RecordingClockAlignment.End);
            }
            else if (s.Segment == 0)
            {
                st = s.After; // 区間 0 の先頭合わせは区間の長さに関係しない
            }
            double? mk = i < markers.Count ? markers[i] : null;
            if (st is { } a) startErr.Add(a - change);
            if (en is { } b) endErr.Add(b - change);
            if (mk is { } m) defaultErr.Add(m - change);
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i}  {s.Segment}  {s.After,8:0.000}  {change,8:0.000}  {Fmt(st - change)}  {Fmt(en - change)}  {Fmt(mk - change)}   (stamp の幅 {(s.After - s.Before) * 1000:0}ms)"));
        }
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mean err: Start={Mean(startErr)} End={Mean(endErr)} default(markers)={Mean(defaultErr)}; clockTotal−video={totalClock - actual:0.000}s"));

        // 測定が目的。既定の合わせ方（停止時に記録係が作るマーカー）の誤差が大きく外れていないことだけを確かめる。
        Assert.Equal(Toggles, markers.Count);
        Assert.All(defaultErr, e => Assert.InRange(e, -0.6, 0.6));
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("+0.000;-0.000", CultureInfo.InvariantCulture).PadLeft(8) : "       -";
    private static string Mean(List<double> v) => v.Count == 0 ? "-" : v.Average().ToString("+0.000;-0.000", CultureInfo.InvariantCulture);

    private readonly record struct Stamp(int Segment, double Before, double After);

    /// <summary>色が黒 ⇔ 白に変わるだけの小窓（専用の STA スレッド）。</summary>
    private sealed class ColourWindow : IDisposable
    {
        private Window _window = null!;
        private Dispatcher _dispatcher = null!;
        private Thread _thread = null!;

        public static ColourWindow Show()
        {
            var w = new ColourWindow();
            using var ready = new ManualResetEventSlim();
            w._thread = new Thread(() =>
            {
                w._window = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                    ShowActivated = false, Left = 0, Top = 0, Width = 360, Height = 360, Background = Brushes.Black,
                    Title = "MVS time alignment test",
                };
                w._window.Show();
                w._dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            });
            w._thread.SetApartmentState(ApartmentState.STA);
            w._thread.IsBackground = true;
            w._thread.Start();
            ready.Wait(TimeSpan.FromSeconds(10));
            w.WaitForScreen();
            Thread.Sleep(300);
            return w;
        }

        public CaptureRegion PhysicalRect() => _dispatcher.Invoke(() =>
        {
            GetWindowRect(new WindowInteropHelper(_window).Handle, out var r);
            return new CaptureRegion(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        });

        /// <summary>色を変え、描画と画面の合成（DWM）が終わるまで待つ。</summary>
        public void SetWhite(bool white)
        {
            _dispatcher.Invoke(() => _window.Background = white ? Brushes.White : Brushes.Black);
            WaitForScreen();
        }

        private void WaitForScreen()
        {
            _dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            _dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            DwmFlush();
            DwmFlush();
        }

        public void Dispose()
        {
            _dispatcher.Invoke(() => _window.Close());
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}

/// <summary>動画のコマごとの明るさ（全体を 1 画素に縮めた値）と時刻を読み、明暗が切り替わったコマの時刻を求める（実測の解析用）。</summary>
public static partial class FrameLuma
{
    public readonly record struct Frame(double Time, byte Luma);

    public static async Task<List<Frame>> ReadAsync(ProcessRunner runner, string ffmpeg, string video)
    {
        var raw = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + ".gray");
        var times = new List<double>();
        var r = await runner.RunAsync(new ProcessRequest(ffmpeg,
        [
            "-hide_banner", "-nostdin", "-y", "-i", video,
            "-vf", "scale=1:1:flags=area,format=gray,showinfo", "-fps_mode", "passthrough", "-f", "rawvideo", raw,
        ])
        {
            OnStandardErrorLine = line =>
            {
                var m = PtsTime().Match(line);
                if (m.Success) lock (times) times.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            },
        });
        Assert.True(r.Succeeded, r.ErrorTail());
        var bytes = await File.ReadAllBytesAsync(raw);
        Assert.Equal(times.Count, bytes.Length);
        return bytes.Select((b, i) => new Frame(times[i], b)).ToList();
    }

    /// <summary>
    /// 明（128 以上）⇔ 暗が切り替わった時刻（秒）。変化はその前のコマとこのコマの間に起きたため、間の中点を返す。
    /// </summary>
    public static List<double> Transitions(IReadOnlyList<Frame> frames)
    {
        var list = new List<double>();
        for (int i = 1; i < frames.Count; i++)
            if ((frames[i].Luma >= 128) != (frames[i - 1].Luma >= 128))
                list.Add((frames[i - 1].Time + frames[i].Time) / 2);
        return list;
    }

    [GeneratedRegex(@"pts_time:\s*([0-9.]+)")]
    private static partial Regex PtsTime();
}
