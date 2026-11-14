using System.Text.Json;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Join;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;

namespace ManualVideoStudio.Tests;

/// <summary>録画中の操作の記録（v1.21 B）: 時刻の対応付け・記録係・ファイル・入力の無い範囲・連結。</summary>
public class RecordingEventsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-events-" + Guid.NewGuid().ToString("N"));

    public RecordingEventsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>手で動かす時計。Segment が負なら録画中でない。</summary>
    private sealed class ManualClock : IRecordingClock
    {
        public int Segment { get; set; }
        public double Seconds { get; set; }

        public bool TryNow(out int segment, out double seconds)
        {
            segment = Segment;
            seconds = Seconds;
            return Segment >= 0;
        }

        public ManualClock At(int segment, double seconds)
        {
            Segment = segment;
            Seconds = seconds;
            return this;
        }
    }

    // ---- RecordingTimeMap ----

    [Fact]
    public void TimeMapStartAlignmentUsesClockDirectly()
    {
        double? t = RecordingTimeMap.ToVideoTime(0, 3.0, [10.0], [9.6], [true], RecordingClockAlignment.Start, 0);
        Assert.Equal(3.0, t!.Value, 6);
        // 実際の長さを超える時刻は末尾に収める
        Assert.Equal(9.6, RecordingTimeMap.ToVideoTime(0, 9.9, [10.0], [9.6], [true], RecordingClockAlignment.Start, 0)!.Value, 6);
    }

    [Fact]
    public void TimeMapEndAlignmentSubtractsStartDelay()
    {
        // 時計 10 秒・映像 9.6 秒 → 先頭の 0.4 秒は映像が無い
        var end = RecordingClockAlignment.End;
        Assert.Equal(2.6, RecordingTimeMap.ToVideoTime(0, 3.0, [10.0], [9.6], [true], end)!.Value, 6);
        Assert.Equal(0.0, RecordingTimeMap.ToVideoTime(0, 0.2, [10.0], [9.6], [true], end)!.Value, 6);
        Assert.Equal(9.6, RecordingTimeMap.ToVideoTime(0, 10.0, [10.0], [9.6], [true], end)!.Value, 6);
    }

    [Fact]
    public void TimeMapDefaultIsStartAlignmentMinusCaptureLatency()
    {
        // 2026-11-14 の実測で先頭合わせ＋ffmpeg の最初のコマまでの遅れ（0.1 秒）に決めた。
        Assert.Equal(RecordingClockAlignment.Start, RecordingTimeMap.DefaultAlignment);
        Assert.Equal(0.1, RecordingTimeMap.CaptureLatencySeconds, 6);
        Assert.Equal(2.9, RecordingTimeMap.ToVideoTime(0, 3.0, [10.0], [10.2], [true])!.Value, 6);
        Assert.Equal(0.0, RecordingTimeMap.ToVideoTime(0, 0.05, [10.0], [10.2], [true])!.Value, 6);   // 区間の先頭より前にはしない
        // 一時停止の後の区間も、前の区間の実際の長さ＋（時計 − 遅れ）で、遅れは区間ごとに引く
        Assert.Equal(10.2 + 1.9, RecordingTimeMap.ToVideoTime(1, 2.0, [10.0, 5.0], [10.2, 5.2], [true, true])!.Value, 6);
    }

    [Fact]
    public void TimeMapOffsetsLaterSegmentsByActualDurationsOfUsableSegments()
    {
        double[] clocks = [5.0, 0.1, 4.0];
        double[] actual = [4.7, 0.0, 3.8];
        bool[] usable = [true, false, true];
        // 区間 3: 前の使った区間（4.7 秒）の後。末尾合わせで 2.0 - 0.2 = 1.8
        Assert.Equal(4.7 + 1.8, RecordingTimeMap.ToVideoTime(2, 2.0, clocks, actual, usable, RecordingClockAlignment.End)!.Value, 6);
        Assert.Equal(4.7 + 2.0, RecordingTimeMap.ToVideoTime(2, 2.0, clocks, actual, usable, RecordingClockAlignment.Start, 0)!.Value, 6);
        // 使わなかった区間・範囲外は null
        Assert.Null(RecordingTimeMap.ToVideoTime(1, 0.05, clocks, actual, usable));
        Assert.Null(RecordingTimeMap.ToVideoTime(3, 1.0, clocks, actual, usable));
        Assert.Null(RecordingTimeMap.ToVideoTime(-1, 1.0, clocks, actual, usable));
    }

    // ---- RecordingEventRecorder ----

    private static RecordingEventRecorder Recorder(ManualClock clock, bool logInput = true)
        => new(clock, new RecordingInfo { X = 100, Y = 50, Width = 1280, Height = 720 }, logInput);

    [Fact]
    public void ActivitiesWithinHalfSecondMergeIntoOneRange()
    {
        var clock = new ManualClock();
        var rec = Recorder(clock);
        foreach (var t in new[] { 1.0, 1.25, 1.75, 2.25 }) { clock.At(0, t); rec.AddKeyOrWheel(); } // 0.5 秒ちょうどはまとめる
        clock.At(0, 2.875); rec.AddKeyOrWheel(); // 0.625 秒空く → 新しい範囲
        clock.At(0, 5.0); rec.AddMouseMove();
        clock.At(0, 5.4); rec.AddMouseMove();

        var e = rec.Build([10], [10], [true], RecordingClockAlignment.Start, 0);
        Assert.Equal([new TimeRange(1.0, 2.25), new TimeRange(2.875, 2.875)], e.KeyRanges);
        Assert.Equal([new TimeRange(5.0, 5.4)], e.MoveRanges);
        Assert.True(e.InputLogged);
    }

    [Fact]
    public void RangesDoNotMergeAcrossSegments()
    {
        var clock = new ManualClock();
        var rec = Recorder(clock);
        clock.At(0, 4.9); rec.AddKeyOrWheel();
        clock.At(1, 0.1); rec.AddKeyOrWheel();
        var e = rec.Build([5, 5], [5, 5], [true, true], RecordingClockAlignment.Start, 0);
        Assert.Equal([new TimeRange(4.9, 4.9), new TimeRange(5.1, 5.1)], e.KeyRanges);
    }

    [Fact]
    public void ClicksInsideRegionKeepVideoCoordinatesAndOutsideBecomeInput()
    {
        var clock = new ManualClock().At(0, 2.0);
        var rec = Recorder(clock);
        Assert.True(rec.AddClick(100 + 640, 50 + 212, "left"));
        clock.At(0, 3.0);
        Assert.True(rec.AddClick(100 + 10, 50 + 20, "right"));
        clock.At(0, 4.0);
        Assert.True(rec.AddClick(99, 60, "left"));            // 左の外
        Assert.True(rec.AddClick(100 + 1280, 60, "left"));    // 右端（幅ちょうど）は外

        var e = rec.Build([10], [10], [true], RecordingClockAlignment.Start, 0);
        Assert.Equal([new RecordedClick(2.0, 640, 212, "left"), new RecordedClick(3.0, 10, 20, "right")], e.Clicks);
        Assert.Equal([new TimeRange(4.0, 4.0)], e.KeyRanges);
    }

    [Fact]
    public void MarkersAreRecordedEvenWhenLogInputIsOff()
    {
        var clock = new ManualClock().At(0, 1.5);
        var rec = Recorder(clock, logInput: false);
        Assert.Equal(1, rec.AddMarker());
        Assert.False(rec.AddClick(200, 200, "left"));
        Assert.False(rec.AddKeyOrWheel());
        Assert.False(rec.AddMouseMove());
        clock.At(0, 3.0);
        Assert.Equal(2, rec.AddMarker());
        Assert.Equal(2, rec.MarkerCount);

        var e = rec.Build([10], [10], [true], RecordingClockAlignment.Start, 0);
        Assert.Equal([1.5, 3.0], e.Markers);
        Assert.Empty(e.Clicks);
        Assert.Empty(e.KeyRanges);
        Assert.False(e.InputLogged);
        Assert.Empty(e.IdleRanges(10, 1)); // 入力を記録していなければ入力の無い範囲は求めない
    }

    [Fact]
    public void NothingIsRecordedWhileNotRecording()
    {
        var clock = new ManualClock { Segment = -1 };
        var rec = Recorder(clock);
        Assert.Null(rec.AddMarker());
        Assert.False(rec.AddClick(200, 200, "left"));
        Assert.False(rec.AddKeyOrWheel());
        var e = rec.Build([10], [10], [true]);
        Assert.Empty(e.Markers);
        Assert.Empty(e.KeyRanges);
    }

    [Fact]
    public void BuildDropsEventsOfUnusableSegmentsAndShiftsLaterOnes()
    {
        var clock = new ManualClock();
        var rec = Recorder(clock);
        clock.At(0, 1.0); rec.AddMarker();
        clock.At(1, 0.05); rec.AddMarker(); rec.AddClick(300, 300, "left"); rec.AddKeyOrWheel();
        clock.At(2, 2.0); rec.AddMarker(); rec.AddClick(300, 300, "left");

        var e = rec.Build([5, 0.1, 4], [4.5, 0, 3.6], [true, false, true], RecordingClockAlignment.End);
        // 区間 1: 末尾合わせで 1.0 - 0.5 = 0.5。区間 3: 4.5 + (2.0 - 0.4) = 6.1
        Assert.Equal([0.5, 6.1], e.Markers);
        Assert.Equal([new RecordedClick(6.1, 200, 250, "left")], e.Clicks);
        Assert.Empty(e.KeyRanges);
    }

    // ---- IdleRanges / Shifted / Merge ----

    [Fact]
    public void IdleRangesExcludeClicksKeysAndMoves()
    {
        var e = new RecordingEvents
        {
            Clicks = [new RecordedClick(10, 0, 0, "left")],
            KeyRanges = [new TimeRange(20, 25)],
            MoveRanges = [new TimeRange(24, 26), new TimeRange(28, 28.5)],
            Markers = [5],
        };
        var idle = e.IdleRanges(40, 2);
        // 26～28 の 2 秒も条件ちょうどなら入る（マーカーは入力に数えない）
        Assert.Equal([new TimeRange(0, 9.75), new TimeRange(10.25, 20), new TimeRange(26, 28), new TimeRange(28.5, 40)], idle);
        Assert.DoesNotContain(new TimeRange(26, 28), e.IdleRanges(40, 2.1));
        Assert.Equal([new TimeRange(0, 5)], new RecordingEvents().IdleRanges(5, 1)); // 入力が無ければ全体
    }

    [Fact]
    public void ShiftedAndMergeOffsetTheSecondRecord()
    {
        var a = new RecordingEvents { Markers = [1], Clicks = [new RecordedClick(2, 1, 1, "left")], KeyRanges = [new TimeRange(3, 4)] };
        var b = new RecordingEvents { Markers = [0.5], MoveRanges = [new TimeRange(1, 2)], Clicks = [new RecordedClick(0.2, 5, 6, "right")] };

        var s = b.Shifted(10);
        Assert.Equal([10.5], s.Markers);
        Assert.Equal(0.5, b.Markers[0]); // 元は変えない

        var m = RecordingEvents.Merge(a, b, 10)!;
        Assert.Equal([1, 10.5], m.Markers);
        Assert.Equal([new RecordedClick(2, 1, 1, "left"), new RecordedClick(10.2, 5, 6, "right")], m.Clicks);
        Assert.Equal([new TimeRange(3, 4)], m.KeyRanges);
        Assert.Equal([new TimeRange(11, 12)], m.MoveRanges);
        Assert.True(m.InputLogged);

        var onlyB = RecordingEvents.Merge(null, b, 10)!;
        Assert.Equal([10.5], onlyB.Markers);
        Assert.False(onlyB.InputLogged); // 記録の無い部分を「入力なし」と誤らない
        Assert.Null(RecordingEvents.Merge(null, null, 10));
    }

    // ---- ファイル ----

    private string Video(string name = "rec_20261113_101500.mp4")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    [Fact]
    public void SaveWritesDesignJsonShapeAndReadsBack()
    {
        var video = Video();
        var events = new RecordingEvents
        {
            Markers = [83.42, 190.05],
            Clicks = [new RecordedClick(12.31, 640, 212, "left")],
            KeyRanges = [new TimeRange(20.10, 24.85)],
            MoveRanges = [new TimeRange(11.02, 12.40)],
        };
        RecordingEventsFile.Save(video, events);

        var path = Path.Combine(_dir, "rec_20261113_101500.events.json");
        Assert.Equal(path, RecordingEventsFile.PathFor(video));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("rec_20261113_101500.mp4", root.GetProperty("videoFileName").GetString());
            Assert.Equal([83.42, 190.05], root.GetProperty("markers").EnumerateArray().Select(x => x.GetDouble()));
            var click = root.GetProperty("clicks")[0];
            Assert.Equal(12.31, click.GetProperty("t").GetDouble());
            Assert.Equal(640, click.GetProperty("x").GetInt32());
            Assert.Equal(212, click.GetProperty("y").GetInt32());
            Assert.Equal("left", click.GetProperty("button").GetString());
            var key = root.GetProperty("keyRanges")[0];
            Assert.Equal(JsonValueKind.Array, key.ValueKind);
            Assert.Equal([20.10, 24.85], key.EnumerateArray().Select(x => x.GetDouble()));
            Assert.Equal([11.02, 12.40], root.GetProperty("moveRanges")[0].EnumerateArray().Select(x => x.GetDouble()));
        }

        var read = RecordingEventsFile.TryReadFor(video, out var problem);
        Assert.Null(problem);
        Assert.NotNull(read);
        Assert.Equal(events.Markers, read.Markers);
        Assert.Equal(events.Clicks, read.Clicks);
        Assert.Equal(events.KeyRanges, read.KeyRanges);
        Assert.Equal(events.MoveRanges, read.MoveRanges);
        Assert.True(read.InputLogged);
    }

    [Fact]
    public void ReadsDesignExampleWithoutInputLoggedKey()
    {
        var video = Video();
        File.WriteAllText(RecordingEventsFile.PathFor(video), """
            {
              "schemaVersion": 1,
              "videoFileName": "rec_20261113_101500.mp4",
              "markers": [ 83.42, 190.05 ],
              "clicks": [ { "t": 12.31, "x": 640, "y": 212, "button": "left" } ],
              "keyRanges": [ [ 20.10, 24.85 ] ],
              "moveRanges": [ [ 11.02, 12.40 ] ]
            }
            """);
        var read = RecordingEventsFile.TryReadFor(video);
        Assert.NotNull(read);
        Assert.True(read.InputLogged);
        Assert.Equal([new TimeRange(20.10, 24.85)], read.KeyRanges);
        Assert.Equal(new RecordedClick(12.31, 640, 212, "left"), read.Clicks.Single());
    }

    [Fact]
    public void TryReadForReturnsNullForMissingOtherVideoAndCorrupt()
    {
        var video = Video();
        Assert.Null(RecordingEventsFile.TryReadFor(video, out var missing));
        Assert.Null(missing);

        // 別の動画の記録（ファイル名を変えた等）
        RecordingEventsFile.Save(Video("other.mp4"), new RecordingEvents { Markers = [1] });
        File.Copy(Path.Combine(_dir, "other.events.json"), RecordingEventsFile.PathFor(video));
        Assert.Null(RecordingEventsFile.TryReadFor(video, out var other));
        Assert.Null(other);

        // 壊れている（暗号化など）→ フルパスを返す
        File.WriteAllBytes(RecordingEventsFile.PathFor(video), [0x01, 0x02, 0x03, 0xFF]);
        Assert.Null(RecordingEventsFile.TryReadFor(video, out var corrupt));
        Assert.Equal(Path.GetFullPath(RecordingEventsFile.PathFor(video)), corrupt);
    }

    // ---- RecordingService ----

    private sealed class Proc(string file, string workDir) : IRecordingProcess
    {
        public bool HasExited => false;
        public string ErrorTail => "";
        public event EventHandler? Exited { add { } remove { } }
        public Task StartAsync(CancellationToken ct) { File.WriteAllBytes(Path.Combine(workDir, file), new byte[4096]); return Task.CompletedTask; }
        public Task<bool> StopAsync(TimeSpan timeout) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProcFactory : IRecordingProcessFactory
    {
        public IRecordingProcess Create(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory) => new Proc(arguments[^1], workingDirectory);
    }

    private sealed class Runner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory!, request.Arguments[^1]), new byte[2048]);
            return Task.FromResult(new ProcessResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class Meta : IMediaMetadataService
    {
        public List<string> Probed { get; } = [];
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
        {
            Probed.Add(Path.GetFileName(videoPath));
            return Task.FromResult(new VideoMetadata { Duration = 30, HasVideo = true, Width = 1280, Height = 720 });
        }
    }

    [Fact]
    public async Task RecordingServiceStampsOnlyWhileRecordingAndReturnsEvents()
    {
        var meta = new Meta();
        var svc = new RecordingService(new ProcFactory(), new Runner(), meta);
        Assert.Null(svc.Events);
        Assert.False(svc.TryNow(out _, out _));
        await svc.StartAsync(new RecordingStartRequest("ffmpeg.exe", "ffprobe.exe",
            new ScreenRecordingOptions(new CaptureRegion(0, 0, 1280, 720), 30, true, null), Path.Combine(_dir, "rec.mp4"),
            new RecordingInfo { Width = 1280, Height = 720 }, LogInput: false));
        Assert.True(svc.TryNow(out var seg, out _));
        Assert.Equal(0, seg);
        Assert.Equal(1, svc.Events!.AddMarker());
        await svc.PauseAsync();
        Assert.Null(svc.Events.AddMarker()); // 一時停止中は記録しない
        await svc.ResumeAsync();
        Assert.True(svc.TryNow(out seg, out _));
        Assert.Equal(1, seg);
        Assert.Equal(2, svc.Events.AddMarker());
        var result = await svc.StopAsync();

        Assert.NotNull(result.Events);
        Assert.Equal(2, result.Events.Markers.Count);
        Assert.False(result.Events.InputLogged);
        // 区間が 2 つなら区間ごとに長さを調べる（各 30 秒 → 2 つ目のマーカーは 30 秒以降）
        Assert.Contains("seg001.mkv", meta.Probed);
        Assert.Contains("seg002.mkv", meta.Probed);
        Assert.True(result.Events.Markers[1] >= 30);
        Assert.False(File.Exists(RecordingEventsFile.PathFor(result.OutputPath))); // ファイルは呼び出し側が書く
    }

    // ---- 連結 ----

    [Fact]
    public void JoinMergeShiftsAppendedRecordingByPrecedingDurations()
    {
        var a = Video("a.mp4");
        var b = Video("b.mp4");
        var c = Video("c.mp4");
        RecordingEventsFile.Save(a, new RecordingEvents { Markers = [2], KeyRanges = [new TimeRange(1, 3)] });
        RecordingEventsFile.Save(b, new RecordingEvents { Markers = [1], Clicks = [new RecordedClick(0.5, 3, 4, "left")] });

        var m = RecordingJoinService.MergeEvents([a, b], [10.0, 5.0])!;
        Assert.Equal([2, 11], m.Markers);
        Assert.Equal([new RecordedClick(10.5, 3, 4, "left")], m.Clicks);
        Assert.Equal([new TimeRange(1, 3)], m.KeyRanges);
        Assert.True(m.InputLogged);

        // 片方だけ: ある分だけ（ずらして）。入力の無い範囲は求めない
        var onlySecond = RecordingJoinService.MergeEvents([c, b], [7.0, 5.0])!;
        Assert.Equal([8], onlySecond.Markers);
        Assert.False(onlySecond.InputLogged);

        // 3 本: 3 本目は 10 + 5 秒後ろ
        var three = RecordingJoinService.MergeEvents([a, c, b], [10.0, 5.0, 4.0])!;
        Assert.Equal([2, 16], three.Markers);

        // どれにも無ければ null（書かない）
        Assert.Null(RecordingJoinService.MergeEvents([c, Video("d.mp4")], [1.0, 1.0]));
    }
}
