using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>マイクの選択肢（先頭は「既定のマイク」= Name が空）。</summary>
public sealed record MicrophoneOption(string Name, string Label);

/// <summary>録画の結果（録画画面を閉じた後の主画面の動作）。</summary>
public sealed record RecordingOutcome(string OutputPath, RecordingInfo Info, bool StartEditing);

/// <summary>録画終了画面での選択（編集を開始するか・プロジェクト名。名前は空でもよい）。</summary>
public sealed record RecordingDoneChoice(bool StartEditing, string ProjectName);

/// <summary>停止した直後の確認画面に出す内容（録画ファイルができる前に分かる範囲）。</summary>
public sealed record RecordingStopSummary(RecordingInfo Info, TimeSpan Duration, int SegmentCount);

/// <summary>停止した直後の選択（v1.6.1）。録画に失敗した場合に、保存せずに撮り直せるようにする。</summary>
public enum RecordingStopChoice
{
    /// <summary>保存する（録画の完了画面へ進む）。</summary>
    Save,
    /// <summary>録画ファイルを破棄して録画画面に戻る（同じ設定で録り直す）。</summary>
    Retake,
    /// <summary>録画ファイルを破棄して録画画面を閉じる。</summary>
    DiscardAndClose,
}

/// <summary>
/// 録画画面。録画の設定・対象の選択と、録画中の状態（経過時間・一時停止）を持つ。
/// ffmpeg の実行は Core の RecordingService、ウィンドウ・モニター・マイクの列挙は Services が行い、ここには FFmpeg のコマンドを書かない。
/// 枠・カウントダウン・コントローラーの表示は View（RecorderWindow）が状態を見て切り替える。
/// </summary>
public sealed partial class RecorderViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IRecordingService _recorder;
    private readonly ICaptureTargetService _targets;
    private readonly IAudioInputDeviceService _audio;
    private readonly FFmpegStatus _ffmpeg;
    private readonly IDialogService _dialogs;
    private readonly ILogger<RecorderViewModel> _logger;
    private IReadOnlyList<AudioInputDevice> _devices = [];
    private CaptureRegion _recordingRegion;
    private RecordingInfo? _recordingInfo;

    public RecorderViewModel(ISettingsService settings, IRecordingService recorder, ICaptureTargetService targets,
        IAudioInputDeviceService audio, FFmpegStatus ffmpeg, IDialogService dialogs, ILogger<RecorderViewModel> logger)
    {
        _settings = settings;
        _recorder = recorder;
        _targets = targets;
        _audio = audio;
        _ffmpeg = ffmpeg;
        _dialogs = dialogs;
        _logger = logger;
        var s = settings.Current.Recording;
        _mode = s.Mode;
        _fps = RecordingSettings.FpsPresets.Contains(s.Fps) ? s.Fps : RecordingSettings.DefaultFps;
        _drawCursor = s.DrawCursor;
        // マイク録音は毎回 OFF から始める（設定に ON が残っていても使わない）。
        _microphoneEnabled = false;
        _countdown = s.Countdown;
        _outputDirectory = s.ResolveOutputDirectory();
        _sizePreset = Enum.IsDefined(s.RegionSizePreset) ? s.RegionSizePreset : RegionSizePreset.Free;
        _orientation = Enum.IsDefined(s.RegionOrientation) ? s.RegionOrientation : RegionOrientation.Landscape;
        _recorder.StateChanged += (_, _) => RunOnUi(OnRecorderStateChanged);
        _recorder.Interrupted += (_, detail) => RunOnUi(() => _ = OnInterruptedAsync(detail));
        RefreshTargets();
        InitRegion(s);
    }

    /// <summary>録画中の UI（枠・コントローラー・カウントダウン）を View に切り替えさせる。</summary>
    public event EventHandler? RecordingUiChanged;
    /// <summary>カウントダウン（秒数）を View に表示させる。完了したら true、取り消されたら false。</summary>
    public Func<int, CaptureRegion, Task<bool>>? CountdownHandler { get; set; }
    /// <summary>
    /// 録画終了後の選択（編集を開始 / 保存して終了・プロジェクト名）を View に尋ねる（v1.4.1）。
    /// 停止した直後に呼び、View は録画ファイルができる（引数のタスクが終わる）まで待ってから選ばせる。
    /// 録画ファイルを作れなかった場合は null（エラーはこの ViewModel が表示する）。
    /// </summary>
    public Func<Task<RecordingResult>, RecordingInfo, RecordingDoneChoice?>? AskStartEditing { get; set; }
    /// <summary>
    /// 停止した直後に、保存するか・破棄して録り直すか・破棄して閉じるかを View に尋ねる（v1.6.1）。
    /// 録画ファイルの作成（区間の連結）は裏で進める。未設定なら保存する。
    /// </summary>
    public Func<RecordingStopSummary, RecordingStopChoice>? AskAfterStop { get; set; }
    /// <summary>破棄した録画ファイルを消す（既定はごみ箱へ移動。試験で差し替える）。</summary>
    public Action<string> DiscardFile { get; set; } = path =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    /// <summary>画面を閉じる（結果、中止なら null）。</summary>
    public event Action<RecordingOutcome?>? CloseRequested;

    public IReadOnlyList<int> FpsOptions => RecordingSettings.FpsPresets;
    public ObservableCollection<CaptureWindow> Windows { get; } = [];
    public ObservableCollection<CaptureMonitor> Monitors { get; } = [];
    public ObservableCollection<MicrophoneOption> Microphones { get; } = [];

    [ObservableProperty] private RecordingMode _mode;
    [ObservableProperty] private int _fps;
    [ObservableProperty] private bool _drawCursor;
    [ObservableProperty] private bool _microphoneEnabled;
    [ObservableProperty] private MicrophoneOption? _selectedMicrophone;
    [ObservableProperty] private string _microphoneMessage = "";
    [ObservableProperty] private bool _countdown;
    [ObservableProperty] private string _outputDirectory;
    [ObservableProperty] private CaptureWindow? _selectedWindow;
    [ObservableProperty] private CaptureMonitor? _selectedMonitor;
    /// <summary>範囲（物理 px）。</summary>
    [ObservableProperty] private CaptureRegion _region;
    [ObservableProperty] private string _regionWidthText = "";
    [ObservableProperty] private string _regionHeightText = "";
    /// <summary>大きさの種類（v1.16）。規格サイズなら縦横比を固定する。枠のドラッグでは変わらず、数値入力の［適用］で自由サイズになる。</summary>
    [ObservableProperty] private RegionSizePreset _sizePreset;
    /// <summary>向き（v1.16）。自由サイズの間は操作できないが、最後に選んだ向きを保持する。</summary>
    [ObservableProperty] private RegionOrientation _orientation;
    [ObservableProperty] private RecordingState _state = RecordingState.Idle;
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isCountingDown;
    /// <summary>停止してから録画完了の選択が終わるまで（録画画面・枠を出さず、ショートカットも受け付けない）。</summary>
    [ObservableProperty] private bool _isFinishing;

    public bool IsRegionMode => Mode == RecordingMode.Region;
    public bool IsWindowMode => Mode == RecordingMode.Window;
    public bool IsMonitorMode => Mode == RecordingMode.Monitor;
    public bool IsBusy => State is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping
        || IsCountingDown || IsFinishing;
    public bool IsIdle => !IsBusy;
    public bool IsRecording => State == RecordingState.Recording;
    public bool IsPaused => State == RecordingState.Paused;
    /// <summary>縦横比を固定しているか（規格サイズを選んでいる）。</summary>
    public bool IsAspectLocked => SizePreset != RegionSizePreset.Free;
    /// <summary>向きの切り替えができるか（規格サイズを選んでいて、録画前）。</summary>
    public bool CanChangeOrientation => IsAspectLocked && IsIdle;
    public bool IsPreset720 => SizePreset == RegionSizePreset.Hd720;
    public bool IsPreset1080 => SizePreset == RegionSizePreset.Hd1080;
    public bool IsFreeSize => SizePreset == RegionSizePreset.Free;
    public bool IsLandscape => Orientation == RegionOrientation.Landscape;
    public bool IsPortrait => Orientation == RegionOrientation.Portrait;
    /// <summary>規格サイズのボタンの表示（向きに合わせて 1280×720 / 720×1280 などに切り替える）。</summary>
    public string Preset720Label => PresetLabel(RegionSizePreset.Hd720);
    public string Preset1080Label => PresetLabel(RegionSizePreset.Hd1080);
    /// <summary>枠の上の表示（固定中は「1280×720・横16:9」、自由サイズは「1280×720・自由サイズ」）。</summary>
    public string FrameLabel => IsAspectLocked
        ? Loc.Format("Recorder.FrameLocked", Region.Width, Region.Height, Loc.Get(IsPortrait ? "Recorder.AspectPortrait" : "Recorder.AspectLandscape"))
        : Loc.Format("Recorder.FrameFree", Region.Width, Region.Height);

    private string PresetLabel(RegionSizePreset preset)
    {
        var (w, h) = RegionAspect.PresetSize(preset, Orientation);
        return Loc.Format("Recorder.SizeFormat", w, h);
    }
    public string PauseButtonText => IsPaused ? "▶ 再開" : "❚❚ 一時停止";
    public string StateText => State switch
    {
        RecordingState.Recording => "● REC",
        RecordingState.Paused => "❚❚ 一時停止中",
        RecordingState.Starting => "開始しています…",
        RecordingState.Stopping => "保存しています…",
        _ => "",
    };

    /// <summary>録画枠・対象を示す範囲（View の枠表示用。対象が無ければ null）。</summary>
    public CaptureRegion? PreviewRegion => Mode switch
    {
        RecordingMode.Region => Region,
        RecordingMode.Window => SelectedWindow is { } w ? _targets.GetWindowBounds(w.Handle) : null,
        RecordingMode.Monitor => SelectedMonitor?.Bounds,
        _ => null,
    };

    /// <summary>録画中の範囲（録画開始時に確定）。</summary>
    public CaptureRegion RecordingRegion => _recordingRegion;

    public CaptureRegion VirtualScreen => _targets.VirtualScreen();

    // ---------------- 初期化・対象 ----------------

    private void InitRegion(RecordingSettings s)
    {
        var screen = _targets.VirtualScreen();
        CaptureRegion region;
        if (s.RegionWidth >= RecordingSettings.MinRegionSize && s.RegionHeight >= RecordingSettings.MinRegionSize)
        {
            region = new CaptureRegion(s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight).ClampTo(screen);
        }
        else
        {
            var main = Monitors.FirstOrDefault(m => m.IsPrimary)?.Bounds ?? screen;
            int w = Math.Min(1280, main.Width), h = Math.Min(720, main.Height);
            region = new CaptureRegion(main.X + (main.Width - w) / 2, main.Y + (main.Height - h) / 2, w, h);
        }
        if (IsAspectLocked)
        {
            // 調整済みの大きさは規格サイズへ戻さない。条件（単位の整数倍・画面内）を満たさないときだけ比率を保って合わせる。
            Region = RegionAspect.Normalize(region, Orientation, screen);
        }
        else
        {
            SetRegion(region);
        }
    }

    [RelayCommand]
    private void RefreshTargets()
    {
        var oldWindow = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var w in _targets.ListWindows()) Windows.Add(w);
        SelectedWindow = Windows.FirstOrDefault(w => w.Handle == oldWindow) ?? Windows.FirstOrDefault();

        var oldMonitor = SelectedMonitor?.DeviceName ?? _settings.Current.Recording.MonitorDevice;
        Monitors.Clear();
        foreach (var m in _targets.ListMonitors()) Monitors.Add(m);
        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == oldMonitor) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
    }

    /// <summary>マイクの一覧を読み込む（画面を開いたとき）。</summary>
    public async Task LoadMicrophonesAsync()
    {
        Microphones.Clear();
        if (_ffmpeg.FfmpegPath is null)
        {
            MicrophoneMessage = "FFmpeg が見つからないためマイクを確認できません。";
            return;
        }
        try
        {
            _devices = await _audio.ListAsync(_ffmpeg.FfmpegPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone list failed");
            _devices = [];
        }
        var defaultName = _audio.GetDefaultDeviceName();
        if (_devices.Count == 0)
        {
            MicrophoneMessage = "マイクが見つかりません（マイクを接続すると選べます）。";
            SelectedMicrophone = null;
            return;
        }
        MicrophoneMessage = "";
        Microphones.Add(new MicrophoneOption("", defaultName is null ? "既定のマイク" : $"既定のマイク（{defaultName}）"));
        foreach (var d in _devices) Microphones.Add(new MicrophoneOption(d.Name, d.Name));
        var saved = _settings.Current.Recording.MicrophoneDevice;
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Name == saved) ?? Microphones[0];
    }

    public bool HasMicrophones => _devices.Count > 0;

    partial void OnModeChanged(RecordingMode value)
    {
        OnPropertyChanged(nameof(IsRegionMode));
        OnPropertyChanged(nameof(IsWindowMode));
        OnPropertyChanged(nameof(IsMonitorMode));
        NotifyPreview();
    }

    partial void OnSelectedWindowChanged(CaptureWindow? value) => NotifyPreview();
    partial void OnSelectedMonitorChanged(CaptureMonitor? value) => NotifyPreview();

    partial void OnMicrophoneEnabledChanged(bool value)
    {
        if (value && _devices.Count == 0)
        {
            MicrophoneEnabled = false;
            _dialogs.ShowWarning("マイクが見つかりません。マイクを接続してから［録画］画面を開き直してください。");
        }
    }

    partial void OnRegionChanged(CaptureRegion value)
    {
        OnPropertyChanged(nameof(FrameLabel));
        RegionWidthText = value.Width.ToString(CultureInfo.InvariantCulture);
        RegionHeightText = value.Height.ToString(CultureInfo.InvariantCulture);
        NotifyPreview();
    }

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(PreviewRegion));
        RecordingUiChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSizePresetChanged(RegionSizePreset value)
    {
        OnPropertyChanged(nameof(IsAspectLocked));
        OnPropertyChanged(nameof(CanChangeOrientation));
        OnPropertyChanged(nameof(IsPreset720));
        OnPropertyChanged(nameof(IsPreset1080));
        OnPropertyChanged(nameof(IsFreeSize));
        OnPropertyChanged(nameof(FrameLabel));
        NotifyPreview();
    }

    partial void OnOrientationChanged(RegionOrientation value)
    {
        OnPropertyChanged(nameof(IsLandscape));
        OnPropertyChanged(nameof(IsPortrait));
        OnPropertyChanged(nameof(Preset720Label));
        OnPropertyChanged(nameof(Preset1080Label));
        OnPropertyChanged(nameof(FrameLabel));
    }

    /// <summary>
    /// 自由サイズの範囲を設定する（画面内に収め、最小サイズ以上にする）。自由サイズのドラッグ・数値入力から呼ぶ。
    /// 縦横比の固定中は、幅・高さを別々に補正すると比率が崩れるため <see cref="SetLockedRegion"/> を使う。
    /// </summary>
    public void SetRegion(CaptureRegion region)
    {
        var screen = _targets.VirtualScreen();
        int min = RecordingSettings.MinRegionSize;
        region = new CaptureRegion(region.X, region.Y, Math.Max(min, region.Width), Math.Max(min, region.Height)).ClampTo(screen);
        if (region != Region) Region = region;
    }

    /// <summary>固定中の範囲を設定する。大きさは計算済み（単位の整数倍）として変えず、位置だけ画面内へ寄せる。</summary>
    private void SetLockedRegion(CaptureRegion region)
    {
        region = RegionAspect.ClampPosition(region, _targets.VirtualScreen());
        if (region != Region) Region = region;
    }

    /// <summary>
    /// 枠のつまみのドラッグ（View の RegionFrameWindow から呼ぶ）。start はドラッグ開始時の範囲、(dx, dy) はつまみの方向、
    /// (mx, my) はマウスの移動量（物理 px）。自由サイズは v1.15 までと同じ、固定中は比率を保つ。大きさの種類・固定状態は変えない。
    /// </summary>
    public void ResizeRegionByDrag(CaptureRegion start, int dx, int dy, int mx, int my)
    {
        if (!IsIdle) return;
        if (IsAspectLocked)
            SetLockedRegion(RegionAspect.Resize(start, dx, dy, mx, my, Orientation, _targets.VirtualScreen()));
        else
            SetRegion(RegionAspect.ResizeFree(start, dx, dy, mx, my));
    }

    /// <summary>枠の移動（上・中央のつまみ）。大きさは変えない。</summary>
    public void MoveRegionByDrag(CaptureRegion start, int mx, int my)
    {
        if (!IsIdle) return;
        var next = start with { X = start.X + mx, Y = start.Y + my };
        if (IsAspectLocked) SetLockedRegion(next);
        else SetRegion(next);
    }

    /// <summary>
    /// 大きさの種類を選ぶ（サイズのボタン）。規格サイズは今の枠の中心を保ってその大きさにし、縦横比を固定する
    /// （同じボタンをもう一度押すと、ドラッグで変えた大きさを規格サイズに戻せる）。自由サイズは枠をそのままにして固定だけ解除する。
    /// </summary>
    [RelayCommand]
    private void SelectSizePreset(RegionSizePreset preset)
    {
        if (!IsIdle) return;
        if (preset == RegionSizePreset.Free)
        {
            SizePreset = RegionSizePreset.Free;
            StatusText = Loc.Get("Recorder.Status.FreeSize");
            return;
        }
        // 今の枠の中心を保ち、枠のあるモニターに収まらなければ比率を保って縮める。
        var monitor = _targets.MonitorOf(Region).Bounds;
        var (region, shrunk) = RegionAspect.ApplyPreset(Region, preset, Orientation, monitor);
        SizePreset = preset;
        SetLockedRegion(region);
        StatusText = shrunk ? Loc.Format("Recorder.Status.PresetShrunk", Region.Width, Region.Height) : "";
    }

    /// <summary>
    /// 向きを切り替える（横 / 縦。規格サイズを選んでいるときだけ）。今の枠の幅と高さを入れ替えてすぐに枠へ反映し、
    /// 中心を保つ。枠のあるモニターに収まらなければ比率を保って縮め、位置を寄せる。
    /// </summary>
    [RelayCommand]
    private void SelectOrientation(RegionOrientation orientation)
    {
        if (!IsIdle || !IsAspectLocked || orientation == Orientation) return;
        var monitor = _targets.MonitorOf(Region).Bounds;
        var (region, shrunk) = RegionAspect.Rotate(Region, orientation, monitor);
        Orientation = orientation;
        SetLockedRegion(region);
        StatusText = shrunk ? Loc.Format("Recorder.Status.RotateShrunk", Region.Width, Region.Height) : "";
    }

    /// <summary>数値入力の適用（［適用］・Enter）。入力値を使う従来の処理で、縦横比の固定は解除して自由サイズにする（入力エラーでは解除しない）。</summary>
    [RelayCommand]
    private void ApplyRegionSize()
    {
        if (!IsIdle) return;
        if (!int.TryParse(RegionWidthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(RegionHeightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            StatusText = "幅と高さを整数で入力してください。";
            return;
        }
        SizePreset = RegionSizePreset.Free;
        SetRegion(Region with { Width = w, Height = h });
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var picked = _dialogs.PickFolder(Directory.Exists(OutputDirectory) ? OutputDirectory : null, "プロジェクトの保存先（この中にプロジェクトごとのフォルダーを作ります）");
        if (picked is not null) OutputDirectory = picked;
    }

    // ---------------- 録画 ----------------

    private bool CanStart() => IsIdle;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!IsIdle) return;
        if (_ffmpeg.FfmpegPath is null || _ffmpeg.FfprobePath is null)
        {
            _dialogs.ShowError("FFmpeg / ffprobe が見つかりません。［ツール］→［設定］で指定してください。");
            return;
        }
        if (_ffmpeg.Capabilities is { HasLibx264: false })
        {
            _dialogs.ShowError("この ffmpeg には libx264 が無いため録画できません。別の ffmpeg を指定してください。");
            return;
        }

        // 録画対象の範囲を確定する（ウィンドウは録画開始時の位置・大きさ）。
        CaptureRegion region;
        string target;
        switch (Mode)
        {
            case RecordingMode.Window:
                if (SelectedWindow is not { } win) { _dialogs.ShowWarning("録画するウィンドウを選んでください。"); return; }
                if (!_targets.BringToFront(win.Handle) || _targets.GetWindowBounds(win.Handle) is null)
                {
                    _dialogs.ShowWarning("選んだウィンドウが見つかりません（閉じられた可能性があります）。一覧を更新してください。");
                    RefreshTargets();
                    return;
                }
                await Task.Delay(300); // 最小化から戻る・前面に出るのを待つ
                if (_targets.GetWindowBounds(win.Handle) is not { } wb) { _dialogs.ShowWarning("ウィンドウの位置を取得できません。"); return; }
                region = wb;
                target = $"ウィンドウ: {win.Title}";
                break;
            case RecordingMode.Monitor:
                if (SelectedMonitor is not { } mon) { _dialogs.ShowWarning("録画するモニターを選んでください。"); return; }
                region = mon.Bounds;
                target = mon.Label.Trim();
                break;
            default:
                // 縦横比の固定中は、幅・高さを別々に切り詰めず、比率を保って画面内に収める（単位が偶数なので下の偶数化で変わらない）。
                region = IsAspectLocked ? RegionAspect.Normalize(Region, Orientation, _targets.VirtualScreen()) : Region;
                target = $"範囲 {region.Width}x{region.Height}";
                break;
        }
        // 画面の外にはみ出した部分は録画できないため切り詰め、H.264 用に幅・高さを偶数にする。
        region = region.ClampTo(_targets.VirtualScreen()).ToEvenSize();
        if (region.Width < 16 || region.Height < 16) { _dialogs.ShowWarning("録画する範囲が小さすぎます。"); return; }

        AudioInputSpec? audio = null;
        if (MicrophoneEnabled)
        {
            var device = _audio.Resolve(_devices, SelectedMicrophone?.Name ?? "", _audio.GetDefaultDeviceName());
            if (device is null)
            {
                _dialogs.ShowWarning("マイクが見つかりません。マイク録音を OFF にするか、マイクを接続してください。");
                return;
            }
            audio = AudioInputSpec.DirectShow(device.InputName, device.Name);
        }

        try { Directory.CreateDirectory(OutputDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _dialogs.ShowError("保存先フォルダーを作成できません。", ex.Message);
            return;
        }
        SaveSettings();

        _recordingRegion = region;
        var output = Path.Combine(OutputDirectory, RecordingSettings.BuildFileName(DateTime.Now));
        var info = new RecordingInfo
        {
            Mode = Mode, Fps = Fps, DrawCursor = DrawCursor, Microphone = audio is not null,
            MicrophoneDevice = audio?.DisplayName ?? "", Target = target,
            X = region.X, Y = region.Y, Width = region.Width, Height = region.Height, RecordedAt = DateTimeOffset.Now,
        };
        _recordingInfo = info;

        if (Countdown && CountdownHandler is { } countdown)
        {
            IsCountingDown = true;
            RaiseBusy();
            bool go;
            try { go = await countdown(3, region); }
            finally { IsCountingDown = false; RaiseBusy(); }
            if (!go) { StatusText = "録画を取り消しました。"; return; }
        }

        try
        {
            await _recorder.StartAsync(new RecordingStartRequest(_ffmpeg.FfmpegPath, _ffmpeg.FfprobePath,
                new ScreenRecordingOptions(region, Fps, DrawCursor, audio), output, info));
        }
        catch (RecordingException ex)
        {
            _logger.LogError(ex, "Recording start failed");
            _dialogs.ShowError(ex.Message, ex.Detail);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording start failed");
            _dialogs.ShowError("録画を開始できませんでした。", ex.Message);
            return;
        }
        StartTimer();
    }

    private bool CanPause() => State is RecordingState.Recording or RecordingState.Paused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseResumeAsync()
    {
        try
        {
            if (State == RecordingState.Recording) await _recorder.PauseAsync();
            else if (State == RecordingState.Paused) await _recorder.ResumeAsync();
        }
        catch (RecordingException ex)
        {
            _dialogs.ShowError(ex.Message + Environment.NewLine + "［停止］でそれまでの録画を保存できます。", ex.Detail);
        }
    }

    private bool CanStop() => State is RecordingState.Recording or RecordingState.Paused;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (!CanStop() || IsFinishing) return;
        // 停止した時点から選択が終わるまで、録画画面・枠を隠し、ショートカット（開始 / 一時停止）を無視する（v1.4.1）。
        IsFinishing = true;
        RaiseBusy();
        try
        {
            var info = _recordingInfo ?? new RecordingInfo();
            var saving = _recorder.StopAsync();
            // 停止の直後に保存するかを尋ねる（v1.6.1）。録画ファイルの作成はその間も裏で進める。
            // StopAsync は最初の待機の前に時計を止めるため、ここで録画した長さが分かる。
            var decision = AskAfterStop?.Invoke(new RecordingStopSummary(info, _recorder.Elapsed, _recorder.SegmentCount)) ?? RecordingStopChoice.Save;
            if (decision != RecordingStopChoice.Save)
            {
                _logger.LogInformation("Recording stop choice: {Choice}", decision);
                await DiscardAsync(saving);
                StopTimer();
                if (decision == RecordingStopChoice.DiscardAndClose)
                {
                    SaveSettings();
                    CloseRequested?.Invoke(null);
                }
                else
                {
                    ElapsedText = "00:00";
                    StatusText = "録画を破棄しました。［録画開始］でもう一度録画できます。";
                }
                return;
            }
            // 完了画面は保存を選んだ直後に出し、録画ファイルができるまで「保存しています」と表示して待つ（ボタンはその後で押せる）。
            var choice = AskStartEditing?.Invoke(saving, info);
            RecordingResult result;
            try
            {
                result = await saving;
            }
            catch (RecordingException ex)
            {
                _dialogs.ShowError(ex.Message, ex.Detail);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recording stop failed");
                _dialogs.ShowError("録画ファイルを作成できませんでした。", ex.Message);
                return;
            }
            finally
            {
                StopTimer();
            }
            choice ??= new RecordingDoneChoice(false, "");
            _logger.LogInformation("Recording done choice: {Choice}, project name \"{Name}\"", choice.StartEditing ? "start editing" : "save and close", choice.ProjectName);
            StatusText = $"保存しました: {result.OutputPath}";
            // 録画ファイルを保存先の直下に作った「日時_名前」のプロジェクトフォルダーへ移す（v1.4）。
            var path = result.OutputPath;
            try
            {
                path = ProjectFolders.MoveIntoNewFolder(path, OutputDirectory, result.Info.RecordedAt.LocalDateTime, choice.ProjectName);
                _logger.LogInformation("Moved recording into project folder: {Path}", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to move recording into project folder");
                _dialogs.ShowWarning($"録画ファイルをプロジェクトフォルダーへ移動できませんでした。保存先に残しています。{Environment.NewLine}{path}{Environment.NewLine}{ex.Message}");
            }
            CloseRequested?.Invoke(new RecordingOutcome(path, result.Info, choice.StartEditing));
        }
        finally
        {
            IsFinishing = false;
            RaiseBusy();
        }
    }

    /// <summary>
    /// 停止後に破棄を選んだ場合。録画ファイルができるのを待ってごみ箱へ移す（連結中に止めると区間の作業フォルダーが残るため待つ）。
    /// 作成に失敗した場合は、破棄するのでエラーを出さず、残した作業フォルダーも消す。
    /// </summary>
    private async Task DiscardAsync(Task<RecordingResult> saving)
    {
        RecordingResult result;
        try
        {
            result = await saving;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording save failed while discarding");
            await _recorder.AbortAsync();
            return;
        }
        try
        {
            DiscardFile(result.OutputPath);
            _logger.LogInformation("Discarded recording: {Path}", result.OutputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to discard recording {Path}", result.OutputPath);
            _dialogs.ShowWarning($"録画ファイルを削除できませんでした。不要な場合は手動で削除してください。{Environment.NewLine}{result.OutputPath}{Environment.NewLine}{ex.Message}");
        }
    }

    /// <summary>録画中に閉じる・アプリを終了する場合。録画を止めて途中のファイルを消す。</summary>
    public async Task AbortAsync()
    {
        StopTimer();
        await _recorder.AbortAsync();
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy) return;
        SaveSettings();
        CloseRequested?.Invoke(null);
    }

    /// <summary>グローバルショートカット（録画開始 / 停止）。</summary>
    public void ToggleStartStop()
    {
        if (IsFinishing) return;
        if (IsIdle) StartCommand.Execute(null);
        else if (CanStop()) StopCommand.Execute(null);
    }

    /// <summary>グローバルショートカット（一時停止 / 再開）。</summary>
    public void TogglePause()
    {
        if (IsFinishing) return;
        if (CanPause()) PauseResumeCommand.Execute(null);
    }

    private async Task OnInterruptedAsync(string detail)
    {
        if (State != RecordingState.Recording) return;
        _dialogs.ShowError("録画が中断されました（ffmpeg が終了しました）。それまでの録画を保存します。", detail);
        await StopAsync();
    }

    public void SaveSettings()
    {
        var s = _settings.Current.Recording;
        s.Mode = Mode;
        s.Fps = Fps;
        s.DrawCursor = DrawCursor;
        s.MicrophoneEnabled = MicrophoneEnabled;
        s.MicrophoneDevice = SelectedMicrophone?.Name ?? s.MicrophoneDevice;
        s.Countdown = Countdown;
        s.OutputDirectory = string.Equals(OutputDirectory, RecordingSettings.DefaultOutputDirectory(), StringComparison.OrdinalIgnoreCase) ? "" : OutputDirectory;
        s.RegionX = Region.X;
        s.RegionY = Region.Y;
        s.RegionWidth = Region.Width;
        s.RegionHeight = Region.Height;
        s.RegionSizePreset = SizePreset;
        s.RegionOrientation = Orientation;
        if (SelectedMonitor is not null) s.MonitorDevice = SelectedMonitor.DeviceName;
        try { _settings.Save(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Settings save failed"); }
    }

    // ---------------- 状態・経過時間 ----------------

    private System.Windows.Threading.DispatcherTimer? _timer;

    private void StartTimer()
    {
        _timer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var t = _recorder.Elapsed;
        ElapsedText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void OnRecorderStateChanged()
    {
        State = _recorder.State;
    }

    partial void OnStateChanged(RecordingState value)
    {
        RaiseBusy();
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(StateText));
        PauseResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RecordingUiChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanChangeOrientation));
        StartCommand.NotifyCanExecuteChanged();
        RecordingUiChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void RunOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
