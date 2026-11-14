namespace ManualVideoStudio.Core.Models;

/// <summary>画面録画の対象の種類。</summary>
public enum RecordingMode
{
    /// <summary>画面上の範囲（枠で指定）。</summary>
    Region,
    /// <summary>ウィンドウ（HWND で識別し、録画開始時のウィンドウの位置・大きさで録画する）。</summary>
    Window,
    /// <summary>モニター全体。</summary>
    Monitor,
}

/// <summary>範囲指定の大きさの種類（v1.16）。規格サイズでは縦横比を固定する（ピクセル数は固定しない）。</summary>
public enum RegionSizePreset
{
    /// <summary>自由サイズ（縦横比を固定しない。v1.15 までの動作・旧設定の既定）。</summary>
    Free,
    /// <summary>1280x720（縦は 720x1280）を起点に縦横比を固定する。</summary>
    Hd720,
    /// <summary>1920x1080（縦は 1080x1920）を起点に縦横比を固定する。</summary>
    Hd1080,
}

/// <summary>範囲指定の向き（v1.16）。横 = 16:9、縦 = 9:16。自由サイズの間も最後に選んだ向きを保持する。</summary>
public enum RegionOrientation
{
    Landscape,
    Portrait,
}

/// <summary>
/// 画面録画の設定。アプリ設定（settings.json の recording）に保存し、録画から作ったプロジェクトにも参考として記録する。
/// マイク録音は意図した操作でのみ有効にするため、設定を読み込むたびに OFF へ戻す（<see cref="NormalizeOnLoad"/>）。
/// </summary>
public sealed class RecordingSettings
{
    public const int DefaultFps = 30;
    /// <summary>画面に出す FPS の選択肢。</summary>
    public static readonly IReadOnlyList<int> FpsPresets = [15, 30];
    /// <summary>範囲指定の最小サイズ（px）。</summary>
    public const int MinRegionSize = 64;

    public RecordingMode Mode { get; set; } = RecordingMode.Region;
    public int Fps { get; set; } = DefaultFps;
    /// <summary>マウスカーソルを録画に含める。</summary>
    public bool DrawCursor { get; set; } = true;
    /// <summary>マイク録音。既定 OFF。読み込み時は常に OFF に戻す。</summary>
    public bool MicrophoneEnabled { get; set; }
    /// <summary>選んだマイクの名前。空なら Windows の既定のマイク。</summary>
    public string MicrophoneDevice { get; set; } = "";
    /// <summary>録画開始前の 3 秒カウントダウン。</summary>
    public bool Countdown { get; set; } = true;
    /// <summary>録画ファイルの保存先。空なら「ビデオ\ManualVideoStudio」。</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>範囲指定の位置・大きさ（仮想スクリーンの物理 px）。幅・高さが 0 なら未指定（主モニターの中央に 1280x720）。</summary>
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }
    /// <summary>最後に選んだ大きさの種類（v1.16）。旧設定には無く、自由サイズとして読む（保存済みの幅・高さから推定しない）。</summary>
    public RegionSizePreset RegionSizePreset { get; set; } = RegionSizePreset.Free;
    /// <summary>最後に選んだ向き（v1.16）。旧設定には無く、横として読む。</summary>
    public RegionOrientation RegionOrientation { get; set; } = RegionOrientation.Landscape;
    /// <summary>最後に選んだモニターのデバイス名（\\.\DISPLAY1 など）。</summary>
    public string MonitorDevice { get; set; } = "";

    /// <summary>
    /// 録画中のクリック位置と入力の時刻を記録する（v1.21。既定 OFF。2026-11-14 利用者決定: 利用者が選んで ON にする）。
    /// キーの種類・内容は記録しない。記録は録画フォルダーの「動画名.events.json」に置き、PC の外へは送らない。
    /// 不要マーカーはこの設定に関わらず記録する。
    /// </summary>
    public bool LogInput { get; set; }

    /// <summary>録画のショートカット（v1.21。旧設定には無く、既定値で読む）。</summary>
    public RecordingHotkeys Hotkeys { get; set; } = new();

    /// <summary>設定ファイルから読んだ値を整える。マイク録音は常に OFF にする（前回 ON でも自動では ON にしない）。</summary>
    public void NormalizeOnLoad()
    {
        MicrophoneEnabled = false;
        if (!FpsPresets.Contains(Fps)) Fps = DefaultFps;
        if (!Enum.IsDefined(Mode)) Mode = RecordingMode.Region;
        if (!Enum.IsDefined(RegionSizePreset)) RegionSizePreset = RegionSizePreset.Free;
        if (!Enum.IsDefined(RegionOrientation)) RegionOrientation = RegionOrientation.Landscape;
        MicrophoneDevice ??= "";
        OutputDirectory ??= "";
        MonitorDevice ??= "";
        Hotkeys ??= new RecordingHotkeys();
        Hotkeys.Normalize();
        if (RegionWidth < MinRegionSize || RegionHeight < MinRegionSize)
        {
            RegionWidth = 0;
            RegionHeight = 0;
        }
    }

    public RecordingSettings Clone()
    {
        var c = (RecordingSettings)MemberwiseClone();
        c.Hotkeys = (Hotkeys ?? new RecordingHotkeys()).Clone();
        return c;
    }

    /// <summary>既定の保存先（ビデオ\ManualVideoStudio）。</summary>
    public static string DefaultOutputDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ManualVideoStudio");

    public string ResolveOutputDirectory()
        => string.IsNullOrWhiteSpace(OutputDirectory) ? DefaultOutputDirectory() : OutputDirectory;

    /// <summary>録画ファイル名（rec_yyyyMMdd_HHmmss.mp4）。</summary>
    public static string BuildFileName(DateTime time) => $"rec_{time:yyyyMMdd_HHmmss}.mp4";
}

/// <summary>録画のショートカットの修飾キー（v1.21）。誤操作を避けるため、2 つ以上の組み合わせだけを選べる。</summary>
public enum HotkeyModifiers
{
    CtrlShift,
    CtrlAlt,
    CtrlAltShift,
}

/// <summary>録画のショートカットのキー（v1.21）。F1～F12・A～Z・0～9。仮想キーコードへの変換は Services 側。</summary>
public enum HotkeyKey
{
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
}

/// <summary>録画のショートカット 1 つ（v1.21）。</summary>
public sealed record RecordingHotkey(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    /// <summary>画面に出す文字（例: Ctrl+Shift+F9）。</summary>
    public string DisplayText => $"{ModifiersText(Modifiers)}+{KeyText(Key)}";

    public static string ModifiersText(HotkeyModifiers m) => m switch
    {
        HotkeyModifiers.CtrlAlt => "Ctrl+Alt",
        HotkeyModifiers.CtrlAltShift => "Ctrl+Alt+Shift",
        _ => "Ctrl+Shift",
    };

    public static string KeyText(HotkeyKey k) => k is >= HotkeyKey.D0 and <= HotkeyKey.D9
        ? ((int)k - (int)HotkeyKey.D0).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : k.ToString();

    public bool IsValid => Enum.IsDefined(Modifiers) && Enum.IsDefined(Key);

    public override string ToString() => DisplayText;
}

/// <summary>
/// 録画のショートカット（v1.21）。録画開始・停止、一時停止・再開、不要マーカーの 3 つを設定で選べる。
/// 既定は従来と同じ Ctrl+Shift+F9 / Ctrl+Shift+F8 と、マーカーの Ctrl+Shift+F7。
/// </summary>
public sealed class RecordingHotkeys
{
    public static readonly RecordingHotkey DefaultStartStop = new(HotkeyModifiers.CtrlShift, HotkeyKey.F9);
    public static readonly RecordingHotkey DefaultPause = new(HotkeyModifiers.CtrlShift, HotkeyKey.F8);
    public static readonly RecordingHotkey DefaultMarker = new(HotkeyModifiers.CtrlShift, HotkeyKey.F7);

    public RecordingHotkey StartStop { get; set; } = DefaultStartStop;
    public RecordingHotkey Pause { get; set; } = DefaultPause;
    public RecordingHotkey Marker { get; set; } = DefaultMarker;

    /// <summary>3 つが互いに違うか（同じ組み合わせは保存できない）。</summary>
    public static bool AreDistinct(RecordingHotkey a, RecordingHotkey b, RecordingHotkey c) => a != b && a != c && b != c;

    /// <summary>不正な値（null・範囲外）は既定へ戻す。重なりがあれば 3 つとも既定へ戻す（設定ファイルの手編集などの対策）。</summary>
    public void Normalize()
    {
        if (StartStop is not { IsValid: true }) StartStop = DefaultStartStop;
        if (Pause is not { IsValid: true }) Pause = DefaultPause;
        if (Marker is not { IsValid: true }) Marker = DefaultMarker;
        if (!AreDistinct(StartStop, Pause, Marker))
        {
            StartStop = DefaultStartStop;
            Pause = DefaultPause;
            Marker = DefaultMarker;
        }
    }

    public RecordingHotkeys Clone() => new() { StartStop = StartStop, Pause = Pause, Marker = Marker };
}

/// <summary>録画から作ったプロジェクトに残す記録（参考情報。読み込み時に設定へは反映しない）。</summary>
public sealed class RecordingInfo
{
    public RecordingMode Mode { get; set; }
    public int Fps { get; set; }
    public bool DrawCursor { get; set; }
    public bool Microphone { get; set; }
    public string MicrophoneDevice { get; set; } = "";
    /// <summary>録画対象の説明（「範囲 1280x720」「ウィンドウ: メモ帳」など）。</summary>
    public string Target { get; set; } = "";
    /// <summary>録画した範囲（仮想スクリーンの物理 px）。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>一時停止で分かれた区間の数（1 なら一時停止なし）。</summary>
    public int SegmentCount { get; set; } = 1;
    public DateTimeOffset RecordedAt { get; set; }
}
