using System.Collections.Concurrent;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Core.Analysis;

public sealed record AnalysisResult(
    IReadOnlyList<CutCandidate> Candidates,
    IReadOnlyList<TimeRange> Freezes,
    IReadOnlyList<TimeRange> Silences,
    IReadOnlyList<double> SceneChanges,
    TimeSpan Elapsed);

public interface ILocalAnalysisService
{
    Task<AnalysisResult> AnalyzeAsync(string ffmpegPath, string videoPath, VideoMetadata meta, AnalysisSettings settings,
        IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// freezedetect（静止）・silencedetect（無音）・scene（画面変化）を 1 回の ffmpeg 実行で検出し、カット候補を作る。
/// 結果は候補であり、確定編集には反映しない。
/// </summary>
public sealed class LocalAnalysisService(IProcessRunner runner, ILogger<LocalAnalysisService>? logger = null) : ILocalAnalysisService
{
    private readonly ILogger _logger = logger ?? NullLogger<LocalAnalysisService>.Instance;

    public async Task<AnalysisResult> AnalyzeAsync(string ffmpegPath, string videoPath, VideoMetadata meta, AnalysisSettings settings,
        IProgress<double>? progress, CancellationToken ct)
    {
        var args = FFmpegArguments.Analysis(videoPath, settings, meta.HasAudio);
        var lines = new ConcurrentQueue<string>();
        var request = new ProcessRequest(ffmpegPath, args)
        {
            // 検出結果は info ログ。stderr 全体は保持せず必要な行だけ集める。
            OnStandardErrorLine = line =>
            {
                if (line.Contains("freezedetect", StringComparison.Ordinal) || line.Contains("silence_", StringComparison.Ordinal)
                    || line.Contains("showinfo", StringComparison.Ordinal))
                    lines.Enqueue(line);
            },
            OnStandardOutputLine = line =>
            {
                var t = FFmpegProgressParser.ParseLine(line);
                if (t is { } sec) progress?.Report(FFmpegProgressParser.Ratio(sec, meta.Duration));
            },
            MaxCapturedChars = 200_000,
        };
        var r = await runner.RunAsync(request, ct);
        if (!r.Succeeded)
            throw new InvalidOperationException($"解析に失敗しました（終了コード {r.ExitCode}）。{Environment.NewLine}{r.ErrorTail(5)}");

        var all = lines.ToList();
        var freezes = settings.EnableFreeze ? DetectionLogParser.ParseFreezes(all, meta.Duration) : [];
        var silences = settings.EnableSilence && meta.HasAudio ? DetectionLogParser.ParseSilences(all, meta.Duration) : [];
        var scenes = settings.EnableSceneChange ? DetectionLogParser.ParseSceneChanges(all) : [];
        var candidates = CandidateBuilder.Build(freezes, silences, meta.Duration, meta.HasAudio, settings);
        _logger.LogInformation("Analysis done: freezes={F} silences={S} scenes={C} candidates={N} ({E:0.0}s)",
            freezes.Count, silences.Count, scenes.Count, candidates.Count, r.Elapsed.TotalSeconds);
        progress?.Report(1);
        return new AnalysisResult(candidates, freezes, silences, scenes, r.Elapsed);
    }
}
