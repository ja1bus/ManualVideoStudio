using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Recording;

/// <summary>録画中の時刻の取得口（v1.21）。録画中（Recording）だけ、いまの区間の番号とその区間の時計の秒を返す。</summary>
public interface IRecordingClock
{
    /// <summary>録画中なら true と、区間の番号（0 から）・区間の時計の秒。一時停止中・カウントダウン中・停止処理中は false。</summary>
    bool TryNow(out int segment, out double seconds);
}

/// <summary>
/// 録画中の操作の記録係（v1.21。UI 非依存・スレッドセーフ）。入力の監視スレッドとショートカット・ボタンから呼ぶ。
/// 時刻は「区間の番号＋区間の時計の秒」で持ち、停止時に <see cref="Build"/> で動画の秒へ直す。
/// 不要マーカーは入力の記録の設定に関わらず残す。クリック・入力は入力の記録が ON のときだけ残す。
/// </summary>
public sealed class RecordingEventRecorder
{
    /// <summary>この間隔（秒）以内の入力は 1 つの範囲にまとめる。</summary>
    public const double MergeGapSeconds = 0.5;

    private readonly record struct Stamp(int Segment, double Seconds);
    private readonly record struct RawClick(Stamp At, int X, int Y, string Button);
    private sealed class RawRange(int segment, double start, double end)
    {
        public int Segment { get; } = segment;
        public double Start { get; set; } = start;
        public double End { get; set; } = end;
    }

    private readonly object _lock = new();
    private readonly IRecordingClock _clock;
    private readonly int _x, _y, _width, _height;
    private readonly List<Stamp> _markers = [];
    private readonly List<RawClick> _clicks = [];
    private readonly List<RawRange> _keyRanges = [];
    private readonly List<RawRange> _moveRanges = [];

    /// <param name="clock">時刻の取得口（通常は RecordingService）。</param>
    /// <param name="x">録画範囲の左上（仮想スクリーンの物理 px）。</param>
    /// <param name="logInput">クリック位置と入力の時刻を記録するか（RecordingSettings.LogInput）。</param>
    public RecordingEventRecorder(IRecordingClock clock, int x, int y, int width, int height, bool logInput)
    {
        _clock = clock;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        LogInput = logInput;
    }

    public RecordingEventRecorder(IRecordingClock clock, RecordingInfo region, bool logInput)
        : this(clock, region.X, region.Y, region.Width, region.Height, logInput)
    {
    }

    /// <summary>クリック位置と入力の時刻を記録するか。</summary>
    public bool LogInput { get; }

    /// <summary>これまでに記録したマーカーの数。</summary>
    public int MarkerCount
    {
        get { lock (_lock) return _markers.Count; }
    }

    /// <summary>
    /// 不要マーカーを記録する（入力の記録の設定に関わらず）。記録したら通算の数（「マーク 3」の 3）、
    /// 録画中でない（一時停止中・カウントダウン中など）ため記録しなかったら null。
    /// </summary>
    public int? AddMarker()
    {
        if (!_clock.TryNow(out var seg, out var sec)) return null;
        lock (_lock)
        {
            _markers.Add(new Stamp(seg, sec));
            return _markers.Count;
        }
    }

    /// <summary>
    /// クリック（押し下げ）を記録する。screenX / screenY は仮想スクリーンの物理 px、button は "left" / "right"。
    /// 録画範囲の中なら動画の px で位置を残し、外なら位置を残さず入力があったことだけ（キー・ホイールの範囲に）残す。
    /// 記録したら true。
    /// </summary>
    public bool AddClick(int screenX, int screenY, string button)
    {
        if (!LogInput || !_clock.TryNow(out var seg, out var sec)) return false;
        int vx = screenX - _x, vy = screenY - _y;
        lock (_lock)
        {
            if (vx >= 0 && vy >= 0 && vx < _width && vy < _height)
                _clicks.Add(new RawClick(new Stamp(seg, sec), vx, vy, button == RecordedClick.Right ? RecordedClick.Right : RecordedClick.Left));
            else
                AddActivity(_keyRanges, seg, sec);
        }
        return true;
    }

    /// <summary>キー・ホイールの入力（種類・内容は持たない）を記録する。記録したら true。</summary>
    public bool AddKeyOrWheel() => AddActivity(_keyRanges);

    /// <summary>マウスの移動を記録する。記録したら true。</summary>
    public bool AddMouseMove() => AddActivity(_moveRanges);

    private bool AddActivity(List<RawRange> ranges)
    {
        if (!LogInput || !_clock.TryNow(out var seg, out var sec)) return false;
        lock (_lock) AddActivity(ranges, seg, sec);
        return true;
    }

    /// <summary>直前の範囲と同じ区間で間隔 0.5 秒以内なら広げ、そうでなければ新しい範囲にする（呼び出し側でロック）。</summary>
    private static void AddActivity(List<RawRange> ranges, int seg, double sec)
    {
        if (ranges.Count > 0)
        {
            var last = ranges[^1];
            if (last.Segment == seg && sec <= last.End + MergeGapSeconds && sec >= last.Start - MergeGapSeconds)
            {
                last.Start = Math.Min(last.Start, sec);
                last.End = Math.Max(last.End, sec);
                return;
            }
        }
        ranges.Add(new RawRange(seg, sec, sec));
    }

    /// <summary>
    /// 記録を動画の秒へ直す。clockLengths は区間ごとの時計の長さ、actualDurations は実際の映像の長さ、segmentUsable は連結に使ったか。
    /// 使わなかった区間の記録は捨てる。VideoFileName は空（保存時に入れる）。
    /// </summary>
    public RecordingEvents Build(IReadOnlyList<double> clockLengths, IReadOnlyList<double> actualDurations, IReadOnlyList<bool> segmentUsable,
        RecordingClockAlignment alignment = RecordingTimeMap.DefaultAlignment, double latencySeconds = RecordingTimeMap.CaptureLatencySeconds)
    {
        double? Map(int seg, double sec) => RecordingTimeMap.ToVideoTime(seg, sec, clockLengths, actualDurations, segmentUsable, alignment, latencySeconds);

        List<TimeRange> MapRanges(List<RawRange> raw)
        {
            var list = new List<TimeRange>();
            foreach (var r in raw)
                if (Map(r.Segment, r.Start) is { } s && Map(r.Segment, r.End) is { } e)
                    list.Add(new TimeRange(RecordingEvents.Round(s), RecordingEvents.Round(Math.Max(s, e))));
            return list;
        }

        lock (_lock)
        {
            var events = new RecordingEvents { InputLogged = LogInput };
            foreach (var m in _markers)
                if (Map(m.Segment, m.Seconds) is { } t) events.Markers.Add(RecordingEvents.Round(t));
            foreach (var c in _clicks)
                if (Map(c.At.Segment, c.At.Seconds) is { } t) events.Clicks.Add(new RecordedClick(RecordingEvents.Round(t), c.X, c.Y, c.Button));
            events.KeyRanges.AddRange(MapRanges(_keyRanges));
            events.MoveRanges.AddRange(MapRanges(_moveRanges));
            events.SortAll();
            return events;
        }
    }
}
