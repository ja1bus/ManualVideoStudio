using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 録画中の操作の記録（v1.21。録画フォルダーの「動画名.events.json」）。プロジェクトを開いたとき・動画を指定し直したときに読む。
/// 時刻は元動画基準なので、カットを変えても直す必要がない。記録が無い動画では null（機能が使えないだけ）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>今の動画の操作の記録（無ければ null）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordingEvents), nameof(HasRecordedClicks), nameof(RecordedClickCount), nameof(RecordingMarkers))]
    private RecordingEvents? _recordingEvents;

    public bool HasRecordingEvents => RecordingEvents is not null;
    public bool HasRecordedClicks => RecordingEvents is { Clicks.Count: > 0 };
    public int RecordedClickCount => RecordingEvents?.Clicks.Count ?? 0;

    /// <summary>不要マーカー（元動画の秒・昇順）。</summary>
    public IReadOnlyList<double> RecordingMarkers => RecordingEvents?.Markers.OrderBy(m => m).ToList() ?? [];

    /// <summary>入力の無い範囲（簡易解析・区間表に渡す）。記録が無い・入力を記録していなければ空。</summary>
    public IReadOnlyList<TimeRange> InputIdleRanges(double minSeconds)
        => RecordingEvents is { } e && Duration > 0 ? e.IdleRanges(Duration, minSeconds) : [];

    /// <summary>今の動画の操作の記録を読み直す。読めないときはフルパスをログに残し、記録なしとして扱う。</summary>
    private void LoadRecordingEvents()
    {
        var video = Project?.SourceVideo.Path;
        if (string.IsNullOrEmpty(video) || !File.Exists(video))
        {
            RecordingEvents = null;
            return;
        }
        RecordingEvents = RecordingEventsFile.TryReadFor(video, out var problem);
        if (problem is not null)
            _logger.LogWarning("Recording events unreadable: {Message}", FileReadProblem.Message("操作の記録を読み込めませんでした。", problem));
    }
}
