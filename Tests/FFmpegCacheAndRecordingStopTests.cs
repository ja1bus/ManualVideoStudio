using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>v1.6.1: 前回見つけた ffmpeg の記録（パス・版・機能）と、PATH の探し方。</summary>
public class FFmpegCacheTests
{
    private sealed class FakeEnv(string[] paths, params string[] files) : IToolEnvironment
    {
        private readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string p) => _files.Contains(p);
        public string? GetEnvironmentVariable(string name) => name == "PATH" ? paths.FirstOrDefault() : null;
        public IEnumerable<string?> GetPathVariables() => paths;
    }

    private static readonly FFmpegLocation Found = new(
        new ToolLocation(@"C:\ff\ffmpeg.exe", ToolSource.PathEnvironment, ""),
        new ToolLocation(@"C:\ff\ffprobe.exe", ToolSource.SameDirectoryAsFfmpeg, ""));

    private static readonly FFmpegCapabilities Caps = new()
    {
        FfmpegVersion = "9.0.2", FfprobeVersion = "9.0.2", MajorVersion = 9,
        Filters = new HashSet<string> { "subtitles", "palettegen", "paletteuse" },
        Encoders = new HashSet<string> { "libx264", "aac" },
    };

    private static readonly ToolFileStamp Stamp = new(1234, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void RememberedPathUsedBeforePath()
    {
        var env = new FakeEnv([@"C:\bin"], @"C:\bin\ffmpeg.exe", @"C:\bin\ffprobe.exe", @"D:\old\ffmpeg.exe", @"D:\old\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate("", "", @"D:\old\ffmpeg.exe", @"E:\x\ffprobe.exe");
        Assert.Equal(@"D:\old\ffmpeg.exe", loc.Ffmpeg.Path);
        Assert.Equal(ToolSource.Remembered, loc.Ffmpeg.Source);
        // ffprobe は ffmpeg と同じフォルダを先に探す（記録した別の場所より優先）。
        Assert.Equal(@"D:\old\ffprobe.exe", loc.Ffprobe.Path);
    }

    [Fact]
    public void MissingRememberedPathFallsBackToPath()
    {
        // winget の更新でフォルダーの名前（版）が変わった場合など。
        var env = new FakeEnv([@"C:\bin"], @"C:\bin\ffmpeg.exe", @"C:\bin\ffprobe.exe");
        var loc = new FFmpegLocator(env).Locate("", "", @"D:\old\ffmpeg.exe", @"D:\old\ffprobe.exe");
        Assert.Equal(@"C:\bin\ffmpeg.exe", loc.Ffmpeg.Path);
        Assert.Equal(ToolSource.PathEnvironment, loc.Ffmpeg.Source);
    }

    [Fact]
    public void UserSettingBeatsRemembered()
    {
        var env = new FakeEnv([], @"D:\user\ffmpeg.exe", @"D:\old\ffmpeg.exe");
        var loc = new FFmpegLocator(env).Locate(@"D:\user\ffmpeg.exe", "", @"D:\old\ffmpeg.exe");
        Assert.Equal(ToolSource.UserSetting, loc.Ffmpeg.Source);
    }

    [Fact]
    public void RememberedWithWrongNameIgnored()
    {
        var env = new FakeEnv([], @"D:\old\other.exe");
        Assert.False(new FFmpegLocator(env).Locate("", "", @"D:\old\other.exe").Ffmpeg.Found);
    }

    [Fact]
    public void LaterPathVariablesSearched()
    {
        // このプロセスの PATH に無くても、ユーザーの PATH（レジストリ）にあれば見つける。
        var env = new FakeEnv([@"C:\Windows", null!, @"C:\Users\u\winget\bin"], @"C:\Users\u\winget\bin\ffmpeg.exe");
        Assert.Equal(@"C:\Users\u\winget\bin\ffmpeg.exe", new FFmpegLocator(env).FindInPath("ffmpeg.exe"));
    }

    [Fact]
    public void CacheMatchesSameFiles()
    {
        var cache = FFmpegCache.Create(Found, Caps, Stamp, Stamp);
        var caps = cache.Match(Found, _ => Stamp);
        Assert.NotNull(caps);
        Assert.Equal("9.0.2", caps!.FfmpegVersion);
        Assert.Equal(9, caps.MajorVersion);
        Assert.True(caps.HasSubtitles);
        Assert.True(caps.HasGifPalette);
        Assert.True(caps.HasLibx264);
    }

    [Fact]
    public void CacheRejectsChangedOrMovedFiles()
    {
        var cache = FFmpegCache.Create(Found, Caps, Stamp, Stamp);
        Assert.Null(cache.Match(Found, _ => Stamp with { Length = 99 }));
        Assert.Null(cache.Match(Found, _ => Stamp with { LastWriteTimeUtc = Stamp.LastWriteTimeUtc.AddSeconds(1) }));
        Assert.Null(cache.Match(Found, _ => null));
        var moved = Found with { Ffmpeg = Found.Ffmpeg with { Path = @"D:\new\ffmpeg.exe" } };
        Assert.Null(cache.Match(moved, _ => Stamp));
        var noProbe = Found with { Ffprobe = new ToolLocation(null, ToolSource.NotFound, "") };
        Assert.Null(cache.Match(noProbe, _ => Stamp));
    }

    [Fact]
    public void CacheRoundTripsThroughSettingsJson()
    {
        var dir = Directory.CreateTempSubdirectory("mvs-ffcache-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var svc = new SettingsService(path);
            svc.Current.FfmpegCache = FFmpegCache.Create(Found, Caps, Stamp, Stamp);
            svc.Save();
            var loaded = new SettingsService(path).Load();
            Assert.NotNull(loaded.FfmpegCache);
            Assert.NotNull(loaded.FfmpegCache!.Match(Found, _ => Stamp));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void StampOfRealFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[10]);
            Assert.Equal(10, FFmpegCache.StampOf(file)!.Length);
            Assert.Null(FFmpegCache.StampOf(file + "-missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>v1.6.1: 録画を停止した直後の選択（保存する / 破棄して録り直す / 破棄して閉じる）。</summary>
public class RecordingStopChoiceTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Messages { get; } = [];
        public void ShowInfo(string message, string title = "情報") => Messages.Add(message);
        public void ShowWarning(string message, string title = "注意") => Messages.Add(message);
        public void ShowError(string message, string? detail = null, string title = "エラー") => Messages.Add(message);
        public bool Confirm(string message, string title = "確認") => true;
        public bool? ConfirmYesNoCancel(string message, string title = "確認") => false;
        public string? OpenFile(string filter, string? initialDirectory = null, string title = "ファイルを開く") => null;
        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null, string title = "名前を付けて保存") => null;
        public string? PickFolder(string? initialDirectory = null, string title = "フォルダーの選択") => null;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public AppSettings Load() => Current;
        public void Save() { }
    }

    private sealed class FakeTargets : ICaptureTargetService
    {
        private static readonly CaptureRegion Screen = new(0, 0, 1920, 1080);
        public IReadOnlyList<CaptureWindow> ListWindows() => [];
        public IReadOnlyList<CaptureMonitor> ListMonitors() => [];
        public CaptureRegion VirtualScreen() => Screen;
        public CaptureRegion? GetWindowBounds(IntPtr handle) => null;
        public bool BringToFront(IntPtr handle) => false;
        public CaptureMonitor MonitorOf(CaptureRegion region) => throw new NotSupportedException();
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

    /// <summary>録画中の状態から停止する偽の録画。停止で outputPath のファイルを作る（null なら失敗）。</summary>
    private sealed class FakeRecorder(string? outputPath) : IRecordingService
    {
        public RecordingState State { get; private set; } = RecordingState.Idle;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(12);
        public int SegmentCount => 1;
        public int AbortCalls { get; private set; }
        public event EventHandler? StateChanged;
        public event EventHandler<string>? Interrupted { add { } remove { } }

        public void BeginRecording() => SetState(RecordingState.Recording);

        private void SetState(RecordingState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task StartAsync(RecordingStartRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
        {
            await Task.Yield();
            if (outputPath is null)
            {
                SetState(RecordingState.Failed);
                throw new RecordingException("録画ファイルを作成できませんでした。");
            }
            await File.WriteAllBytesAsync(outputPath, new byte[16], ct);
            SetState(RecordingState.Completed);
            return new RecordingResult(outputPath, TimeSpan.FromSeconds(12), 1, new RecordingInfo { RecordedAt = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.FromHours(9)) });
        }

        public Task AbortAsync()
        {
            AbortCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("mvs-recstop-");
        public FakeDialogs Dialogs { get; } = new();
        public FakeRecorder Recorder { get; }
        public RecorderViewModel Vm { get; }
        public List<string> Discarded { get; } = [];
        public List<RecordingOutcome?> Closed { get; } = [];
        public int DoneWindowCalls { get; private set; }

        public Harness(RecordingStopChoice choice, bool fail = false)
        {
            Recorder = new FakeRecorder(fail ? null : Path.Combine(Dir.FullName, "rec_20260924_100000.mp4"));
            var settings = new FakeSettings();
            var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new FakeInfo(), settings, NullLogger<FFmpegStatus>.Instance);
            Vm = new RecorderViewModel(settings, Recorder, new FakeTargets(), new FakeAudio(), ffmpeg, Dialogs, NullLogger<RecorderViewModel>.Instance)
            {
                OutputDirectory = Dir.FullName,
                AskAfterStop = _ => choice,
                AskStartEditing = (saving, _) =>
                {
                    DoneWindowCalls++;
                    return new RecordingDoneChoice(true, "手順");
                },
            };
            Vm.DiscardFile = p =>
            {
                Discarded.Add(p);
                File.Delete(p);
            };
            Vm.CloseRequested += o => Closed.Add(o);
            Recorder.BeginRecording();
        }

        public void Dispose() => Dir.Delete(true);
    }

    [Fact]
    public async Task SaveGoesToDoneWindowAndProjectFolder()
    {
        using var h = new Harness(RecordingStopChoice.Save);
        await h.Vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(1, h.DoneWindowCalls);
        Assert.Empty(h.Discarded);
        var outcome = Assert.Single(h.Closed);
        Assert.NotNull(outcome);
        Assert.True(outcome!.StartEditing);
        Assert.True(File.Exists(outcome.OutputPath));
        Assert.EndsWith("_手順", Path.GetDirectoryName(outcome.OutputPath));
        Assert.False(h.Vm.IsFinishing);
    }

    [Fact]
    public async Task RetakeDiscardsAndStaysOpen()
    {
        using var h = new Harness(RecordingStopChoice.Retake);
        await h.Vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(0, h.DoneWindowCalls);
        var discarded = Assert.Single(h.Discarded);
        Assert.False(File.Exists(discarded));
        Assert.Empty(h.Closed);
        Assert.False(h.Vm.IsFinishing);
        Assert.True(h.Vm.IsIdle);
        Assert.Contains("破棄しました", h.Vm.StatusText);
        Assert.Equal("00:00", h.Vm.ElapsedText);
        // プロジェクトフォルダーを作らない。
        Assert.Empty(h.Dir.GetDirectories());
    }

    [Fact]
    public async Task DiscardAndCloseClosesWithoutOutcome()
    {
        using var h = new Harness(RecordingStopChoice.DiscardAndClose);
        await h.Vm.StopCommand.ExecuteAsync(null);
        Assert.Single(h.Discarded);
        Assert.Null(Assert.Single(h.Closed));
        Assert.Equal(0, h.DoneWindowCalls);
        Assert.Empty(h.Dir.GetDirectories());
    }

    [Fact]
    public async Task DiscardAfterFailedSaveCleansUpWithoutError()
    {
        using var h = new Harness(RecordingStopChoice.Retake, fail: true);
        await h.Vm.StopCommand.ExecuteAsync(null);
        Assert.Empty(h.Discarded);
        Assert.Equal(1, h.Recorder.AbortCalls);
        Assert.Empty(h.Dialogs.Messages);
        Assert.Empty(h.Closed);
        Assert.True(h.Vm.IsIdle);
    }

    [Fact]
    public async Task DiscardFailureShowsFullPath()
    {
        using var h = new Harness(RecordingStopChoice.Retake);
        h.Vm.DiscardFile = _ => throw new IOException("使用中");
        await h.Vm.StopCommand.ExecuteAsync(null);
        var message = Assert.Single(h.Dialogs.Messages);
        Assert.Contains(Path.Combine(h.Dir.FullName, "rec_20260924_100000.mp4"), message);
    }
}
