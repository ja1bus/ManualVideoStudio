using ManualVideoStudio.Core.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Services.Recording;

/// <summary>
/// ある時点の入力の状態（v1.21）。Left / Right は論理ボタン（左右入れ替えの設定を反映済み）、X / Y はカーソルの物理 px、
/// LastInputTick は最後の入力の時刻（GetLastInputInfo）。キーの種類・内容は含まない。
/// </summary>
public readonly record struct InputSnapshot(bool Left, bool Right, bool HasCursor, int X, int Y, uint LastInputTick);

/// <summary>入力の状態の取得口（試験で差し替える）。</summary>
public interface IInputStateSource
{
    InputSnapshot Read();
}

/// <summary>Win32 から入力の状態を読む（フックは使わない。キーの種類は取得しない）。</summary>
public sealed class Win32InputStateSource : IInputStateSource
{
    private readonly bool _swapped = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0;

    public InputSnapshot Read()
    {
        // GetAsyncKeyState は物理ボタンの状態を返すため、左右を入れ替えている設定では入れ替えて論理ボタンにする。
        bool physLeft = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        bool physRight = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;
        bool hasCursor = NativeMethods.GetPhysicalCursorPos(out var p);
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        uint tick = NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : 0;
        return new InputSnapshot(_swapped ? physRight : physLeft, _swapped ? physLeft : physRight, hasCursor, p.X, p.Y, tick);
    }
}

/// <summary>
/// 録画中の入力の監視（v1.21。設計 2.1）。専用スレッドで 10 ms ごとに入力の状態を調べ、録画の記録係へ渡す。
/// - 左右ボタンの押し下げ → クリック（位置は GetPhysicalCursorPos）
/// - 最後の入力の時刻が変わった → カーソルが動いていればマウス移動、動かずボタンの変化も無ければキー・ホイール
/// 記録係は録画中（Recording）だけ記録するため、一時停止中・カウントダウン中の入力は残らない。
/// </summary>
public sealed class InputPoller : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    private readonly Func<RecordingEventRecorder?> _recorder;
    private readonly IInputStateSource _source;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _stop;
    private InputSnapshot? _previous;
    private bool _errorLogged;
    private bool _disposed;

    /// <param name="recorder">いまの録画の記録係（録画を始めるたびに変わるため、毎回取り出す）。</param>
    public InputPoller(Func<RecordingEventRecorder?> recorder, IInputStateSource? source = null, ILogger? logger = null)
    {
        _recorder = recorder;
        _source = source ?? new Win32InputStateSource();
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _thread is not null; }
    }

    /// <summary>監視を始める（動いていれば何もしない）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _thread is not null) return;
            _previous = null;
            var stop = new ManualResetEventSlim(false);
            _stop = stop;
            _thread = new Thread(() => Run(stop)) { IsBackground = true, Name = "MVS input poller" };
            _thread.Start();
        }
    }

    /// <summary>監視を止める（スレッドの終了を待つ）。止まっていれば何もしない。</summary>
    public void Stop()
    {
        Thread? thread;
        ManualResetEventSlim? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }
        if (thread is null || stop is null) return;
        stop.Set();
        if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        stop.Dispose();
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Stop();
    }

    private void Run(ManualResetEventSlim stop)
    {
        try
        {
            while (!stop.Wait(Interval)) Poll();
        }
        catch (ObjectDisposedException)
        {
            // 停止と同時に破棄された。
        }
    }

    /// <summary>1 回分の監視（試験から直接呼べる）。</summary>
    public void Poll()
    {
        try
        {
            var now = _source.Read();
            var prev = _previous;
            _previous = now;
            if (prev is not { } p) return;
            var recorder = _recorder();
            if (recorder is null) return;

            bool leftPressed = now.Left && !p.Left;
            bool rightPressed = now.Right && !p.Right;
            if (now.HasCursor)
            {
                if (leftPressed) recorder.AddClick(now.X, now.Y, RecordedClick.Left);
                if (rightPressed) recorder.AddClick(now.X, now.Y, RecordedClick.Right);
            }
            else if (leftPressed || rightPressed)
            {
                recorder.AddKeyOrWheel();
            }

            if (now.LastInputTick != p.LastInputTick)
            {
                bool moved = now.HasCursor && p.HasCursor && (now.X != p.X || now.Y != p.Y);
                bool buttonChanged = now.Left != p.Left || now.Right != p.Right;
                if (moved) recorder.AddMouseMove();
                else if (!buttonChanged) recorder.AddKeyOrWheel();
            }
        }
        catch (Exception ex)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                _logger.LogWarning(ex, "Input polling failed");
            }
        }
    }
}
