using System.Runtime.InteropServices;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Recording;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.Services.Recording;

/// <summary>マイク（録音デバイス）。Name は ffmpeg（DirectShow）のデバイス名、AlternativeName は一意な代替名。</summary>
public sealed record AudioInputDevice(string Name, string AlternativeName)
{
    /// <summary>ffmpeg に渡す名前（代替名があれば同名デバイスでも区別できるので優先する）。</summary>
    public string InputName => AlternativeName.Length > 0 ? AlternativeName : Name;
}

public interface IAudioInputDeviceService
{
    /// <summary>録音デバイスの一覧（ffmpeg の DirectShow 列挙）。</summary>
    Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default);
    /// <summary>Windows の既定の録音デバイス名（無ければ null）。</summary>
    string? GetDefaultDeviceName();
    /// <summary>
    /// 録音に使うデバイスを決める。selectedName が空なら Windows の既定のマイク。
    /// 見つからなければ null（呼び出し側で「マイクが見つかりません」と案内する）。
    /// </summary>
    AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName);
}

/// <summary>
/// マイクの列挙。録音は ffmpeg の DirectShow（dshow）入力で行うため、一覧も ffmpeg から取る。
/// 「既定のマイク」は Windows のサウンド設定の既定の録音デバイス（Core Audio API）の名前で DirectShow のデバイスを探す。
/// </summary>
public sealed class AudioInputDeviceService(IProcessRunner runner, ILogger<AudioInputDeviceService> logger) : IAudioInputDeviceService
{
    public async Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default)
    {
        // -i dummy は必ず失敗する（終了コード 1）が、デバイス一覧は stderr に出る。
        var r = await runner.RunAsync(new ProcessRequest(ffmpegPath, FFmpegArguments.ListDshowDevices()) { Timeout = TimeSpan.FromSeconds(20) }, ct);
        var devices = DshowDeviceParser.Parse(r.StandardError).Where(d => d.IsAudio)
            .Select(d => new AudioInputDevice(d.Name, d.AlternativeName)).ToList();
        logger.LogInformation("Audio input devices: {Count} ({Names})", devices.Count, string.Join(" / ", devices.Select(d => d.Name)));
        return devices;
    }

    public AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName)
    {
        if (devices.Count == 0) return null;
        if (!string.IsNullOrEmpty(selectedName))
            return devices.FirstOrDefault(d => d.Name == selectedName || d.AlternativeName == selectedName);
        if (!string.IsNullOrEmpty(defaultName))
        {
            // 古い DirectShow は名前を 31 文字で切るため、前方一致でも探す。
            var match = devices.FirstOrDefault(d => d.Name == defaultName)
                ?? devices.FirstOrDefault(d => defaultName.StartsWith(d.Name, StringComparison.Ordinal) || d.Name.StartsWith(defaultName, StringComparison.Ordinal));
            if (match is not null) return match;
        }
        return devices[0];
    }

    public string? GetDefaultDeviceName()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                // eCapture = 1, eConsole = 0
                if (enumerator.GetDefaultAudioEndpoint(1, 0, out var device) != 0 || device is null) return null;
                try
                {
                    if (device.OpenPropertyStore(0, out var store) != 0 || store is null) return null;
                    try
                    {
                        var key = FriendlyNameKey;
                        if (store.GetValue(ref key, out var value) != 0) return null;
                        try
                        {
                            return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) : null;
                        }
                        finally
                        {
                            PropVariantClear(ref value);
                        }
                    }
                    finally { Marshal.ReleaseComObject(store); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            logger.LogWarning(ex, "Default capture device lookup failed");
            return null;
        }
    }

    // ---------------- Core Audio API（最小限の定義） ----------------

    private static PROPERTYKEY FriendlyNameKey => new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr pointer;
        public IntPtr pointer2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore? properties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
}
