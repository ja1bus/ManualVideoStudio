using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// 動画冒頭の黒背景タイトル（v1.15）を実 ffmpeg で書き出して確認する。テスト動画は 640x360・12 秒・音声あり
/// （0～3 秒 testsrc2 + 440Hz / 3～9 秒 紺 + 無音 / 9～12 秒 testsrc2 + 660Hz）。
/// 書き出した動画から 1 コマを切り出し、タイトル区間の黒と白い文字、フェードの明るさ、字幕のずれ、長さ、音声の無音区間を調べる。
/// </summary>
public sealed class TitleIntegrationTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private sealed record Bgra(byte[] Data, int W, int H)
    {
        public double MeanLuma()
        {
            double sum = 0;
            for (int i = 0; i < Data.Length; i += 4) sum += 0.114 * Data[i] + 0.587 * Data[i + 1] + 0.299 * Data[i + 2];
            return sum / (Data.Length / 4);
        }

        /// <summary>範囲内の白っぽい画素の数。</summary>
        public int WhiteCount(int x0, int y0, int x1, int y1)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 4;
                    if (Data[i] > 200 && Data[i + 1] > 200 && Data[i + 2] > 200) n++;
                }
            return n;
        }
    }

    private async Task<Bgra> FrameAsync(string video, double time)
    {
        var png = Path.Combine(fx.Dir, $"frame-{Guid.NewGuid():N}");
        var svc = new FrameExtractionService(fx.Runner, Path.Combine(fx.Dir, "cache-title"));
        await svc.ExtractAsync(Ffmpeg, video, time, png, ImageFormat.Png, 2, 0, default);
        using var stream = File.OpenRead(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bmp = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return new Bgra(px, bmp.PixelWidth, bmp.PixelHeight);
    }

    private async Task<double> DurationAsync(string path)
        => (await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, path)).Duration;

    private async Task<VideoTitleImage> TitleImageAsync(VideoTitle title, double mainDuration, int w = 640, int h = 360)
    {
        VideoTitleImage? image = null;
        string? error = null;
        await StaThread.RunAsync(() => (image, error) = TitleRenderer.BuildExportImage(title, w, h, mainDuration));
        Assert.Null(error);
        return image!;
    }

    private static VideoTitle T(string text = "操作説明", int seconds = 2, bool fade = true)
        => new() { Text = text, Seconds = seconds, FadeIn = fade };

    private static double MainLength(IReadOnlyList<CutSegment> cuts, double duration = 12)
        => EditTimelineCalculator.OutputDuration(EditTimelineCalculator.ComputeKeepRanges(duration, cuts));

    private async Task<string> ExportAsync(string name, IReadOnlyList<CutSegment> cuts, VideoTitle? title, IReadOnlyList<SubtitleSegment>? subs = null,
        string? input = null, VideoMetadata? meta = null, double transition = 0)
    {
        meta ??= fx.Metadata;
        var output = Path.Combine(fx.Dir, name);
        var image = title is null ? null : await TitleImageAsync(title, MainLength(cuts, meta.Duration));
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, input ?? fx.VideoPath, meta,
            cuts, output, new VideoEncodeOptions { Preset = "ultrafast" }, subs, new SubtitleStyle(), transition, Title: image), null, CancellationToken.None);
        Assert.True(File.Exists(output));
        return output;
    }

    private async Task<(double Mean, double Max)> VolumeAsync(string video, double from, double length)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            ["-hide_banner", "-nostdin", "-ss", from.ToString(CultureInfo.InvariantCulture), "-t", length.ToString(CultureInfo.InvariantCulture), "-i", video,
             "-vn", "-af", "volumedetect", "-f", "null", "-"]));
        double Get(string key)
        {
            var m = Regex.Match(r.StandardError, key + @":\s*(-?inf|-?[\d.]+) dB");
            if (!m.Success) return double.NegativeInfinity;
            return m.Groups[1].Value.Contains("inf") ? double.NegativeInfinity : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return (Get("mean_volume"), Get("max_volume"));
    }

    private async Task<(double Video, double Audio)> StreamDurationsAsync(string path)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", path]));
        double v = 0, a = 0;
        foreach (var line in r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',');
            if (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) continue;
            if (p[0] == "video") v = d; else if (p[0] == "audio") a = d;
        }
        return (v, a);
    }

    [Fact]
    public async Task Mp4TitleThenFadeInThenNormal()
    {
        var output = await ExportAsync("title-fade.mp4", [], T());
        Assert.InRange(await DurationAsync(output), 13.85, 14.2);   // 本編 12 + タイトル 2

        // タイトル区間: ほぼ黒で、文字（白）がある。
        var title = await FrameAsync(output, 1.0);
        Assert.True(title.MeanLuma() < 25, $"タイトル区間の明るさ {title.MeanLuma():0.0}");
        Assert.True(title.WhiteCount(100, 100, 540, 220) > 150, "タイトルの文字（白）が見えない");

        // 2～2.3 秒: 本編が進みながら黒から明るくなる。
        var start = (await FrameAsync(output, 2.03)).MeanLuma();
        var mid = (await FrameAsync(output, 2.15)).MeanLuma();
        var full = (await FrameAsync(output, 2.6)).MeanLuma();
        Assert.True(start < mid && mid < full, $"{start:0.0} < {mid:0.0} < {full:0.0}");
        Assert.True(start < full * 0.4, $"フェードの始まり {start:0.0} / 通常 {full:0.0}");
        Assert.True(mid < full * 0.95 && mid > start, $"フェードの途中 {mid:0.0}");

        // 本編はタイトルの後ろから通常再生（タイトルの文字は本編に重ならない）。
        Assert.True((await FrameAsync(output, 2.6)).WhiteCount(100, 100, 540, 220) < 2000);
    }

    [Fact]
    public async Task FadeOffSwitchesDirectlyAndKeepsTheSameLength()
    {
        var on = await ExportAsync("fade-on.mp4", [], T(fade: true));
        var off = await ExportAsync("fade-off.mp4", [], T(fade: false));
        Assert.InRange(Math.Abs(await DurationAsync(on) - await DurationAsync(off)), 0, 0.1);   // フェードで長さは変わらない

        var justAfter = (await FrameAsync(off, 2.05)).MeanLuma();
        var later = (await FrameAsync(off, 2.6)).MeanLuma();
        Assert.True(justAfter > later * 0.5, $"フェードなし: 直後 {justAfter:0.0} / 通常 {later:0.0}");
        Assert.True((await FrameAsync(on, 2.05)).MeanLuma() < justAfter * 0.5, "フェードありは直後が暗い");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task EveryChoiceOfSecondsAddsExactlyThatMuch(int seconds)
    {
        var output = await ExportAsync($"sec-{seconds}.mp4", [], T(seconds: seconds));
        Assert.InRange(await DurationAsync(output), 12 + seconds - 0.15, 12 + seconds + 0.2);
        Assert.True((await FrameAsync(output, seconds - 0.3)).MeanLuma() < 25, "タイトル区間の最後も黒背景");
    }

    [Fact]
    public async Task NoTitleExportIsUnchanged()
    {
        var output = await ExportAsync("no-title.mp4", [], null);
        Assert.InRange(await DurationAsync(output), 11.85, 12.2);
        Assert.True((await FrameAsync(output, 0.03)).MeanLuma() > 30, "タイトルなしには冒頭フェードを適用しない");
    }

    [Fact]
    public async Task AudioIsSilentDuringTitleAndStartsWithMainWithoutFade()
    {
        var output = await ExportAsync("title-audio.mp4", [], T());
        var title = await VolumeAsync(output, 0.1, 1.7);
        Assert.True(title.Max < -60, $"タイトル区間は無音 {title.Max} dB");
        var main = await VolumeAsync(output, 2.05, 0.9);   // 本編の最初の 440Hz（フェードの区間も含む）
        Assert.True(main.Max > -12, $"本編の音声が先頭から出る {main.Max} dB");
        var (v, a) = await StreamDurationsAsync(output);
        Assert.InRange(Math.Abs(v - a), 0, 0.15);          // 映像と音声の長さが揃う
    }

    [Fact]
    public async Task SubtitleAndAudioStayOnMainTimeShiftedByTitleSeconds()
    {
        // 3～9 秒（紺・無音）を残し、先頭の 0～3 秒を削除 → 本編 9 秒（紺 6 秒 + testsrc2 3 秒）。字幕は本編 1～3 秒。
        // タイトル 2 秒なので、完成動画では 3～5 秒に出る。
        var cuts = new[] { new CutSegment { Start = 0, End = 3 } };
        var subs = new[] { new SubtitleSegment { Start = 1, End = 3, Text = "確認用の字幕", FontSize = 72 } };
        var output = await ExportAsync("title-sub.mp4", cuts, T(), subs);
        Assert.InRange(await DurationAsync(output), 10.85, 11.2);   // 本編 9 + タイトル 2

        int Subtitle(Bgra f) => f.WhiteCount(100, 250, 540, 355);
        Assert.Equal(0, Subtitle(await FrameAsync(output, 2.6)));   // 本編 0.6 秒: まだ字幕なし
        Assert.True(Subtitle(await FrameAsync(output, 3.5)) > 100, "本編 1.5 秒（完成動画 3.5 秒）に字幕が出る");
        Assert.True(Subtitle(await FrameAsync(output, 4.5)) > 100, "本編 2.5 秒（完成動画 4.5 秒）にも出る");
        Assert.Equal(0, Subtitle(await FrameAsync(output, 5.5)));   // 本編 3.5 秒: 終わっている
        Assert.Equal(0, Subtitle(await FrameAsync(output, 1.0)));   // タイトル区間に字幕は出ない
    }

    [Fact]
    public async Task SubtitleAtTheVeryStartOfMainAppearsRightAfterTheTitle()
    {
        var cuts = new[] { new CutSegment { Start = 0, End = 3 } };
        var subs = new[] { new SubtitleSegment { Start = 0, End = 1.5, Text = "冒頭の字幕", FontSize = 72 } };
        var output = await ExportAsync("title-sub-first.mp4", cuts, T(), subs);
        int Subtitle(Bgra f) => f.WhiteCount(100, 250, 540, 355);
        Assert.Equal(0, Subtitle(await FrameAsync(output, 1.0)));
        Assert.True(Subtitle(await FrameAsync(output, 3.0)) > 100, "本編 1.0 秒（完成動画 3.0 秒）");
        Assert.Equal(0, Subtitle(await FrameAsync(output, 3.9)));   // 本編 1.9 秒: 終わっている
    }

    [Fact]
    public async Task FadeAppliesToTheTrimmedStartOfMain()
    {
        // 先頭 1 秒を削除 → 本編 11 秒（元動画 1 秒目から）。フェードは削除後の先頭にかかる。
        var cuts = new[] { new CutSegment { Start = 0, End = 1 } };
        var output = await ExportAsync("title-trim.mp4", cuts, T());
        Assert.InRange(await DurationAsync(output), 12.85, 13.2);
        var start = (await FrameAsync(output, 2.03)).MeanLuma();
        var full = (await FrameAsync(output, 2.6)).MeanLuma();
        Assert.True(start < full * 0.4, $"{start:0.0} / {full:0.0}");
    }

    [Fact]
    public async Task ShortMainStillExportsWithShrunkFade()
    {
        var cuts = new[] { new CutSegment { Start = 0.2, End = 12 } };   // 本編 0.2 秒
        var output = await ExportAsync("title-short.mp4", cuts, T());
        Assert.InRange(await DurationAsync(output), 2.0, 2.45);
        Assert.True((await FrameAsync(output, 1.0)).MeanLuma() < 25);
    }

    [Fact]
    public async Task CrossfadeBetweenCutsAndTitleLengthAdd()
    {
        var cuts = new[] { new CutSegment { Start = 3, End = 9 } };   // 本編 6 秒（クロスフェードあり）
        var output = await ExportAsync("title-xfade.mp4", cuts, T(seconds: 3), transition: ExportSettings.CutTransitionSeconds);
        Assert.InRange(await DurationAsync(output), 8.85, 9.25);   // 6 + 3
    }

    [Fact]
    public async Task SourceWithoutAudioProducesVideoOnlyFileOfTheSameLength()
    {
        var silent = Path.Combine(fx.Dir, "no-audio-source.mp4");
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", fx.VideoPath, "-an", "-c:v", "copy", silent]));
        Assert.True(r.Succeeded, r.ErrorTail());
        var meta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, silent);
        Assert.False(meta.HasAudio);

        var output = await ExportAsync("title-noaudio.mp4", [], T(), input: silent, meta: meta);
        var outMeta = await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, output);
        Assert.False(outMeta.HasAudio);
        Assert.InRange(outMeta.Duration, 13.85, 14.2);
        Assert.True((await FrameAsync(output, 1.0)).WhiteCount(100, 100, 540, 220) > 150);
    }

    [Fact]
    public async Task LargerFontAndSecondLineAreDrawnInTheExport()
    {
        var title = new VideoTitle { Text = "一行目\n二行目", Seconds = 2, FontSize = 120 };
        var output = await ExportAsync("title-big.mp4", [], title);
        var f = await FrameAsync(output, 1.0);
        Assert.True(f.WhiteCount(0, 120, 640, 175) > 100, "1 行目");
        Assert.True(f.WhiteCount(0, 200, 640, 290) > 100, "2 行目");
        Assert.Equal(0, f.WhiteCount(0, 0, 640, 100));              // 上端（高さの 1/3 = 120px）より上には描かない
    }

    [Fact]
    public async Task GifIncludesTitleAndTitleLength()
    {
        var output = Path.Combine(fx.Dir, "title.gif");
        var image = await TitleImageAsync(T(), 12);
        var result = await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            [], [], new SubtitleStyle(), GifQuality.Light, output, Title: image), null, CancellationToken.None);
        Assert.True(File.Exists(result.OutputPath));
        Assert.InRange(result.OutputDuration, 13.99, 14.01);   // 結果の長さはタイトルを含む
        Assert.InRange(await DurationAsync(output), 13.5, 14.5);
        var title = await FrameAsync(output, 1.0);
        Assert.True(title.MeanLuma() < 30, $"GIF のタイトル区間 {title.MeanLuma():0.0}");
        Assert.True(title.WhiteCount(0, 0, title.W, title.H) > 50, "GIF のタイトルに白い文字");
        Assert.True((await FrameAsync(output, 2.8)).MeanLuma() > 30, "GIF の本編");
        Assert.Empty(Directory.GetFiles(fx.Dir, "*.partial*"));
    }

    [Fact]
    public async Task GifFadeOffAndOnHaveTheSameLength()
    {
        async Task<double> Run(string name, bool fade)
        {
            var output = Path.Combine(fx.Dir, name);
            var image = await TitleImageAsync(T(fade: fade), 12);
            var result = await new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
                [], [], new SubtitleStyle(), GifQuality.Light, output, Title: image), null, CancellationToken.None);
            return await DurationAsync(result.OutputPath);
        }
        Assert.InRange(Math.Abs(await Run("g-on.gif", true) - await Run("g-off.gif", false)), 0, 0.2);
    }

    [Fact]
    public async Task ProgressReachesTheLengthIncludingTheTitle()
    {
        var output = Path.Combine(fx.Dir, "title-progress.mp4");
        var image = await TitleImageAsync(T(seconds: 3), 12);
        var last = new List<VideoExportProgress>();
        var progress = new Progress<VideoExportProgress>(p => last.Add(p));
        var result = await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata,
            [], output, new VideoEncodeOptions { Preset = "ultrafast" }, Title: image), progress, CancellationToken.None);
        await Task.Delay(300);   // Progress<T> は同期コンテキストへ投げるため少し待つ
        Assert.Equal(15, result.OutputDuration, 3);
        Assert.All(last, p => Assert.Equal(15, p.TotalSeconds, 3));
    }

    [Fact]
    public async Task FailureLeavesNoPartialFileOrWorkFolder()
    {
        var output = Path.Combine(fx.Dir, "title-fail.mp4");
        var before = Directory.GetDirectories(Path.GetTempPath(), "manualvideostudio-sub-*").ToHashSet();
        var broken = new VideoTitleImage(2, 0.3, [1, 2, 3, 4]);   // PNG として読めない
        await Assert.ThrowsAsync<FFmpegException>(() => new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg,
            fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, [], output, new VideoEncodeOptions { Preset = "ultrafast" }, Title: broken),
            null, CancellationToken.None));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(fx.Dir, "title-fail*"));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "manualvideostudio-sub-*").Except(before));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialFileOrWorkFolder()
    {
        var output = Path.Combine(fx.Dir, "title-cancel.mp4");
        var before = Directory.GetDirectories(Path.GetTempPath(), "manualvideostudio-sub-*").ToHashSet();
        var image = await TitleImageAsync(T(), 12);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg,
            fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, [], output, new VideoEncodeOptions { Preset = "ultrafast" }, Title: image),
            null, cts.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(fx.Dir, "title-cancel*"));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "manualvideostudio-sub-*").Except(before));
    }

    [Fact]
    public async Task GifFailureLeavesNoPartialFile()
    {
        var output = Path.Combine(fx.Dir, "title-fail.gif");
        var broken = new VideoTitleImage(2, 0.3, [1, 2, 3, 4]);
        await Assert.ThrowsAsync<FFmpegException>(() => new GifExportService(fx.Runner).ExportAsync(new GifExportRequest(Ffmpeg,
            fx.Capabilities.MajorVersion, fx.VideoPath, fx.Metadata, [], [], new SubtitleStyle(), GifQuality.Light, output, Title: broken),
            null, CancellationToken.None));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(fx.Dir, "title-fail.partial*"));
    }
}
