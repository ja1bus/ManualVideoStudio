using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Recording;

/// <summary>録画する範囲（仮想スクリーン上の物理 px）。</summary>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>H.264（yuv420p）用に幅・高さを偶数へ切り下げる（最小 2px）。</summary>
    public CaptureRegion ToEvenSize() => this with { Width = Math.Max(2, Width & ~1), Height = Math.Max(2, Height & ~1) };

    /// <summary>bounds の内側に収める（はみ出した分は切り詰め、位置は内側へ寄せる）。</summary>
    public CaptureRegion ClampTo(CaptureRegion bounds)
    {
        int w = Math.Clamp(Width, 2, Math.Max(2, bounds.Width));
        int h = Math.Clamp(Height, 2, Math.Max(2, bounds.Height));
        int x = Math.Clamp(X, bounds.X, bounds.Right - w);
        int y = Math.Clamp(Y, bounds.Y, bounds.Bottom - h);
        return new CaptureRegion(x, y, w, h);
    }

    public bool IntersectsWith(CaptureRegion other)
        => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public override string ToString() => $"{Width}x{Height} ({X},{Y})";
}

/// <summary>
/// 音声入力（ffmpeg の入力形式と入力名）。通常は DirectShow のマイク（Format = "dshow"、Input = "audio=デバイス名"）。
/// 統合試験では lavfi の信号音で代用する。
/// </summary>
public sealed record AudioInputSpec(string Format, string Input, string DisplayName, IReadOnlyList<string>? ExtraInputOptions = null)
{
    /// <summary>DirectShow のマイク。name はデバイス名または代替名（@device_cm_...）。</summary>
    public static AudioInputSpec DirectShow(string name, string displayName) => new("dshow", "audio=" + name, displayName);
}

/// <summary>1 回の録画の条件（録画中は変えない）。</summary>
public sealed record ScreenRecordingOptions(CaptureRegion Region, int Fps, bool DrawCursor, AudioInputSpec? Audio);

/// <summary>ffmpeg の DirectShow デバイス一覧の 1 件。</summary>
public sealed record DshowDevice(string Name, string AlternativeName, bool IsAudio);

/// <summary>ffmpeg -list_devices true -f dshow -i dummy の出力を解析する。</summary>
public static partial class DshowDeviceParser
{
    public static IReadOnlyList<DshowDevice> Parse(string stderr)
    {
        var list = new List<DshowDevice>();
        string? name = null;
        bool isAudio = false;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = DevicePattern().Match(line);
            if (m.Success)
            {
                if (name is not null) list.Add(new DshowDevice(name, "", isAudio));
                name = m.Groups["name"].Value;
                isAudio = m.Groups["kind"].Value.Contains("audio", StringComparison.Ordinal);
                continue;
            }
            var alt = AlternativePattern().Match(line);
            if (alt.Success && name is not null)
            {
                list.Add(new DshowDevice(name, alt.Groups["alt"].Value, isAudio));
                name = null;
            }
        }
        if (name is not null) list.Add(new DshowDevice(name, "", isAudio));
        return list;
    }

    [GeneratedRegex(@"\]\s+""(?<name>.+)""\s+\((?<kind>[a-z, ]+)\)\s*$")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"Alternative name\s+""(?<alt>.+)""\s*$")]
    private static partial Regex AlternativePattern();
}

/// <summary>録画の状態。</summary>
public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
    Completed,
    Failed,
}

/// <summary>録画が完了した結果。</summary>
public sealed record RecordingResult(string OutputPath, TimeSpan RecordedDuration, int SegmentCount, RecordingInfo Info);

/// <summary>録画に失敗した（利用者向けの説明と ffmpeg の詳細）。</summary>
public sealed class RecordingException(string message, string? detail = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Detail { get; } = detail;
}
