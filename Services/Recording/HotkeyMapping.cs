using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Services.Recording;

/// <summary>録画のショートカット（v1.21）を RegisterHotKey の修飾キー（MOD_*）と仮想キーコード（VK_*）へ変換する。</summary>
public static class HotkeyMapping
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    /// <summary>修飾キー（MOD_NOREPEAT は含めない）。</summary>
    public static uint ToModifiers(HotkeyModifiers modifiers) => modifiers switch
    {
        HotkeyModifiers.CtrlAlt => ModControl | ModAlt,
        HotkeyModifiers.CtrlAltShift => ModControl | ModAlt | ModShift,
        _ => ModControl | ModShift,
    };

    /// <summary>仮想キーコード。F1～F12 = 0x70～0x7B、A～Z = 0x41～0x5A、0～9 = 0x30～0x39。</summary>
    public static uint ToVirtualKey(HotkeyKey key) => key switch
    {
        >= HotkeyKey.F1 and <= HotkeyKey.F12 => 0x70u + (uint)(key - HotkeyKey.F1),
        >= HotkeyKey.A and <= HotkeyKey.Z => 0x41u + (uint)(key - HotkeyKey.A),
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => 0x30u + (uint)(key - HotkeyKey.D0),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
