using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 字幕依頼用動画（v1.12）を実 ffmpeg で作り、完成動画と同じ時間軸・同じ解像度になることを確かめる。
/// クロスフェードあり・なし × 音声あり・なし。
/// </summary>
public sealed class CaptionRequestIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;
    private MediaMetadataService Probe => new(fx.Runner);

    private async Task<(string Path, VideoMetadata Meta)> SourceAsync(bool audio)
    {
        if (audio) return (fx.VideoPath, fx.Metadata);
        var path = Path.Combine(fx.Dir, "音声なし.mp4");
        if (!File.Exists(path))
        {
            var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-i", fx.VideoPath, "-an", "-c:v", "copy", path]));
            Assert.True(r.Succeeded, r.ErrorTail());
        }
        return (path, await Probe.ProbeAsync(Ffprobe, path));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RequestVideoHasSameTimelineAndSizeAsFinalExport(bool crossfade, bool audio)
    {
        var (src, meta) = await SourceAsync(audio);
        var project = new ProjectDocument { SourceVideo = new SourceVideo { Path = src }, VideoMetadata = meta };
        project.ExportSettings.CutTransition = crossfade;
        // 3～9 秒の静止部分を 1 秒だけ残す（短縮）。カットが 1 つだけでもつなぎ目ができる。
        project.CutSegments.Add(new CutSegment { Start = 3, End = 9, Action = SegmentAction.Shorten, KeepSeconds = 1 });
        project.SubtitleSegments.Add(new SubtitleSegment { Start = 1, End = 2, Text = "既存の字幕は焼き込まない" });
        var expected = EditTimelineCalculator.OutputDuration(EditTimelineCalculator.ComputeKeepRanges(meta.Duration, project.CutSegments));

        var store = new CaptionRequestStore(Path.Combine(fx.Dir, "caption-requests-" + crossfade + audio));
        var export = new VideoExportService(fx.Runner);
        var service = new CaptionRequestService(export, store);
        var request = await service.PrepareAsync(Ffmpeg, fx.Capabilities.MajorVersion, project, "動画 #1", null, CancellationToken.None);

        var finalPath = Path.Combine(fx.Dir, $"final_{crossfade}_{audio}.mp4");
        await export.ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, src, meta, project.CutSegments.ToList(), finalPath,
            new VideoEncodeOptions { Crf = 22, Preset = "ultrafast", AudioBitrateKbps = 192 },
            project.SubtitleSegments.ToList(), new SubtitleStyle(), project.ExportSettings.TransitionSecondsForExport), null, CancellationToken.None);

        var rm = await Probe.ProbeAsync(Ffprobe, request.Path);
        var fm = await Probe.ProbeAsync(Ffprobe, finalPath);
        Assert.InRange(rm.Duration, expected - 0.15, expected + 0.15);
        Assert.InRange(Math.Abs(rm.Duration - fm.Duration), 0, 0.15);
        Assert.Equal(request.Duration, expected, 2);
        // 操作画面の文字が読めるよう、解像度は変えない。音声の有無も完成動画と同じ。
        Assert.Equal((fm.Width, fm.Height), (rm.Width, rm.Height));
        Assert.Equal(meta.Width & ~1, rm.Width);
        Assert.Equal(audio, rm.HasAudio);
        Assert.Equal(fm.HasAudio, rm.HasAudio);
        // 保存場所はアプリ管理のフォルダー。作成途中のファイルは残らない。
        Assert.Equal(store.Root, Path.GetDirectoryName(request.Path));
        Assert.Empty(Directory.GetFiles(store.Root, "*.partial*"));
        Assert.True(CaptionRequestService.IsCurrent(request, project, project.ExportSettings.TransitionSecondsForExport));
        // 完成動画は元動画から別に作る（依頼用動画とは別のファイル）。
        Assert.NotEqual(request.Path, finalPath);
        Assert.True(store.Delete(request.Path).Success);
        Assert.False(File.Exists(request.Path));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task CancelLeavesNoFile()
    {
        var project = new ProjectDocument { SourceVideo = new SourceVideo { Path = fx.VideoPath }, VideoMetadata = fx.Metadata };
        var store = new CaptionRequestStore(Path.Combine(fx.Dir, "caption-requests-cancel"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CaptionRequestService(new VideoExportService(fx.Runner), store).PrepareAsync(Ffmpeg, fx.Capabilities.MajorVersion, project, "x", null, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(store.Root));
    }
}
