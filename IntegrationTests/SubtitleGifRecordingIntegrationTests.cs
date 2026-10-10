using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 字幕の焼き込み（MP4 / GIF）・Animated GIF・画面録画を実 ffmpeg で確認する。
/// テスト動画（MediaFixture）の 3～9 秒は紺一色なので、そこに白い字幕が出るかを画素で調べる。
/// 画面録画は gdigrab で実際のデスクトップを録るため、対話セッション（画面のある PC）で実行すること。
/// </summary>
public sealed class SubtitleGifRecordingIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    /// <summary>0～1 秒を削除し、編集後 3.0～4.5 秒（元動画 4.0～5.5 秒 = 紺の部分）に字幕を置く。</summary>
    private static readonly CutSegment[] Cuts = [new CutSegment { Start = 0, End = 1, Action = SegmentAction.Remove }];
    private static readonly SubtitleSegment[] Subs = [new SubtitleSegment { Start = 3.0, End = 4.5, Text = "字幕テスト ABC", FontSize = 96 }];

    private async Task<string> ExtractFrameAsync(string video, double time)
    {
        var png = Path.Combine(fx.Dir, $"frame-{Guid.NewGuid():N}.png");
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, FFmpegArguments.FrameExtraction(video, time, png, ImageFormat.Png)));
        Assert.True(r.Succeeded, r.ErrorTail());
        return png;
    }

    /// <summary>画像の下側 30%（字幕の位置）にある明るい画素（白い文字。背景は紺）の数。</summary>
    private static int WhitePixelsInBottom(string png)
    {
        var frame = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);
        int count = 0;
        for (int y = (int)(h * 0.7); y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                if (px[i] + px[i + 1] + px[i + 2] > 540) count++;
            }
        return count;
    }

    [Fact]
    public async Task Mp4ExportBurnsSubtitlesAfterCutsAndKeepsAudio()
    {
        var output = Path.Combine(fx.Dir, "字幕付き 出力.mp4");
        var svc = new VideoExportService(fx.Runner);
        await svc.ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, Cuts, output,
            new VideoEncodeOptions { Preset = "ultrafast" }, Subs, new SubtitleStyle()), null, CancellationToken.None);

        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.Equal(11, meta.Duration, 0.2);
        Assert.True(meta.HasAudio); // 字幕を焼き込んでも音声は残す
        Assert.True(WhitePixelsInBottom(await ExtractFrameAsync(output, 3.7)) > 150, "字幕の表示時間に白い文字が無い");
        Assert.True(WhitePixelsInBottom(await ExtractFrameAsync(output, 5.5)) < 20, "字幕の表示時間外に白い画素がある");
    }

    [Fact]
    public async Task SubtitleUsesMeiryoInLibass()
    {
        // libass が Meiryo を実際に選ぶか（別のフォントで黙って描かれないか）を stderr の fontselect で確認する
        // （-f null では文字を描かないため fontselect が出ない。画像に出力する）。
        var dir = Path.Combine(fx.Dir, "ass-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "subtitles.ass"), AssSubtitleBuilder.Build(
            [new SubtitleSegment { Start = 0, End = 3, Text = "字幕テスト ABC" }], new SubtitleStyle(), 640, 360));
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-loglevel", "verbose", "-f", "lavfi", "-i", "color=c=navy:s=640x360:d=5",
            "-vf", FFmpegArguments.SubtitlesFilter("subtitles.ass"), "-frames:v", "1", "-y", "check.png",
        ]) { WorkingDirectory = dir });
        Assert.True(r.Succeeded, r.ErrorTail());
        var line = r.StandardError.Split('\n').FirstOrDefault(l => l.Contains("fontselect", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains("(Meiryo, 400, 0) -> Meiryo", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Meiryo")]
    [InlineData("Meiryo UI")]
    [InlineData("Yu Gothic")]
    [InlineData("BIZ UDPGothic")]
    public async Task SubtitleFontChoicesResolveInLibass(string family)
    {
        // 選択肢のフォントを libass が別のフォントに置き換えずに選ぶか（v1.4）。例: "(BIZ UDPGothic, 400, 0) -> BIZ-UDPGothic, 1, BIZ-UDPGothic"。
        var dir = Path.Combine(fx.Dir, "font-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "subtitles.ass"), AssSubtitleBuilder.Build(
            [new SubtitleSegment { Start = 0, End = 3, Text = "字幕テスト ABC" }], new SubtitleStyle { FontFamily = family }, 640, 360));
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-loglevel", "verbose", "-f", "lavfi", "-i", "color=c=navy:s=640x360:d=5",
            "-vf", FFmpegArguments.SubtitlesFilter("subtitles.ass"), "-frames:v", "1", "-y", "check.png",
        ]) { WorkingDirectory = dir });
        Assert.True(r.Succeeded, r.ErrorTail());
        var line = r.StandardError.Split('\n').FirstOrDefault(l => l.Contains("fontselect", StringComparison.Ordinal));
        Assert.NotNull(line);
        var chosen = line[(line.IndexOf("->", StringComparison.Ordinal) + 2)..].Split(',')[0];
        static string Key(string n) => n.Replace(" ", "").Replace("-", "").ToUpperInvariant();
        Assert.StartsWith(Key(family), Key(chosen));
    }

    /// <summary>画像の下側 30% で条件に合う画素の数。</summary>
    private static int CountBottom(string png, Func<byte, byte, byte, bool> match)
    {
        var frame = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);
        int count = 0;
        for (int y = (int)(h * 0.7); y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                if (match(px[i + 2], px[i + 1], px[i])) count++;
            }
        return count;
    }

    [Theory]
    [InlineData(SubtitlePreset.Outline, false, false)]
    [InlineData(SubtitlePreset.BlackBox, true, false)]
    [InlineData(SubtitlePreset.WhiteBox, false, true)]
    public async Task SubtitlePresetsDrawBoxes(SubtitlePreset preset, bool blackBox, bool whiteBox)
    {
        // 背景は紺。黒い帯なら黒い画素が、白い帯なら白い画素が帯の面積ぶん（文字だけより多く）出る。
        var output = Path.Combine(fx.Dir, $"preset-{preset}.mp4");
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, Cuts, output,
            new VideoEncodeOptions { Preset = "ultrafast" }, Subs, new SubtitleStyle { Preset = preset }), null, CancellationToken.None);
        var png = await ExtractFrameAsync(output, 3.7);
        int black = CountBottom(png, (r, g, b) => r + g + b < 60);
        int white = CountBottom(png, (r, g, b) => r + g + b > 700);
        Assert.True(blackBox == black > 6000, $"黒い画素 {black}");
        Assert.True(whiteBox == white > 6000, $"白い画素 {white}");
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0)]
    public async Task CutTransitionKeepsDurationAndBlendsAtTheJoin(double transition)
    {
        // 1～5 秒を削除: 出力 0～1 秒は動く映像（testsrc2）、1 秒以降は紺。つなぎ目（出力 1.0 秒）は、
        // クロスフェードなら両方が混ざり、なしなら紺だけ。長さはどちらも 8 秒で、音声も残る。
        var output = Path.Combine(fx.Dir, $"transition-{transition}.mp4");
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            [new CutSegment { Start = 1, End = 5, Action = SegmentAction.Remove }], output,
            new VideoEncodeOptions { Preset = "ultrafast" }, TransitionSeconds: transition), null, CancellationToken.None);
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.Equal(8, meta.Duration, 0.15);
        Assert.True(meta.HasAudio);

        static bool NotNavy(byte r, byte g, byte b) => r > 40 || g > 40 || Math.Abs(b - 128) > 40;
        int atJoin = CountBottom(await ExtractFrameAsync(output, 1.04), NotNavy);
        int after = CountBottom(await ExtractFrameAsync(output, 1.5), NotNavy);
        Assert.True(after < 100, $"つなぎ目の後に紺以外の画素がある ({after})");
        if (transition > 0) Assert.True(atJoin > 5000, $"つなぎ目が混ざっていない ({atJoin})");
        else Assert.True(atJoin < 100, $"トランジションなしなのに混ざっている ({atJoin})");
    }

    [Fact]
    public async Task GifExportWithTransition()
    {
        var output = Path.Combine(fx.Dir, "transition.gif");
        var result = await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            [new CutSegment { Start = 1, End = 5, Action = SegmentAction.Remove }], Subs, new SubtitleStyle(), GifQuality.Light, output, 0.3), null, CancellationToken.None);
        Assert.True(File.Exists(output));
        Assert.Equal(8, result.OutputDuration, 0.01);
    }

    [Theory]
    [InlineData(GifQuality.Light, 8)]
    [InlineData(GifQuality.Standard, 12)]
    [InlineData(GifQuality.High, 15)]
    public async Task GifExportWithCutsAndSubtitlesIgnoresAudio(GifQuality quality, int fps)
    {
        var output = Path.Combine(fx.Dir, $"操作 説明 {quality}.gif");
        var svc = new GifExportService(fx.Runner);
        var result = await svc.ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            Cuts, Subs, new SubtitleStyle(), quality, output), null, CancellationToken.None);

        Assert.True(fx.Metadata.HasAudio); // 音声付きの動画からでも GIF を作れる
        var head = new byte[6];
        await using (var fs = File.OpenRead(output)) Assert.Equal(6, await fs.ReadAsync(head));
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(head));
        Assert.Equal(result.FileSize, new FileInfo(output).Length);
        Assert.False(File.Exists(Path.Combine(fx.Dir, $"操作 説明 {quality}.partial.gif")));

        var probe = await fx.Runner.RunAsync(new ProcessRequest(Ffprobe,
            ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=width,height,nb_read_frames,codec_name", "-of", "csv=p=0", output]));
        var parts = probe.StandardOutput.Trim().Split(',');
        Assert.Equal("gif", parts[0]);
        Assert.Equal(640, int.Parse(parts[1])); // 元が小さいので拡大しない
        var frames = int.Parse(parts[3]);
        Assert.InRange(frames, 11 * fps - fps, 11 * fps + fps); // カット後 11 秒 × GIF の FPS（録画の 30fps とは別）

        // 字幕が GIF にも焼き込まれている（字幕の時間帯のフレームの下側に白い文字）。
        var png = await ExtractFrameAsync(output, 3.7);
        Assert.True(WhitePixelsInBottom(png) > 150, "GIF に字幕が無い");
    }

    [Fact]
    public async Task GifExportCancelRemovesPartial()
    {
        var output = Path.Combine(fx.Dir, "キャンセル.gif");
        using var cts = new CancellationTokenSource();
        var progress = new Progress<VideoExportProgress>(p => { if (p.Ratio > 0) cts.Cancel(); });
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GifExportService(fx.Runner).ExportAsync(
            new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, [], [], new SubtitleStyle(), GifQuality.High, output),
            progress, cts.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(fx.Dir, "キャンセル.partial.gif")));
    }

    private RecordingService CreateRecorder() => new(new FFmpegRecordingProcessFactory(), fx.Runner, new MediaMetadataService(fx.Runner));

    [Fact]
    public async Task ScreenRecordingRegionWithPauseProducesMp4WithoutPausedTime()
    {
        var output = Path.Combine(fx.Dir, "録画 テスト", "rec.mp4");
        var svc = CreateRecorder();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        await svc.StartAsync(new RecordingStartRequest(Ffmpeg, Ffprobe,
            new ScreenRecordingOptions(new CaptureRegion(0, 0, 320, 240), 30, true, null), output, new RecordingInfo()));
        await Task.Delay(1500);
        await svc.PauseAsync();
        var pause = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(3000); // 一時停止中（動画に含めない）
        pause.Stop();
        await svc.ResumeAsync();
        await Task.Delay(1500);
        var elapsed = svc.Elapsed;
        var result = await svc.StopAsync();
        wall.Stop();

        Assert.Equal(2, result.SegmentCount);
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.Equal("h264", meta.VideoCodec);
        Assert.Equal((320, 240), (meta.Width, meta.Height));
        Assert.Equal(30, meta.Fps, 0.5);
        Assert.False(meta.HasAudio); // マイク OFF
        // 動画の長さ ≒ 画面に出す経過時間。一時停止の 3 秒は含まない（全体の時間 − 一時停止より短い）。
        Assert.InRange(meta.Duration, elapsed.TotalSeconds - 0.7, elapsed.TotalSeconds + 0.7);
        Assert.True(meta.Duration < (wall.Elapsed - pause.Elapsed).TotalSeconds + 0.3,
            $"一時停止中の時間が含まれている: 動画 {meta.Duration:0.00}s / 全体 {wall.Elapsed.TotalSeconds:0.00}s / 一時停止 {pause.Elapsed.TotalSeconds:0.00}s");
        Assert.True(meta.Duration > 2.5);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(output)!, ".mvs-rec-*")); // 作業フォルダーは消える
    }

    [Fact]
    public async Task ScreenRecordingWithAudioInputProducesMp4WithAudio()
    {
        // 実マイクの代わりに lavfi の信号音を実時間で入力する（マイク入力と同じ引数の経路で音声付き MP4 になるかの確認）。
        var output = Path.Combine(fx.Dir, "録画 音声", "rec.mp4");
        var audio = new AudioInputSpec("lavfi", "sine=frequency=440:sample_rate=48000", "テスト信号", ["-re"]);
        var svc = CreateRecorder();
        await svc.StartAsync(new RecordingStartRequest(Ffmpeg, Ffprobe,
            new ScreenRecordingOptions(new CaptureRegion(0, 0, 320, 240), 15, false, audio), output, new RecordingInfo { Microphone = true }));
        await Task.Delay(2000);
        await svc.PauseAsync();
        await svc.ResumeAsync();
        await Task.Delay(1000);
        var elapsed = svc.Elapsed;
        var result = await svc.StopAsync();

        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.True(meta.HasAudio);
        Assert.Equal("aac", meta.AudioCodec);
        Assert.Equal(15, meta.Fps, 0.5);
        Assert.InRange(meta.Duration, elapsed.TotalSeconds - 0.8, elapsed.TotalSeconds + 0.8);
        Assert.True(result.Info.Microphone);
        Assert.Equal(2, result.SegmentCount);
    }

    [Fact]
    public async Task ScreenRecordingStartFailureIsReported()
    {
        // 存在しないマイクを指定すると、ffmpeg はすぐ終了する → 分かりやすいエラーにする。
        var output = Path.Combine(fx.Dir, "録画 失敗", "rec.mp4");
        var svc = CreateRecorder();
        var ex = await Assert.ThrowsAsync<RecordingException>(() => svc.StartAsync(new RecordingStartRequest(Ffmpeg, Ffprobe,
            new ScreenRecordingOptions(new CaptureRegion(0, 0, 320, 240), 30, true, AudioInputSpec.DirectShow("存在しないマイク 12345", "x")),
            output, new RecordingInfo())));
        Assert.Contains("開始できません", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Detail));
        Assert.Equal(RecordingState.Failed, svc.State);
        Assert.False(File.Exists(output));
    }
}
