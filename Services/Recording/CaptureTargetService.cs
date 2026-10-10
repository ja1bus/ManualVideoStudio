using System.Diagnostics;
using ManualVideoStudio.Core.Recording;
using static ManualVideoStudio.Services.Recording.NativeMethods;

namespace ManualVideoStudio.Services.Recording;

/// <summary>録画対象のウィンドウ。内部では HWND で識別し、画面にはタイトルとプロセス名を出す。</summary>
public sealed record CaptureWindow(IntPtr Handle, string Title, string ProcessName, bool IsMinimized)
{
    public string Label => (IsMinimized ? "（最小化）" : "") + Title + (ProcessName.Length > 0 ? $" — {ProcessName}" : "");
}

/// <summary>録画対象のモニター。Bounds は仮想スクリーン上の物理 px。</summary>
public sealed record CaptureMonitor(int Number, string DeviceName, CaptureRegion Bounds, bool IsPrimary)
{
    public string Label => $"モニター {Number}  {Bounds.Width}x{Bounds.Height}{(IsPrimary ? "（メイン）" : "")}";
}

/// <summary>録画対象（ウィンドウ・モニター）の列挙と位置の取得。</summary>
public interface ICaptureTargetService
{
    IReadOnlyList<CaptureWindow> ListWindows();
    IReadOnlyList<CaptureMonitor> ListMonitors();
    /// <summary>すべてのモニターを含む範囲（物理 px）。</summary>
    CaptureRegion VirtualScreen();
    /// <summary>ウィンドウの見た目の枠（影を除く、物理 px）。閉じられていれば null。</summary>
    CaptureRegion? GetWindowBounds(IntPtr handle);
    /// <summary>ウィンドウが開いていれば最小化を戻して前面に出す。</summary>
    bool BringToFront(IntPtr handle);
    /// <summary>指定範囲の中心を含むモニター（無ければメイン）。</summary>
    CaptureMonitor MonitorOf(CaptureRegion region);
}

public sealed class CaptureTargetService : ICaptureTargetService
{
    public IReadOnlyList<CaptureWindow> ListWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<CaptureWindow>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = GetText(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            var cls = GetClass(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            if (!names.TryGetValue(pid, out var pname))
            {
                try { pname = Process.GetProcessById((int)pid).ProcessName; }
                catch (Exception) { pname = ""; }
                names[pid] = pname;
            }
            list.Add(new CaptureWindow(h, title, pname, IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public IReadOnlyList<CaptureMonitor> ListMonitors()
    {
        var raw = new List<(string Device, CaptureRegion Bounds, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMon, ref info)) return true;
            // GetMonitorInfo の矩形は DPI 仮想化される場合があるため、ディスプレイ設定（物理 px）の位置・解像度を優先する。
            var mode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            var r = info.rcMonitor;
            var bounds = EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref mode) && mode.dmPelsWidth > 0
                ? new CaptureRegion(mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight)
                : new CaptureRegion(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            raw.Add((info.szDevice, bounds, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        // 番号はメインを 1 とし、残りは左から順に振る。
        return raw.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new CaptureMonitor(i + 1, m.Device, m.Bounds, m.Primary)).ToList();
    }

    public CaptureRegion VirtualScreen()
    {
        var monitors = ListMonitors();
        if (monitors.Count == 0) return new CaptureRegion(0, 0, 1920, 1080);
        int l = monitors.Min(m => m.Bounds.X), t = monitors.Min(m => m.Bounds.Y);
        int r = monitors.Max(m => m.Bounds.Right), b = monitors.Max(m => m.Bounds.Bottom);
        return new CaptureRegion(l, t, r - l, b - t);
    }

    public CaptureRegion? GetWindowBounds(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return null;
        if (DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(handle, out r))
            return null;
        var region = new CaptureRegion(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return region.IsEmpty ? null : region;
    }

    public bool BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        return true;
    }

    public CaptureMonitor MonitorOf(CaptureRegion region)
    {
        var monitors = ListMonitors();
        int cx = region.X + region.Width / 2, cy = region.Y + region.Height / 2;
        return monitors.FirstOrDefault(m => cx >= m.Bounds.X && cx < m.Bounds.Right && cy >= m.Bounds.Y && cy < m.Bounds.Bottom)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? new CaptureMonitor(1, "", new CaptureRegion(0, 0, 1920, 1080), true);
    }
}
