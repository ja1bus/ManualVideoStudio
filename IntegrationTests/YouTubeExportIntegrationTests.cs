using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;

namespace ManualVideoStudio.IntegrationTests;

/// <summary>
/// v1.22 §44.5 YouTube 用に書き出し（字幕なし MP4＋SRT）を実 ffmpeg で確かめる（試験表 T-60）。
/// 灰色一色・音声つき 20 秒・1280x720 の合成動画（30fps と 30000/1001fps）に、カット 15〜16 秒・字幕 3 件・タイトル 3 秒（フェードなし）を付け、
/// 主画面と同じ形の要求（YouTube 用は字幕に空の一覧）で MP4 を作る。比べる通常の書き出しは同じ要求に字幕を渡したもの。
/// 本編の始まりはタイトルの黒い背景から灰色に変わるコマ（左上の隅の明るさ）、字幕の有無は画面の下 1/4 の明るさで見る。
/// </summary>
public sealed class YouTubeExportIntegrationTests(MediaFixture fx, Xunit.Abstractions.ITestOutputHelper log) : IClassFixture<MediaFixture>
{
    private string Ffmpeg => fx.Location.Ffmpeg.Path!;
    private string Ffprobe => fx.Location.Ffprobe.Path!;

    private static readonly SubtitleSegment[] Subs =
    [
        new() { Start = 1.0, End = 2.5, Text = "設定を開きます" },
        new() { Start = 3.0, End = 5.0, Text = "保存を押します" },
        new() { Start = 15.5, End = 17.0, Text = "つなぎ目の後" },   // カット後の時間軸。つなぎ目 15.0 をまたぐ
    ];

    private async Task<(string Path, VideoMetadata Meta)> SourceAsync(string rate)
    {
        var path = Path.Combine(fx.Dir, $"yt-src-{rate.Replace('/', '_')}.mp4");
        if (!File.Exists(path))
        {
            var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            [
                "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", $"color=c=gray:s=1280x720:r={rate}", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "20", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", path,
            ]));
            Assert.True(r.Succeeded, r.ErrorTail());
        }
        return (path, await new MediaMetadataService(fx.Runner).ProbeAsync(Ffprobe, path));
    }

    private async Task<(string Video, string? Srt)> ExportAsync(string name, string input, VideoMetadata meta, bool youtube)
    {
        var dir = Path.Combine(fx.Dir, "yt-" + name);
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, youtube ? "y.mp4" : "b.mp4");
        var title = new VideoTitle { Text = "操作マニュアル", Seconds = 3, FadeIn = false };
        IReadOnlyList<CutSegment> cuts = [new CutSegment { Start = 15, End = 16 }];
        VideoTitleImage? image = null;
        string? error = null;
        await StaThread.RunAsync(() => (image, error) = TitleRenderer.BuildExportImage(title, meta.Width & ~1, meta.Height & ~1, 19));
        Assert.Null(error);
        // 主画面の CreateVideoExportRequest と同じ形: YouTube 用は字幕に空の一覧、通常は字幕の一覧。
        await new VideoExportService(fx.Runner).ExportAsync(new VideoExportRequest(Ffmpeg, fx.Capabilities.MajorVersion, input, meta, cuts, output,
            new VideoEncodeOptions { Preset = "ultrafast" }, youtube ? [] : Subs.ToList(), new SubtitleStyle(), 0.3, null, null, image), null, CancellationToken.None);
        Assert.True(File.Exists(output));
        if (!youtube) return (output, null);
        var srt = Path.ChangeExtension(output, ".srt");
        SrtExport.Write(srt, SrtExport.Build(Subs, SrtExport.TitleOffsetSeconds(title, meta.Fps)));
        return (output, srt);
    }

    /// <summary>各コマの時刻と、指定した範囲の明るさ（YAVG）と明るさの幅（YMAX−YMIN）。</summary>
    private async Task<List<(double Time, double Y, double Spread)>> LumaAsync(string video, string crop)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffmpeg,
            ["-hide_banner", "-nostdin", "-i", video, "-vf", $"{crop},signalstats,metadata=print", "-an", "-f", "null", "-"])
        { MaxCapturedChars = 16_000_000 });
        Assert.True(r.Succeeded, r.ErrorTail());
        var list = new List<(double, double, double)>();
        double? t = null, avg = null, min = null;
        static double V(Match m) => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        foreach (var line in r.StandardError.Split('\n'))
        {
            var m = Regex.Match(line, @"pts_time:([\d.]+)");
            if (m.Success) { t = V(m); avg = min = null; continue; }
            if (Regex.Match(line, @"lavfi\.signalstats\.YAVG=([\d.]+)") is { Success: true } a) avg = V(a);
            else if (Regex.Match(line, @"lavfi\.signalstats\.YMIN=([\d.]+)") is { Success: true } lo) min = V(lo);
            else if (Regex.Match(line, @"lavfi\.signalstats\.YMAX=([\d.]+)") is { Success: true } hi && t is { } time && avg is { } y && min is { } mn)
                list.Add((time, y, V(hi) - mn));
        }
        Assert.NotEmpty(list);
        return list;
    }

    private const string Band = "crop=iw:ih*0.25:0:ih*0.75";
    private const string Corner = "crop=iw*0.1:ih*0.1:0:0";

    private async Task<(string[] Types, long Frames, double Duration)> ProbeAsync(string video)
    {
        var r = await fx.Runner.RunAsync(new ProcessRequest(Ffprobe,
            ["-v", "error", "-count_frames", "-show_entries", "stream=codec_type,nb_read_frames:format=duration", "-of", "default=nw=1", video]));
        Assert.True(r.Succeeded, r.ErrorTail());
        var types = Regex.Matches(r.StandardOutput, @"codec_type=(\w+)").Select(m => m.Groups[1].Value).ToArray();
        var frames = long.Parse(Regex.Match(r.StandardOutput, @"nb_read_frames=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var duration = double.Parse(Regex.Match(r.StandardOutput, @"duration=([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        return (types, frames, duration);
    }

    [Theory] // T-60（30fps）と、分数 fps（30000/1001）での同じ確認
    [InlineData("30", "00:00:04,000 --> 00:00:05,500", "00:00:06,000 --> 00:00:08,000", "00:00:18,500 --> 00:00:20,000", 3.000)]
    [InlineData("30000/1001", "00:00:04,003 --> 00:00:05,503", "00:00:06,003 --> 00:00:08,003", "00:00:18,503 --> 00:00:20,003", 3.003)]
    public async Task VideoHasNoBurnedSubtitlesAndSrtMatchesTheVideoTimeline(string rate, string a, string b, string c, double mainStart)
    {
        var (src, meta) = await SourceAsync(rate);
        var name = rate.Replace('/', '_');
        var (y, srt) = await ExportAsync(name, src, meta, youtube: true);
        var (burned, _) = await ExportAsync(name, src, meta, youtube: false);
        var frame = 1 / meta.Fps;

        // SRT: タイトルの実際の長さを足した 3 ブロック（AC-22）
        var lines = File.ReadAllText(srt!, Encoding.UTF8).Split("\r\n");
        Assert.Equal([a, b, c], lines.Where(l => l.Contains(" --> ", StringComparison.Ordinal)));

        // 字幕のストリームが無く、長さ・コマ数は通常の書き出しと同じ（AC-21）
        var py = await ProbeAsync(y);
        var pb = await ProbeAsync(burned);
        Assert.Equal(["video", "audio"], py.Types);
        Assert.Equal(pb.Frames, py.Frames);
        Assert.Equal(pb.Duration, py.Duration, 0.05);
        Assert.Equal(22.0 + (mainStart - 3.0), py.Duration, 0.1);

        // 本編の始まり（タイトルの黒から灰色へ）が SRT に足した秒数と一致する
        var corner = await LumaAsync(y, Corner);
        var start = corner.First(x => x.Y > 60).Time;
        Assert.InRange(start, mainStart - 0.001, mainStart + 0.001); // 試験表 T-60 の ±0.001 秒

        // 字幕の有無は、帯の中の明るさの幅（YMAX−YMIN）で見る。灰色一色の素材では字幕が無ければ幅はほぼ 0、
        // 焼き込みの字幕（白い文字と黒い縁）があれば 100 を超える。帯の平均（YAVG）は字幕で 1.5 前後しか変わらないため判定に使わない
        // （2回目レビュー R2。2026-11-16 実測は試験の出力メッセージと確認記録を参照）。
        const double ShownSpread = 50;
        var bandY = (await LumaAsync(y, Band)).Where(x => x.Time >= mainStart + 0.05).ToList();
        var bandB = (await LumaAsync(burned, Band)).Where(x => x.Time >= mainStart + 0.05).ToList(); // タイトルの黒い区間を除く
        static bool Shown((double Time, double Y, double Spread) x) => x.Spread >= ShownSpread;
        var info = $"字幕なし: 幅の最大 {bandY.Max(x => x.Spread):0.0}・平均の幅 {bandY.Max(x => x.Y) - bandY.Min(x => x.Y):0.000}／"
                 + $"焼き込み: 幅の最大 {bandB.Max(x => x.Spread):0.0}・幅の最小 {bandB.Min(x => x.Spread):0.0}・字幕ありのコマ {bandB.Count(Shown)}／本編の始まり {start:0.0000}";
        Assert.True(bandY.All(x => x.Spread < 10), "字幕なしの MP4 に字幕らしい明るさの幅がある。" + info);
        // 焼き込み版は A・B・C の各区間の中で字幕が出て、区間の外（前後 1 コマを除く）では出ない
        var offset = mainStart;
        (double S, double E)[] spans = [.. Subs.Select(s => (s.Start + offset, s.End + offset))];
        foreach (var (s, e) in spans)
            Assert.True(bandB.Where(x => x.Time > s + frame && x.Time < e - frame).All(Shown), $"焼き込み版で {s:0.000}〜{e:0.000} に字幕が出ていないコマがある。" + info);
        Assert.True(bandB.Where(x => spans.All(p => x.Time < p.S - frame - 0.002 || x.Time > p.E + frame + 0.002)).All(x => !Shown(x)),
            "焼き込み版で字幕の区間の外に字幕らしいコマがある。" + info);
        log.WriteLine(info);

        // 焼き込み版で字幕 A が出る・消える時刻が、SRT の A と 1 コマ以内で一致する（同じ時刻に出すべき字幕）
        var aStart = ParseMs(a[..12]) / 1000.0;
        var aEnd = ParseMs(a[^12..]) / 1000.0;
        var firstShown = bandB.First(Shown).Time;
        var hiddenAfter = bandB.First(x => x.Time > firstShown && !Shown(x)).Time;
        Assert.InRange(firstShown, aStart - frame - 0.002, aStart + frame + 0.002);
        Assert.InRange(hiddenAfter, aEnd - frame - 0.002, aEnd + frame + 0.002);
        // C（つなぎ目の後）も焼き込み版の表示と一致する
        var cStart = ParseMs(c[..12]) / 1000.0;
        var cShown = bandB.First(x => x.Time > cStart - 1 && Shown(x)).Time;
        Assert.InRange(cShown, cStart - frame - 0.002, cStart + frame + 0.002);
    }

    private static long ParseMs(string t)
    {
        var x = t.Split(':', ',');
        return long.Parse(x[0], CultureInfo.InvariantCulture) * 3_600_000 + long.Parse(x[1], CultureInfo.InvariantCulture) * 60_000
            + long.Parse(x[2], CultureInfo.InvariantCulture) * 1000 + long.Parse(x[3], CultureInfo.InvariantCulture);
    }
}
