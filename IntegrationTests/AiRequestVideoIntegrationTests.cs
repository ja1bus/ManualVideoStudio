using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 不要区間の解析の依頼用動画（v1.21。設計 4.2）を実 ffmpeg で作り、元動画の時間軸のまま（カットを適用しない）・
/// 下に帯を足した大きさ・10 fps になることを確かめる。帯の文字が読み取れるかは利用者の実機で確かめる（要確認）。
/// </summary>
public sealed class AiRequestVideoIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    [Fact]
    public async Task RequestVideoKeepsSourceTimelineAndAddsBand()
    {
        var meta = fx.Metadata;
        var project = new ProjectDocument { SourceVideo = new SourceVideo { Path = fx.VideoPath }, VideoMetadata = meta };
        project.CutSegments.Add(new CutSegment { Start = 3, End = 9 }); // 依頼用動画には適用しない
        project.SceneChanges.AddRange([3, 9]);
        var table = SegmentTableBuilder.FromProject(project);
        var segments = table.ToHistory();

        var store = new CaptionRequestStore(Path.Combine(fx.Dir, "unused-root-cutreq"));
        var location = Path.Combine(fx.Dir, "プロジェクト cutreq", "caption-requests");
        var service = new AiRequestVideoService(fx.Runner, store);
        var progress = new List<VideoExportProgress>();
        var video = await service.PrepareAsync(fx.Location.Ffmpeg.Path!, project, segments, "動画 #1", location,
            new Progress<VideoExportProgress>(progress.Add), CancellationToken.None);

        Assert.StartsWith(AiRequestVideoService.FilePrefix, Path.GetFileName(video.Path));
        Assert.EndsWith(".mp4", video.Path);
        Assert.Equal(Path.GetFullPath(location), Path.GetDirectoryName(video.Path));
        Assert.Empty(Directory.GetFiles(location, "*.partial*"));
        Assert.True(store.IsManaged(video.Path));
        Assert.True(AiRequestVideoService.IsCurrent(video, project, segments));

        var rm = await new MediaMetadataService(fx.Runner).ProbeAsync(fx.Location.Ffprobe.Path!, video.Path);
        var layout = FFmpegArguments.AiRequestVideoSize(meta.Width, meta.Height);
        Assert.Equal((layout.Width, layout.Height), (rm.Width, rm.Height));
        Assert.Equal(meta.Height + layout.BandHeight, rm.Height);
        Assert.InRange(rm.Duration, meta.Duration - 0.2, meta.Duration + 0.2);
        Assert.InRange(rm.Fps, 9.9, 10.1);
        Assert.Equal(meta.HasAudio, rm.HasAudio);

        Assert.True(store.Delete(video.Path).Success);
        Assert.False(File.Exists(video.Path));
    }
}
