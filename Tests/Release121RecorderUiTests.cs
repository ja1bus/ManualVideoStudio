using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>v1.21 波 2-b: 録画のショートカットの変換・表示、設定画面の検証、入力の監視、録画画面（LogInput・記録の保存・マーカー）。</summary>
public class Release121HotkeyMappingTests
{
    [Theory]
    [InlineData(HotkeyKey.F1, 0x70u)]
    [InlineData(HotkeyKey.F7, 0x76u)]
    [InlineData(HotkeyKey.F8, 0x77u)]
    [InlineData(HotkeyKey.F9, 0x78u)]
    [InlineData(HotkeyKey.F12, 0x7Bu)]
    [InlineData(HotkeyKey.A, 0x41u)]
    [InlineData(HotkeyKey.M, 0x4Du)]
    [InlineData(HotkeyKey.Z, 0x5Au)]
    [InlineData(HotkeyKey.D0, 0x30u)]
    [InlineData(HotkeyKey.D9, 0x39u)]
    public void KeysMapToVirtualKeyCodes(HotkeyKey key, uint vk) => Assert.Equal(vk, HotkeyMapping.ToVirtualKey(key));

    [Fact]
    public void EveryKeyHasADistinctCode()
    {
        var codes = Enum.GetValues<HotkeyKey>().Select(HotkeyMapping.ToVirtualKey).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void ModifiersMapToRegisterHotKeyFlags()
    {
        Assert.Equal(0x2u | 0x4u, HotkeyMapping.ToModifiers(HotkeyModifiers.CtrlShift));
        Assert.Equal(0x2u | 0x1u, HotkeyMapping.ToModifiers(HotkeyModifiers.CtrlAlt));
        Assert.Equal(0x2u | 0x1u | 0x4u, HotkeyMapping.ToModifiers(HotkeyModifiers.CtrlAltShift));
        // MOD_NOREPEAT は登録時に足す（変換には含めない）。
        Assert.All(Enum.GetValues<HotkeyModifiers>(), m => Assert.Equal(0u, HotkeyMapping.ToModifiers(m) & HotkeyMapping.ModNoRepeat));
    }
}

public sealed class Release121HotkeySettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mvs-hotkey-vm-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private (SettingsViewModel Vm, MainViewModelPhaseTests.FakeSettings Settings, MainViewModelPhaseTests.FakeDialogs Dialogs) Create(RecordingHotkeys? hotkeys = null)
    {
        var s = new MainViewModelPhaseTests.FakeSettings();
        if (hotkeys is not null) s.Current.Recording.Hotkeys = hotkeys;
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new MainViewModelPhaseTests.Unused(), s, NullLogger<FFmpegStatus>.Instance);
        var templates = new PromptTemplateService(Path.Combine(_dir, "prompt-templates.json"));
        var dialogs = new MainViewModelPhaseTests.FakeDialogs();
        return (new SettingsViewModel(s, ffmpeg, templates, dialogs, null!), s, dialogs);
    }

    [Fact]
    public void StartsFromSavedValuesAndOffersAllChoices()
    {
        var (vm, _, _) = Create(new RecordingHotkeys
        {
            StartStop = new(HotkeyModifiers.CtrlAlt, HotkeyKey.R),
            Pause = new(HotkeyModifiers.CtrlAlt, HotkeyKey.P),
            Marker = new(HotkeyModifiers.CtrlAltShift, HotkeyKey.D1),
        });
        Assert.Equal((HotkeyModifiers.CtrlAlt, HotkeyKey.R), (vm.StartStopModifiers, vm.StartStopKey));
        Assert.Equal((HotkeyModifiers.CtrlAlt, HotkeyKey.P), (vm.PauseModifiers, vm.PauseKey));
        Assert.Equal((HotkeyModifiers.CtrlAltShift, HotkeyKey.D1), (vm.MarkerModifiers, vm.MarkerKey));
        Assert.False(vm.HasHotkeyError);
        Assert.Equal(3, vm.HotkeyModifierOptions.Count);
        Assert.Equal(12 + 26 + 10, vm.HotkeyKeyOptions.Count);
        Assert.Contains(vm.HotkeyModifierOptions, c => c.Label == "Ctrl+Alt+Shift");
        Assert.Contains(vm.HotkeyKeyOptions, c => c.Value == HotkeyKey.D0 && c.Label == "0");
    }

    [Fact]
    public void DuplicatesShowAnErrorAndAreNotSaved()
    {
        var (vm, s, dialogs) = Create();
        vm.MarkerKey = HotkeyKey.F9; // 開始・停止と同じ Ctrl+Shift+F9
        Assert.True(vm.HasHotkeyError);
        Assert.Contains("重なっています", vm.HotkeyError);
        vm.SelectedTab = SettingsTab.Output;
        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;
        vm.OkCommand.Execute(null);
        Assert.Null(closed);
        Assert.False(vm.Saved);
        Assert.Equal(SettingsTab.Operation, vm.SelectedTab);
        Assert.Contains(dialogs.Warnings, w => w.Contains("重なっています"));
        Assert.Equal(RecordingHotkeys.DefaultMarker, s.Current.Recording.Hotkeys.Marker);

        // 修飾キーを変えれば重ならず、保存できる。
        vm.MarkerModifiers = HotkeyModifiers.CtrlAlt;
        Assert.False(vm.HasHotkeyError);
        vm.OkCommand.Execute(null);
        Assert.True(closed);
        Assert.Equal(new RecordingHotkey(HotkeyModifiers.CtrlAlt, HotkeyKey.F9), s.Current.Recording.Hotkeys.Marker);
        Assert.Equal(RecordingHotkeys.DefaultStartStop, s.Current.Recording.Hotkeys.StartStop);
    }

    [Fact]
    public void ResetRestoresTheDefaults()
    {
        var (vm, _, _) = Create(new RecordingHotkeys
        {
            StartStop = new(HotkeyModifiers.CtrlAlt, HotkeyKey.R),
            Pause = new(HotkeyModifiers.CtrlAlt, HotkeyKey.P),
            Marker = new(HotkeyModifiers.CtrlAlt, HotkeyKey.M),
        });
        vm.PauseKey = HotkeyKey.R; // 重なった状態からでも戻せる
        Assert.True(vm.HasHotkeyError);
        vm.ResetHotkeysCommand.Execute(null);
        Assert.False(vm.HasHotkeyError);
        Assert.Equal(RecordingHotkeys.DefaultStartStop, vm.StartStopHotkey);
        Assert.Equal(RecordingHotkeys.DefaultPause, vm.PauseHotkey);
        Assert.Equal(RecordingHotkeys.DefaultMarker, vm.MarkerHotkey);
    }

    [Fact]
    public void BrokenSavedValuesAreShownAsDefaults()
    {
        var (vm, _, _) = Create(new RecordingHotkeys
        {
            StartStop = new(HotkeyModifiers.CtrlShift, HotkeyKey.A),
            Pause = new(HotkeyModifiers.CtrlShift, HotkeyKey.A),
            Marker = new(HotkeyModifiers.CtrlShift, HotkeyKey.B),
        });
        Assert.Equal(RecordingHotkeys.DefaultStartStop, vm.StartStopHotkey);
        Assert.False(vm.HasHotkeyError);
    }
}

/// <summary>入力の監視の判定（偽の入力状態で 1 回ずつ調べる）。</summary>
public class Release121InputPollerTests
{
    private sealed class Clock : IRecordingClock
    {
        public bool Recording { get; set; } = true;
        public double Seconds { get; set; }
        public bool TryNow(out int segment, out double seconds)
        {
            segment = Recording ? 0 : -1;
            seconds = Seconds;
            return Recording;
        }
    }

    private sealed class Source : IInputStateSource
    {
        public InputSnapshot Next { get; set; } = new(false, false, true, 0, 0, 1);
        public InputSnapshot Read() => Next;
    }

    private static (InputPoller Poller, Source Src, Clock Clock, RecordingEventRecorder Rec) Create(bool logInput = true)
    {
        var clock = new Clock();
        var rec = new RecordingEventRecorder(clock, 100, 100, 640, 480, logInput);
        var src = new Source();
        var poller = new InputPoller(() => rec, src);
        poller.Poll(); // 最初の 1 回は基準にするだけ
        return (poller, src, clock, rec);
    }

    private static RecordingEvents Build(RecordingEventRecorder rec) => rec.Build([100], [100], [true], RecordingClockAlignment.Start, 0);

    [Fact]
    public void ButtonPressInsideTheRegionIsAClickAtVideoPixels()
    {
        var (p, src, clock, rec) = Create();
        src.Next = new(false, false, true, 150, 130, 1); // 入力の時刻が変わらない位置の変化は記録しない
        p.Poll();
        clock.Seconds = 2;
        src.Next = new(true, false, true, 150, 130, 2);
        p.Poll();
        clock.Seconds = 2.1;
        src.Next = new(true, false, true, 150, 130, 2); // 押したまま → 2 回目は数えない
        p.Poll();
        clock.Seconds = 3;
        src.Next = new(false, false, true, 150, 130, 3); // 離す → 入力の時刻は変わるがキーではない
        p.Poll();
        clock.Seconds = 4;
        src.Next = new(false, true, true, 160, 140, 4);
        p.Poll();
        var e = Build(rec);
        Assert.Equal(2, e.Clicks.Count);
        Assert.Equal((2.0, 50, 30, "left"), (e.Clicks[0].T, e.Clicks[0].X, e.Clicks[0].Y, e.Clicks[0].Button));
        Assert.Equal("right", e.Clicks[1].Button);
        Assert.Empty(e.KeyRanges);
        // 右クリックの前にカーソルが動いた分はマウス移動として残る。
        Assert.Equal(4.0, Assert.Single(e.MoveRanges).Start);
    }

    [Fact]
    public void InputWithoutCursorMoveIsKeyOrWheelAndMoveIsMouseMove()
    {
        var (p, src, clock, rec) = Create();
        clock.Seconds = 5;
        src.Next = new(false, false, true, 0, 0, 10); // 入力あり・カーソルは同じ → キー・ホイール
        p.Poll();
        clock.Seconds = 5.2;
        src.Next = new(false, false, true, 0, 0, 11);
        p.Poll();
        clock.Seconds = 9;
        src.Next = new(false, false, true, 30, 40, 12); // カーソルが動いた → マウス移動
        p.Poll();
        clock.Seconds = 12;
        src.Next = new(false, false, true, 30, 40, 12); // 入力の時刻が同じ → 何もしない
        p.Poll();
        var e = Build(rec);
        var key = Assert.Single(e.KeyRanges);
        Assert.Equal((5.0, 5.2), (key.Start, key.End));
        var move = Assert.Single(e.MoveRanges);
        Assert.Equal(9.0, move.Start);
        Assert.Empty(e.Clicks);
    }

    [Fact]
    public void NothingIsRecordedWhileNotRecordingOrWhenLogInputIsOff()
    {
        var (p, src, clock, rec) = Create();
        clock.Recording = false;
        src.Next = new(true, false, true, 150, 130, 2);
        p.Poll();
        Assert.Empty(Build(rec).Clicks);

        var (p2, src2, _, rec2) = Create(logInput: false);
        src2.Next = new(true, false, true, 150, 130, 2);
        p2.Poll();
        var e = Build(rec2);
        Assert.Empty(e.Clicks);
        Assert.Empty(e.KeyRanges);
        Assert.False(e.InputLogged);
    }

    [Fact]
    public void StartAndStopTheThreadSafely()
    {
        var src = new Source();
        using var poller = new InputPoller(() => null, src);
        poller.Start();
        poller.Start();
        Assert.True(poller.IsRunning);
        poller.Stop();
        poller.Stop();
        Assert.False(poller.IsRunning);
        poller.Start();
        poller.Dispose();
        Assert.False(poller.IsRunning);
        poller.Start(); // 破棄後は始めない
        Assert.False(poller.IsRunning);
    }
}

/// <summary>録画画面: LogInput を渡す・停止後に記録を保存する・ショートカットの表示・不要マーカー。</summary>
public sealed class Release121RecorderViewModelTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mvs-rec121-");

    public void Dispose() => _dir.Delete(true);

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public AppSettings Load() => Current;
        public void Save() { }
    }

    private sealed class Targets : ICaptureTargetService
    {
        public IReadOnlyList<CaptureWindow> ListWindows() => [];
        public IReadOnlyList<CaptureMonitor> ListMonitors() => [];
        public CaptureRegion VirtualScreen() => new(0, 0, 1920, 1080);
        public CaptureRegion? GetWindowBounds(IntPtr handle) => null;
        public bool BringToFront(IntPtr handle) => false;
        public CaptureMonitor MonitorOf(CaptureRegion region) => throw new NotSupportedException();
    }

    private sealed class Audio : IAudioInputDeviceService
    {
        public Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public string? GetDefaultDeviceName() => null;
        public AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName) => null;
    }

    private sealed class Info : IFFmpegInfoService
    {
        public Task<FFmpegCapabilities> DetectAsync(FFmpegLocation location, CancellationToken ct = default)
            => Task.FromResult(new FFmpegCapabilities { FfmpegVersion = "9.0", MajorVersion = 9, Encoders = new HashSet<string> { "libx264" } });
    }

    private sealed class Recorder(string output) : IRecordingService, IRecordingClock
    {
        public RecordingState State { get; private set; } = RecordingState.Idle;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(12);
        public int SegmentCount => 1;
        public RecordingStartRequest? Request { get; private set; }
        public RecordingEventRecorder? Events { get; set; }
        public RecordingEvents? ResultEvents { get; set; }
        public event EventHandler? StateChanged;
        public event EventHandler<string>? Interrupted { add { } remove { } }
        public double Now { get; set; } = 1.5;

        public bool TryNow(out int segment, out double seconds)
        {
            segment = 0;
            seconds = Now;
            return State == RecordingState.Recording;
        }

        private void Set(RecordingState s)
        {
            State = s;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(RecordingStartRequest request, CancellationToken ct = default)
        {
            Request = request;
            Events = new RecordingEventRecorder(this, request.Info, request.LogInput);
            Set(RecordingState.Recording);
            return Task.CompletedTask;
        }

        public Task PauseAsync() { Set(RecordingState.Paused); return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken ct = default) { Set(RecordingState.Recording); return Task.CompletedTask; }

        public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
        {
            Set(RecordingState.Stopping);
            await Task.Yield();
            await File.WriteAllBytesAsync(output, new byte[16], ct);
            Set(RecordingState.Completed);
            var events = ResultEvents ?? Events?.Build([12], [12], [true]);
            return new RecordingResult(output, TimeSpan.FromSeconds(12), 1,
                new RecordingInfo { RecordedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(9)) }, events);
        }

        public Task AbortAsync() { Set(RecordingState.Failed); return Task.CompletedTask; }
    }

    private async Task<(RecorderViewModel Vm, Settings S, Recorder R, List<RecordingOutcome?> Closed)> CreateAsync(Action<RecordingSettings>? configure = null)
    {
        var s = new Settings();
        var ffmpegExe = Path.Combine(_dir.FullName, "ffmpeg.exe");
        var ffprobeExe = Path.Combine(_dir.FullName, "ffprobe.exe");
        File.WriteAllBytes(ffmpegExe, [0]);
        File.WriteAllBytes(ffprobeExe, [0]);
        s.Current.FfmpegPath = ffmpegExe;
        s.Current.FfprobePath = ffprobeExe;
        s.Current.Recording.Countdown = false;
        configure?.Invoke(s.Current.Recording);
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new Info(), s, NullLogger<FFmpegStatus>.Instance);
        await ffmpeg.RefreshAsync();
        var r = new Recorder(Path.Combine(_dir.FullName, "rec_20261113_100000.mp4"));
        var closed = new List<RecordingOutcome?>();
        var vm = new RecorderViewModel(s, r, new Targets(), new Audio(), ffmpeg, new MainViewModelPhaseTests.FakeDialogs(), NullLogger<RecorderViewModel>.Instance)
        {
            OutputDirectory = Path.Combine(_dir.FullName, "out"),
            AskAfterStop = _ => RecordingStopChoice.Save,
            AskStartEditing = (_, _) => new RecordingDoneChoice(true, "手順"),
        };
        vm.CloseRequested += o => closed.Add(o);
        return (vm, s, r, closed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogInputIsPassedToTheRecorderAndSaved(bool logInput)
    {
        var (vm, s, r, _) = await CreateAsync(rs => rs.LogInput = !logInput);
        Assert.Equal(!logInput, vm.LogInput);
        vm.LogInput = logInput;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.NotNull(r.Request);
        Assert.Equal(logInput, r.Request!.LogInput);
        Assert.Equal(logInput, s.Current.Recording.LogInput);
    }

    [Fact]
    public async Task EventsAreSavedNextToTheVideoAfterItIsMovedIntoTheProjectFolder()
    {
        var (vm, _, r, closed) = await CreateAsync(rs => rs.LogInput = true);   // 既定は OFF。クリックも残すため ON にする
        vm.OutputDirectory = _dir.FullName;
        await vm.StartCommand.ExecuteAsync(null);
        r.Now = 3.25;
        Assert.Equal(1, vm.AddMarker());
        Assert.Equal("マーク 1", vm.MarkerText);
        r.Events!.AddClick(vm.RecordingRegion.X + 10, vm.RecordingRegion.Y + 20, "left");
        await vm.StopCommand.ExecuteAsync(null);

        var outcome = Assert.Single(closed);
        Assert.NotNull(outcome);
        Assert.EndsWith("_手順", Path.GetDirectoryName(outcome!.OutputPath));
        var saved = RecordingEventsFile.TryReadFor(outcome.OutputPath);
        Assert.NotNull(saved);
        Assert.Equal("rec_20261113_100000.mp4", saved!.VideoFileName);
        Assert.Equal(3.25 - RecordingTimeMap.CaptureLatencySeconds, Assert.Single(saved.Markers), 6);   // 既定は先頭合わせ＋最初のコマまでの遅れを引く
        Assert.Single(saved.Clicks);
        Assert.False(File.Exists(RecordingEventsFile.PathFor(Path.Combine(_dir.FullName, "rec_20261113_100000.mp4"))));
    }

    [Fact]
    public async Task NoEventsMeansNoFile()
    {
        var (vm, _, r, closed) = await CreateAsync();
        vm.OutputDirectory = _dir.FullName;
        await vm.StartCommand.ExecuteAsync(null);
        r.ResultEvents = null;
        r.Events = null; // 記録を作れなかった録画（RecordingResult.Events = null）を真似る。
        await vm.StopCommand.ExecuteAsync(null);
        var outcome = Assert.Single(closed);
        Assert.False(File.Exists(RecordingEventsFile.PathFor(outcome!.OutputPath)));
    }

    [Fact]
    public async Task MarkerIsIgnoredWhenNotRecording()
    {
        var (vm, _, _, _) = await CreateAsync();
        Assert.Null(vm.AddMarker());
        Assert.False(vm.MarkerCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.MarkerCommand.CanExecute(null));
        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.Null(vm.AddMarker());
        Assert.False(vm.MarkerCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShortcutTextsComeFromTheSettings()
    {
        var (def, _, _, _) = await CreateAsync();
        Assert.Contains("Ctrl+Shift+F9: 録画開始 / 停止", def.ShortcutHint);
        Assert.Contains("Ctrl+Shift+F8: 一時停止 / 再開", def.ShortcutHint);
        Assert.Contains("Ctrl+Shift+F7: 不要マーク", def.ShortcutHint);

        var (vm, _, _, _) = await CreateAsync(rs => rs.Hotkeys = new RecordingHotkeys
        {
            StartStop = new(HotkeyModifiers.CtrlAlt, HotkeyKey.R),
            Pause = new(HotkeyModifiers.CtrlAlt, HotkeyKey.P),
            Marker = new(HotkeyModifiers.CtrlAltShift, HotkeyKey.D5),
        });
        Assert.Equal("Ctrl+Alt+R", vm.StartStopHotkeyText);
        Assert.Contains("Ctrl+Alt+R: 録画開始 / 停止", vm.ShortcutHint);
        Assert.Contains("Ctrl+Alt+Shift+5", vm.ShortcutHint);
        Assert.Contains("(Ctrl+Alt+P)", vm.PauseToolTip);
        Assert.Contains("(Ctrl+Alt+R)", vm.StopToolTip);
        Assert.Contains("(Ctrl+Alt+Shift+5)", vm.MarkerToolTip);
        Assert.Equal("記録はフォルダーに保存します。", vm.LogInputNote);   // 説明は取扱説明書へ（2026-11-14 利用者の指示）
        Assert.DoesNotContain("F9", vm.ShortcutHint + vm.PauseToolTip + vm.StopToolTip);
    }
}
