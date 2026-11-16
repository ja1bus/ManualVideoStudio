using System.Text;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Recording;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Services;
using ManualVideoStudio.Services.Recording;
using ManualVideoStudio.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualVideoStudio.Tests;

/// <summary>記録だけする試験用のロガー（v1.22）。</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}

/// <summary>v1.22 字幕ファイル（SRT）の書き出し: 本文・時刻・タイトルの秒数・既定の保存先（試験表 T-01〜T-12）。</summary>
public class SrtExportTests
{
    private static SubtitleSegment S(double start, double end, string text) => new() { Start = start, End = end, Text = text };

    private static string[] TimeLines(string srt)
        => srt.Split("\r\n").Where(l => l.Contains(" --> ", StringComparison.Ordinal)).ToArray();

    private static long Ms(string time)
    {
        var p = time.Split(':', ',');
        return long.Parse(p[0]) * 3_600_000 + long.Parse(p[1]) * 60_000 + long.Parse(p[2]) * 1000 + long.Parse(p[3]);
    }

    [Fact] // T-01
    public void BlocksAreSortedNumberedAndSeparatedByOneBlankLine()
    {
        var doc = SrtExport.Build([S(5, 7, "C"), S(1, 2.5, "A"), S(2.5, 4, "B")]);
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,500\r\nA\r\n\r\n2\r\n00:00:02,500 --> 00:00:04,000\r\nB\r\n\r\n3\r\n00:00:05,000 --> 00:00:07,000\r\nC\r\n\r\n", doc.Text);
        Assert.Equal(3, doc.Count);
    }

    [Fact] // T-02
    public void SameStartIsOrderedByEndThenByGivenOrder()
    {
        var doc = SrtExport.Build([S(1, 3, "X"), S(1, 2, "Y"), S(4, 5, "P1"), S(4, 5, "P2")]);
        var bodies = doc.Text.Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split("\r\n")).ToList();
        Assert.Equal(["1", "2", "3", "4"], bodies.Select(b => b[0]));
        Assert.Equal(["Y", "X", "P1", "P2"], bodies.Select(b => b[2]));
    }

    [Theory] // T-03
    [InlineData(0.0, 1.0, "00:00:00,000 --> 00:00:01,000")]
    [InlineData(59.999, 60.0, "00:00:59,999 --> 00:01:00,000")]
    [InlineData(3725.5, 3726.0, "01:02:05,500 --> 01:02:06,000")]
    [InlineData(360000.0, 360001.0, "100:00:00,000 --> 100:00:01,000")]
    [InlineData(0.0625, 1.5625, "00:00:00,063 --> 00:00:01,563")]
    [InlineData(-0.5, 1.0, "00:00:00,000 --> 00:00:01,000")]
    public void TimesAreFormatted(double start, double end, string expected)
        => Assert.Equal(expected, Assert.Single(TimeLines(SrtExport.Build([S(start, end, "x")]).Text)));

    [Fact] // T-04
    public void TitleOffsetIsTheActualTitleLengthInTheExportedVideo()
    {
        Assert.Equal(0, SrtExport.TitleOffsetSeconds(null, 30));
        Assert.Equal(0, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "", Seconds = 5 }, 30));
        Assert.Equal(0, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "   ", Seconds = 5 }, 30));
        const double ntsc = 30000.0 / 1001.0;
        Assert.Equal(3.000, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3 }, 30), 4);
        Assert.Equal(3.003, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3 }, ntsc), 3);
        Assert.Equal(3.000, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3 }, 0), 4);
        Assert.Equal(3.000, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3 }, -1), 4);
        Assert.Equal(SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3, FadeIn = true }, ntsc),
            SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 3, FadeIn = false }, ntsc));
        Assert.Equal(1.000, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 1 }, 30), 4);
        Assert.Equal(5.000, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = 5 }, 30), 4);

        // 書き出しのタイトルの長さ（TitleFrameCount ÷ fps）と同じ。
        foreach (var (seconds, fps) in new[] { (3, 30.0), (3, ntsc), (1, 30.0), (5, 30.0), (3, 25.0), (2, 59.94) })
        {
            var expected = FFmpegArguments.TitleFrameCount(new TitleFilterPlan("t", seconds, 0.3, fps)) / fps;
            Assert.Equal(expected, SrtExport.TitleOffsetSeconds(new VideoTitle { Text = "操作", Seconds = seconds }, fps), 9);
        }
    }

    [Fact] // T-05
    public void OffsetIsAddedToEveryTimeWithoutChangingTheSubtitles()
    {
        var s = S(5, 8, "本編");
        Assert.Equal("00:00:05,000 --> 00:00:08,000", TimeLines(SrtExport.Build([s], 0).Text)[0]);
        Assert.Equal("00:00:07,000 --> 00:00:10,000", TimeLines(SrtExport.Build([s], 2.0).Text)[0]);
        Assert.Equal("00:00:08,003 --> 00:00:11,003", TimeLines(SrtExport.Build([s], 3.003).Text)[0]);
        Assert.Equal("00:00:03,002 --> 00:00:04,002", TimeLines(SrtExport.Build([S(1, 2, "a")], 2.002).Text)[0]);
        Assert.Equal((5.0, 8.0), (s.Start, s.End));
    }

    private static string[] BodyLines(string srt) => srt.Split("\r\n").Skip(2).TakeWhile(l => l.Length > 0).ToArray();

    [Fact] // T-06
    public void BodyKeepsOnlyTheUsersLineBreaksAndNoStyle()
    {
        Assert.Equal(["一行目", "二行目"], BodyLines(SrtExport.Build([S(1, 2, "一行目\n\n二行目")]).Text));
        Assert.Equal(["前後に空白", "二行目"], BodyLines(SrtExport.Build([S(1, 2, "  前後に空白  \n 二行目 ")]).Text));
        Assert.Equal(["CR", "区切り"], BodyLines(SrtExport.Build([S(1, 2, "CR\r区切り")]).Text));
        Assert.Equal(["CRLF", "区切り"], BodyLines(SrtExport.Build([S(1, 2, "CRLF\r\n区切り")]).Text));
        var longLine = new string('あ', 70);
        Assert.Equal([longLine], BodyLines(SrtExport.Build([S(1, 2, longLine)]).Text));
        var styled = new SubtitleSegment { Start = 1, End = 2, Text = "一\n二\n三", Position = SubtitlePosition.Top, VerticalOffset = 54, FontSize = 96 };
        var text = SrtExport.Build([styled]).Text;
        Assert.Equal(["一", "二", "三"], BodyLines(text));
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("\\an", text);
        Assert.DoesNotContain("<font", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact] // T-07
    public void SubtitleWithEmptyBodyIsSkippedAndNotCounted()
    {
        var doc = SrtExport.Build([S(1, 2, "A"), S(3, 4, "\n  \n"), S(5, 6, "C")]);
        Assert.Equal(2, doc.Count);
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:02,000\r\nA\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,000\r\nC\r\n\r\n", doc.Text);
    }

    [Fact] // T-09（既存の書き方は BOM なしのまま）
    public void AtomicFileWithoutEncodingStillWritesUtf8WithoutBom()
    {
        var dir = Directory.CreateTempSubdirectory("mvs-122-atomic-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.txt");
            AtomicFile.WriteAllText(path, "あ");
            Assert.Equal(new byte[] { 0xE3, 0x81, 0x82 }, File.ReadAllBytes(path));
            AtomicFile.WriteAllText(path, "あ", SrtExport.FileEncoding);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, 0xE3, 0x81, 0x82 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] // T-10 (a)(b)(c)
    public void RoundingDoesNotCreateEmptyOrOverlappingSubtitles()
    {
        Assert.Equal("00:00:01,000 --> 00:00:01,001", TimeLines(SrtExport.Build([S(1.0000, 1.0004, "a")]).Text)[0]);
        var touching = TimeLines(SrtExport.Build([S(1.0, 2.0005, "a"), S(2.0005, 3.0, "b")]).Text);
        Assert.Equal(touching[0][^12..], touching[1][..12]);
        var close = TimeLines(SrtExport.Build([S(1.0, 2.0004, "a"), S(2.0006, 3.0, "b")]).Text);
        Assert.True(Ms(close[0][^12..]) <= Ms(close[1][..12]));
    }

    [Theory] // T-10 (d)
    [InlineData(0.0)]
    [InlineData(2.002)]
    [InlineData(3.003)]
    [InlineData(4.004)]
    public void RandomNonOverlappingSubtitlesStayNonOverlapping(double offset)
    {
        var rnd = new Random(122);
        var subs = new List<SubtitleSegment>();
        var t = 0.0;
        for (var i = 0; i < 50; i++)
        {
            var len = Math.Round(0.2 + rnd.NextDouble() * 2.8, 4);
            var gap = i % 5 == 0 ? 0 : Math.Round(rnd.NextDouble() * 0.5, 4);
            var start = Math.Round(t + gap, 4);
            subs.Add(S(start, Math.Round(start + len, 4), $"s{i}"));
            t = subs[^1].End;
        }
        var lines = TimeLines(SrtExport.Build(subs, offset).Text);
        Assert.Equal(50, lines.Length);
        long prevEnd = -1;
        foreach (var l in lines)
        {
            var (a, b) = (Ms(l[..l.IndexOf(' ')]), Ms(l[(l.LastIndexOf(' ') + 1)..]));
            Assert.True(a < b, l);
            Assert.True(prevEnd <= a, l);
            prevEnd = b;
        }
    }

    [Fact] // T-11
    public void ExportedSrtReadsBackToTheSameSubtitles()
    {
        List<SubtitleSegment> subs =
        [
            S(1.234, 3.456, "一行"), S(3.456, 5.0, "二行の\n字幕"), S(6.0, 8.0, "一\n二\n三"),
            S(10.001, 10.201, "最短"), S(3725.125, 3727.0, "1時間超"),
        ];
        var doc = SrtExport.Build(subs);
        AssertSame(subs, CaptionSrtParser.Parse(doc.Text, 3800));

        var dir = Directory.CreateTempSubdirectory("mvs-122-srt-").FullName;
        try
        {
            var path = Path.Combine(dir, "a.srt");
            SrtExport.Write(path, doc);
            AssertSame(subs, CaptionSrtParser.Parse(File.ReadAllText(path), 3800));
        }
        finally { Directory.Delete(dir, true); }

        static void AssertSame(List<SubtitleSegment> expected, SrtParseResult r)
        {
            Assert.True(r.IsValid, string.Join(" / ", r.Errors.Select(e => e.Display)));
            Assert.Equal(expected.Count, r.Entries.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start, r.Entries[i].Start, 0.0005);
                Assert.Equal(expected[i].End, r.Entries[i].End, 0.0005);
                Assert.Equal(expected[i].Text, r.Entries[i].Text);
            }
        }
    }

    /// <summary>AC-8 の比較規則（2026-11-16 利用者判断）: 各行の前後の空白と空行を除き、改行で行を分けた本文。</summary>
    private static string Comparable(string text)
        => string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0));

    [Fact] // T-11 (b) レビュー R2: 通常の字幕の追加・編集を通した本文（字下げ・空行・空白だけの行・改行の種類・英語）
    public void ExportedSrtReadsBackWithTheAgreedBodyComparison()
    {
        var e = new ProjectEditor(TestProjects.Create(20));
        Assert.True(e.ConfirmCuts().Success);
        string[] texts =
        [
            "一行目\n　 二行目",            // 2 行目の全角・半角の字下げ（R2 の再現入力）
            "一\n  二\n\t三",               // 2・3 行目の字下げ
            "前\n\n後",                      // 本文の中の空行
            "前\n 　 \n後",                  // 空白だけの行
            "CRLF\r\n LF\n終",               // 改行の種類の混在（CRLF・LF）
            "CR\r　区切り",                   // CR だけの改行
            "Hello,\n  world  ",             // 英語と行末の空白
            "通常の日本語の字幕",
        ];
        for (var i = 0; i < texts.Length; i++)
            Assert.True(e.AddSubtitle(1 + i * 2, 2 + i * 2, texts[i]).Result.Success, texts[i]);
        // 編集でも同じ（最後の字幕の本文を字下げつきに変える）
        var last = e.Project.SubtitleSegments[^1];
        Assert.True(e.UpdateSubtitle(last.Id, last.Start, last.End, "編集後\n　字下げ", last.Position, last.FontSize).Success);

        var subs = e.Project.SubtitleSegments.ToList();
        // 元の本文には字下げが残っている（文字どおりの一致ではないことを固定する）
        Assert.Contains(subs, s => s.Text.Contains("\n　 二行目", StringComparison.Ordinal));
        var r = CaptionSrtParser.Parse(SrtExport.Build(subs).Text, 20);
        Assert.True(r.IsValid, string.Join(" / ", r.Errors.Select(x => x.Display)));
        Assert.Equal(subs.Count, r.Entries.Count);
        for (var i = 0; i < subs.Count; i++)
        {
            Assert.Equal(SrtExport.ToMilliseconds(subs[i].Start), SrtExport.ToMilliseconds(r.Entries[i].Start));
            Assert.Equal(SrtExport.ToMilliseconds(subs[i].End), SrtExport.ToMilliseconds(r.Entries[i].End));
            Assert.Equal(Comparable(subs[i].Text), r.Entries[i].Text);
        }
        Assert.Equal("一行目\n二行目", r.Entries[0].Text);
        Assert.Equal("前\n後", r.Entries[3].Text);
        Assert.Equal("編集後\n字下げ", r.Entries[^1].Text);
    }

    [Fact] // T-12
    public void DefaultPathIsTheMp4DefaultWithSrtExtension()
    {
        var video = @"F:\v\rec.mp4";
        var project = @"F:\v\rec.mvsproj.json";
        var cases = new[]
        {
            (new ExportSettings(), project),
            (new ExportSettings { VideoOutputDirectory = @"D:\out", VideoOutputFileName = "x.mp4" }, project),
            (new ExportSettings { VideoOutputFileName = "a.b.mp4" }, project),
            (new ExportSettings { VideoOutputFileName = "x.mkv" }, project),
            (new ExportSettings(), @"F:\v\edits\20261116_1200_別\20261116_1200_別.mvsproj.json"),
        };
        foreach (var (es, proj) in cases)
        {
            var before = (es.VideoOutputDirectory, es.VideoOutputFileName, es.ImageOutputDirectory, es.TejunshoOutputDirectory);
            Assert.Equal(Path.ChangeExtension(VideoExportService.DefaultOutputPath(video, es, proj), ".srt"), SrtExport.DefaultOutputPath(video, es, proj));
            Assert.Equal(before, (es.VideoOutputDirectory, es.VideoOutputFileName, es.ImageOutputDirectory, es.TejunshoOutputDirectory));
        }
        Assert.EndsWith("_edited.srt", SrtExport.DefaultOutputPath(video, new ExportSettings(), project));
        Assert.Equal(Path.GetDirectoryName(VideoExportService.DefaultOutputPath(video, new ExportSettings(), project)),
            Path.GetDirectoryName(SrtExport.DefaultOutputPath(video, new ExportSettings(), project)));
        Assert.Equal(@"D:\out\x.srt", SrtExport.DefaultOutputPath(video, cases[1].Item1, project));
        Assert.Equal("a.b.srt", Path.GetFileName(SrtExport.DefaultOutputPath(video, cases[2].Item1, project)));
        Assert.Equal("x.srt", Path.GetFileName(SrtExport.DefaultOutputPath(video, cases[3].Item1, project)));
    }
}

/// <summary>v1.22 字幕ファイルの書き出しの画面（MainViewModel.ExportSrtCommand。試験表 T-07・T-08・T-13〜T-16）。</summary>
public sealed class SrtExportCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mvs-122-cmd-").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    private async Task<(MainViewModel Vm, MainViewModelPhaseTests.FakeDialogs Dialogs, ListLogger<MainViewModel> Log)> OpenAsync(
        params SubtitleSegment[] subtitles)
    {
        var project = TestProjects.Create(60);
        project.SourceVideo.Path = Path.Combine(_dir, "rec.mp4");
        if (subtitles.Length > 0)
        {
            project.EditPhase = EditPhase.Subtitle;
            project.SubtitleSegments.AddRange(subtitles);
        }
        var dialogs = new MainViewModelPhaseTests.FakeDialogs();
        var settings = new MainViewModelPhaseTests.FakeSettings();
        var unused = new MainViewModelPhaseTests.Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var log = new ListLogger<MainViewModel>();
        var vm = new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(project), unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, new MainViewModelPhaseTests.FakeWindows(), unused, ffmpeg, log);
        await vm.OpenProjectFileAsync(Path.Combine(_dir, "rec.mvsproj.json"));
        Assert.True(vm.HasProject);
        return (vm, dialogs, log);
    }

    private static SubtitleSegment S(double start, double end, string text) => new() { Start = start, End = end, Text = text };

    [Theory] // T-13
    [InlineData(UiModes.Full)]
    [InlineData(UiModes.Simple)]
    public async Task EnabledOnlyWhileThereAreSubtitles(string mode)
    {
        var (none, _, _) = await MainViewModelPhaseTests.OpenAsync(mode);
        Assert.False(none.ExportSrtCommand.CanExecute(null));
        var changed = 0;
        none.ExportSrtCommand.CanExecuteChanged += (_, _) => changed++;
        none.ConfirmCutsCommand.Execute(null);
        Assert.False(none.ExportSrtCommand.CanExecute(null));

        none.SeekTo(1);
        none.SetMarkInCommand.Execute(null);
        none.SeekTo(3);
        none.SetMarkOutCommand.Execute(null);
        changed = 0;
        none.AddSubtitleCommand.Execute(null);
        Assert.True(none.ExportSrtCommand.CanExecute(null));
        Assert.True(changed > 0);

        changed = 0;
        none.Editor!.RemoveSubtitle(none.Project!.SubtitleSegments[0].Id);
        Assert.False(none.ExportSrtCommand.CanExecute(null));
        Assert.True(changed > 0);

        changed = 0;
        none.UndoCommand.Execute(null);
        Assert.True(none.ExportSrtCommand.CanExecute(null));
        Assert.True(changed > 0);
    }

    [Fact] // T-13 (a)
    public void DisabledWithoutProject()
    {
        var dialogs = new MainViewModelPhaseTests.FakeDialogs();
        var settings = new MainViewModelPhaseTests.FakeSettings();
        var unused = new MainViewModelPhaseTests.Unused();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), unused, settings, NullLogger<FFmpegStatus>.Instance);
        var vm = new MainViewModel(settings, new MainViewModelPhaseTests.FakeProjects(TestProjects.Create()), unused, unused, unused, unused,
            new ImageExportService(unused), unused, dialogs, new MainViewModelPhaseTests.FakeWindows(), unused, ffmpeg, NullLogger<MainViewModel>.Instance);
        Assert.False(vm.ExportSrtCommand.CanExecute(null));
        vm.ExportSrtCommand.Execute(null);
        Assert.Equal(0, dialogs.SaveFileCalls);
    }

    [Fact] // T-14・T-07（件数）・T-35（ffmpeg を使わない）
    public async Task WritesTheFileAndReportsPathAndCountWithoutTouchingTheProject()
    {
        var (vm, dialogs, _) = await OpenAsync(S(1, 2.5, "設定を開きます"), S(3, 5, "保存を押します"), S(6, 7, " \n "));
        Assert.False(vm.IsDirty);
        var es = vm.Project!.ExportSettings;
        var output = Path.Combine(_dir, "out.srt");
        dialogs.SaveFileResult = output;
        Assert.True(vm.ExportSrtCommand.CanExecute(null));
        vm.ExportSrtCommand.Execute(null);

        Assert.Equal(1, dialogs.SaveFileCalls);
        var (filter, name, folder, title) = dialogs.LastSaveFile!.Value;
        Assert.Equal("SRT 字幕 (*.srt)|*.srt", filter);
        Assert.Equal("rec_edited.srt", name);
        Assert.Equal(Path.GetDirectoryName(VideoExportService.DefaultOutputPath(vm.VideoPath, es, vm.ProjectPath)), folder);
        Assert.Equal("字幕ファイルの書き出し", title);
        Assert.True(File.Exists(output));
        Assert.Equal($"字幕ファイルを書き出しました: {output}（字幕 2 件）", vm.StatusMessage);
        Assert.Empty(dialogs.Infos);
        Assert.Empty(dialogs.Errors);
        Assert.False(vm.IsDirty);
        Assert.Equal(("", ""), (es.VideoOutputDirectory, es.VideoOutputFileName));
        Assert.Equal([(1.0, 2.5, "設定を開きます"), (3.0, 5.0, "保存を押します"), (6.0, 7.0, " \n ")],
            vm.Project.SubtitleSegments.Select(s => (s.Start, s.End, s.Text)));
    }

    [Fact] // T-08
    public async Task FileIsUtf8WithBomAndCrlf()
    {
        var (vm, dialogs, _) = await OpenAsync(S(1, 2, "日本語の字幕"), S(3, 4, "絵文字😀"));
        var output = Path.Combine(_dir, "bom.srt");
        dialogs.SaveFileResult = output;
        vm.ExportSrtCommand.Execute(null);
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0A) Assert.Equal(0x0D, bytes[i - 1]);
            if (bytes[i] == 0x0D) Assert.Equal(0x0A, bytes[i + 1]);
        }
        Assert.Equal(new byte[] { 0x0D, 0x0A, 0x0D, 0x0A }, bytes[^4..]);
        var text = new UTF8Encoding(false).GetString(bytes[3..]);
        Assert.Equal(SrtExport.Build(vm.Project!.SubtitleSegments).Text, text);
        Assert.Contains("\U0001F600", text);
    }

    [Fact] // T-15 (a) 作れないフォルダー（既存の AtomicFile は無いフォルダーを作るため、ファイルの下のフォルダーで失敗させる）
    public async Task FolderThatCannotBeCreatedShowsErrorWithFullPath() // T-15 (a2)
    {
        var blocker = Path.Combine(_dir, "file-not-folder");
        File.WriteAllText(blocker, "x");
        await AssertFailsAsync(Path.Combine(blocker, "out.srt"), null);
    }

    [Fact] // T-15 (a1) レビュー R3: 未作成のフォルダーは作って保存に成功する（AtomicFile の既存の契約。AC-9 に適合）
    public async Task MissingFolderIsCreatedAndTheFileIsWritten()
    {
        var (vm, dialogs, _) = await OpenAsync(S(1, 2, "A"));
        var output = Path.Combine(_dir, "no-such", "out.srt");
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        dialogs.SaveFileResult = output;
        vm.ExportSrtCommand.Execute(null);
        Assert.Empty(dialogs.Errors);
        Assert.True(File.Exists(output));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(output)[..3]);
        Assert.Equal($"字幕ファイルを書き出しました: {output}（字幕 1 件）", vm.StatusMessage);
        Assert.Equal([Path.GetFileName(output)], Directory.GetFiles(Path.GetDirectoryName(output)!).Select(Path.GetFileName));
    }

    [Fact] // T-15 (b)
    public async Task LockedFileIsKept()
    {
        var path = Path.Combine(_dir, "locked.srt");
        File.WriteAllText(path, "OLD");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await AssertFailsAsync(path, null);
        Assert.Equal("OLD", File.ReadAllText(path));
    }

    [Fact] // T-15 (c)
    public async Task ReadOnlyFileIsKept()
    {
        var path = Path.Combine(_dir, "ro.srt");
        File.WriteAllText(path, "OLD");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await AssertFailsAsync(path, null);
        Assert.Equal("OLD", File.ReadAllText(path));
    }

    private async Task AssertFailsAsync(string path, string? _)
    {
        var (vm, dialogs, log) = await OpenAsync(S(1, 2, "A"), S(3, 4, "B"));
        var before = vm.StatusMessage;
        dialogs.SaveFileResult = path;
        vm.ExportSrtCommand.Execute(null);
        var error = Assert.Single(dialogs.Errors);
        Assert.StartsWith("字幕ファイルを書き出せませんでした。", error);
        Assert.Contains(Path.GetFullPath(path), error);
        var entry = Assert.Single(log.Entries, e => e.Message.StartsWith("SRT export failed:", StringComparison.Ordinal));
        Assert.Equal($"SRT export failed: {Path.GetFullPath(path)}", entry.Message);
        Assert.NotNull(entry.Exception);
        Assert.Equal(before, vm.StatusMessage);
        Assert.False(vm.IsDirty);
        Assert.Equal([(1.0, 2.0, "A"), (3.0, 4.0, "B")], vm.Project!.SubtitleSegments.Select(s => (s.Start, s.End, s.Text)));
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Directory.Exists(folder)) Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact] // T-16
    public async Task CancelDoesNothing()
    {
        var (vm, dialogs, _) = await OpenAsync(S(1, 2, "A"));
        var before = vm.StatusMessage;
        dialogs.SaveFileResult = null;
        vm.ExportSrtCommand.Execute(null);
        Assert.Equal(1, dialogs.SaveFileCalls);
        Assert.Empty(Directory.GetFiles(_dir, "*.srt"));
        Assert.Empty(dialogs.Errors);
        Assert.Empty(dialogs.Infos);
        Assert.False(vm.IsDirty);
        Assert.Equal(before, vm.StatusMessage);
        Assert.Equal(("", ""), (vm.Project!.ExportSettings.VideoOutputDirectory, vm.Project.ExportSettings.VideoOutputFileName));
    }

    [Fact] // 詳細設計: 冒頭タイトルの秒数をプロジェクトの fps で足す
    public async Task TitleSecondsAreAdded()
    {
        var (vm, dialogs, _) = await OpenAsync(S(1, 2.5, "A"));
        vm.Project!.Title = new VideoTitle { Text = "操作マニュアル", Seconds = 3, FadeIn = true };
        var output = Path.Combine(_dir, "title.srt");
        dialogs.SaveFileResult = output;
        vm.ExportSrtCommand.Execute(null);
        Assert.Contains("00:00:04,000 --> 00:00:05,500", File.ReadAllText(output));
        Assert.Equal((1.0, 2.5), (vm.Project.SubtitleSegments[0].Start, vm.Project.SubtitleSegments[0].End));
    }
}

/// <summary>v1.22 AI 履歴の上限（試験表 T-17〜T-20）。</summary>
public class AiHistoryLimitTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(9));

    private static List<AiHistoryEntry> Make(int count)
        => Enumerable.Range(1, count).Select(i => new AiHistoryEntry { Id = $"h{i}", Prompt = "p", PromptCreatedAt = Base.AddDays(i - 1) }).ToList();

    private static ProjectEditor Editor(List<AiHistoryEntry> history, params string[] referenced)
    {
        var p = TestProjects.Create();
        p.AiHistory = history;
        p.CutCandidates.AddRange(referenced.Select(id => new CutCandidate { Start = 1, End = 2, Source = SegmentSource.Ai, AiHistoryId = id }));
        p.CutCandidates.Add(new CutCandidate { Start = 3, End = 4, AiHistoryId = null });
        return new ProjectEditor(p);
    }

    private static AiHistoryEntry New(DateTimeOffset? at = null) => new() { Id = "new", Prompt = "p", PromptCreatedAt = at ?? Base.AddYears(1) };

    [Fact] // T-17 (a)(b)(c)
    public void AddingOverTheLimitRemovesTheOldest()
    {
        var a = Editor(Make(29));
        a.UpsertAiHistory(New());
        Assert.Equal(30, a.Project.AiHistory.Count);

        var b = Editor(Make(30));
        b.UpsertAiHistory(New());
        Assert.Equal(30, b.Project.AiHistory.Count);
        Assert.DoesNotContain(b.Project.AiHistory, h => h.Id == "h1");
        Assert.Equal("new", b.Project.AiHistory[^1].Id);
        Assert.Equal(Enumerable.Range(2, 29).Select(i => $"h{i}"), b.Project.AiHistory.Take(29).Select(h => h.Id));

        var c = Editor(Make(31));
        c.UpsertAiHistory(New());
        Assert.Equal(30, c.Project.AiHistory.Count);
        Assert.DoesNotContain(c.Project.AiHistory, h => h.Id is "h1" or "h2");

        Assert.Equal(0, AiHistoryLimit.Prune(Make(30), [], null));
        Assert.Equal(1, AiHistoryLimit.Prune(Make(31), [], null));
        Assert.Equal(2, AiHistoryLimit.Prune(Make(32), [], null));
    }

    [Fact] // T-17 (d)(e)(f)
    public void OrderUsesPromptCreatedAtThenParsedAtThenListOrder()
    {
        var d = Make(30);
        d[0].PromptCreatedAt = null; d[0].ParsedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(9));
        d[1].PromptCreatedAt = null; d[1].ParsedAt = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(1, AiHistoryLimit.Prune(Append(d), [], "new"));
        Assert.DoesNotContain(d, h => h.Id == "h2");
        Assert.Contains(d, h => h.Id == "h1");

        var e = Make(30);
        e[9].PromptCreatedAt = null;
        Assert.Equal(1, AiHistoryLimit.Prune(Append(e), [], "new"));
        Assert.DoesNotContain(e, h => h.Id == "h10");

        var f = Make(30);
        f[2].PromptCreatedAt = Base.AddYears(-1);
        f[6].PromptCreatedAt = Base.AddYears(-1);
        var before = f.Select(h => h.Id).Where(id => id != "h3").Append("new").ToList();
        Assert.Equal(1, AiHistoryLimit.Prune(Append(f), [], "new"));
        Assert.Equal(before, f.Select(h => h.Id));

        static List<AiHistoryEntry> Append(List<AiHistoryEntry> list) { list.Add(New()); return list; }
    }

    [Fact] // T-18
    public void ReferencedAndAddedEntriesAreKept()
    {
        var a = Editor(Make(30), "h1", "h2");
        a.UpsertAiHistory(New());
        Assert.Equal(30, a.Project.AiHistory.Count);
        Assert.Contains(a.Project.AiHistory, h => h.Id == "h1");
        Assert.Contains(a.Project.AiHistory, h => h.Id == "h2");
        Assert.DoesNotContain(a.Project.AiHistory, h => h.Id == "h3");

        var b = Editor(Make(30));
        b.UpsertAiHistory(new AiHistoryEntry { Id = "new", PromptCreatedAt = null });
        Assert.Contains(b.Project.AiHistory, h => h.Id == "new");
        Assert.DoesNotContain(b.Project.AiHistory, h => h.Id == "h1");

        var all = Enumerable.Range(1, 30).Select(i => $"h{i}").ToArray();
        var c = Editor(Make(30), all);
        c.UpsertAiHistory(New());
        Assert.Equal(31, c.Project.AiHistory.Count);
        Assert.Equal(0, AiHistoryLimit.Prune(c.Project.AiHistory, c.Project.CutCandidates, "new"));

        var d = Editor(Make(30), "zzz");
        d.UpsertAiHistory(New());
        Assert.DoesNotContain(d.Project.AiHistory, h => h.Id == "h1");
        Assert.Equal(30, d.Project.AiHistory.Count);
    }

    [Fact] // T-19
    public void LoadingAndUpdatingDoNotPrune()
    {
        var dir = Directory.CreateTempSubdirectory("mvs-122-ai-").FullName;
        try
        {
            var path = Path.Combine(dir, "p" + ProjectService.Extension);
            var p = TestProjects.Create();
            p.AiHistory = Make(35);
            var svc = new ProjectService();
            svc.Save(p, path);
            var loaded = svc.Load(path).Project;
            Assert.Equal(Make(35).Select(h => h.Id), loaded.AiHistory.Select(h => h.Id));
            svc.Save(loaded, path);
            Assert.Equal(35, svc.Load(path).Project.AiHistory.Count);

            var ed = new ProjectEditor(loaded);
            ed.UpsertAiHistory(new AiHistoryEntry { Id = "h35", Response = "変更", PromptCreatedAt = Base.AddDays(34) });
            Assert.Equal(35, ed.Project.AiHistory.Count);
            Assert.Equal("変更", ed.Project.AiHistory[34].Response);

            var thirty = Editor(Make(30));
            thirty.UpsertAiHistory(new AiHistoryEntry { Id = "h1", Response = "変更" });
            Assert.Equal(30, thirty.Project.AiHistory.Count);
            Assert.Equal("変更", thirty.Project.AiHistory[0].Response);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] // T-20（レビュー R4: Undo で実際に戻る別の編集がある構成）
    public void HistoryIsNotUndoableButMarksDirty()
    {
        var ed = Editor(Make(30));
        Assert.False(ed.IsDirty);
        // 先に Undo できる別の編集（候補の追加）をしておく
        ed.AddCandidates([new CutCandidate { Id = "c-edit", Start = 5, End = 6, Source = SegmentSource.Ai }], "候補の追加");
        Assert.Equal(1, ed.History.UndoCount);
        var (canUndo, label, count) = (ed.History.CanUndo, ed.History.UndoLabel, ed.History.UndoCount);
        ed.UpsertAiHistory(New());                                       // 追加（h1 を削る）
        ed.UpsertAiHistory(new AiHistoryEntry { Id = "h5", Response = "更新" }); // 既存 id の更新
        Assert.True(ed.IsDirty);
        Assert.Equal((canUndo, label, count), (ed.History.CanUndo, ed.History.UndoLabel, ed.History.UndoCount));
        var historyAfter = ed.Project.AiHistory.Select(h => (h.Id, h.Response)).ToList();
        Assert.Equal(30, historyAfter.Count);
        Assert.DoesNotContain(historyAfter, h => h.Id == "h1");

        ed.Undo(); // 戻るのは直前の別の操作（候補の追加）だけ
        Assert.DoesNotContain(ed.Project.CutCandidates, c => c.Id == "c-edit");
        Assert.Equal(historyAfter, ed.Project.AiHistory.Select(h => (h.Id, h.Response)));
        ed.Redo();
        Assert.Contains(ed.Project.CutCandidates, c => c.Id == "c-edit");
        Assert.Equal(historyAfter, ed.Project.AiHistory.Select(h => (h.Id, h.Response)));

        var upd = Editor(Make(30));
        upd.UpsertAiHistory(new AiHistoryEntry { Id = "h5" });
        Assert.True(upd.IsDirty);
        Assert.Equal(0, upd.History.UndoCount);
    }

    [Fact] // T-20 (b) レビュー R4: 候補を戻す Undo で、削った履歴を指す AiHistoryId が残る境界（§44.4 の制約。保存・再読込後も同じ）
    public void UndoCanRestoreCandidatesThatReferToAPrunedHistory()
    {
        var ed = Editor(Make(30), "h1");
        ed.ClearCandidates(SegmentSource.Ai);                  // h1 を指す候補を消す（Undo 対象）
        ed.UpsertAiHistory(New());                             // 守る参照が無くなった h1 を削る
        Assert.DoesNotContain(ed.Project.AiHistory, h => h.Id == "h1");
        ed.Undo();                                             // 候補だけが戻り、履歴は戻らない
        Assert.Contains(ed.Project.CutCandidates, c => c.AiHistoryId == "h1");
        Assert.DoesNotContain(ed.Project.AiHistory, h => h.Id == "h1");
        Assert.Equal(30, ed.Project.AiHistory.Count);

        var dir = Directory.CreateTempSubdirectory("mvs-122-ai-undo-").FullName;
        try
        {
            var path = Path.Combine(dir, "p" + ProjectService.Extension);
            var svc = new ProjectService();
            svc.Save(ed.Project, path);
            var loaded = svc.Load(path).Project;
            Assert.Contains(loaded.CutCandidates, c => c.AiHistoryId == "h1");
            Assert.DoesNotContain(loaded.AiHistory, h => h.Id == "h1");
            Assert.Equal(30, loaded.AiHistory.Count);
            // 欠けた参照は次の上限処理で守る対象にならず、エラーにもならない
            var again = new ProjectEditor(loaded);
            again.UpsertAiHistory(new AiHistoryEntry { Id = "new2", Prompt = "p", PromptCreatedAt = Base.AddYears(2) });
            Assert.Equal(30, again.Project.AiHistory.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>v1.22 録画画面を閉じるときの中止（試験表 T-21）。</summary>
public class RecorderAbortForCloseTests
{
    private sealed class Targets : ICaptureTargetService
    {
        public IReadOnlyList<CaptureWindow> ListWindows() => [];
        public IReadOnlyList<CaptureMonitor> ListMonitors() => [];
        public CaptureRegion VirtualScreen() => new(0, 0, 1920, 1080);
        public CaptureRegion? GetWindowBounds(IntPtr handle) => null;
        public bool BringToFront(IntPtr handle) => false;
        public CaptureMonitor MonitorOf(CaptureRegion region) => throw new NotSupportedException();
    }

    private sealed class Audio : IAudioInputDeviceService
    {
        public Task<IReadOnlyList<AudioInputDevice>> ListAsync(string ffmpegPath, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AudioInputDevice>>([]);
        public string? GetDefaultDeviceName() => null;
        public AudioInputDevice? Resolve(IReadOnlyList<AudioInputDevice> devices, string selectedName, string? defaultName) => null;
    }

    private sealed class Recorder(Func<Task> abort) : IRecordingService
    {
        public int AbortCalls;
        public RecordingState State => RecordingState.Recording;
        public TimeSpan Elapsed => TimeSpan.FromSeconds(3);
        public int SegmentCount => 1;
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler<string>? Interrupted { add { } remove { } }
        public Task StartAsync(RecordingStartRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RecordingResult> StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AbortAsync() { AbortCalls++; return abort(); }
    }

    private static (RecorderViewModel Vm, Recorder R, MainViewModelPhaseTests.FakeDialogs D, ListLogger<RecorderViewModel> L) Create(Func<Task> abort)
    {
        var r = new Recorder(abort);
        var (vm, d, l) = CreateWith(r);
        return (vm, r, d, l);
    }

    private static (RecorderViewModel Vm, MainViewModelPhaseTests.FakeDialogs D, ListLogger<RecorderViewModel> L) CreateWith(IRecordingService recorder)
    {
        var s = new MainViewModelPhaseTests.FakeSettings();
        var ffmpeg = new FFmpegStatus(new FFmpegLocator(), new MainViewModelPhaseTests.Unused(), s, NullLogger<FFmpegStatus>.Instance);
        var d = new MainViewModelPhaseTests.FakeDialogs();
        var l = new ListLogger<RecorderViewModel>();
        return (new RecorderViewModel(s, recorder, new Targets(), new Audio(), ffmpeg, d, l), d, l);
    }

    public static TheoryData<string> Failures => ["sync", "async"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailureIsLoggedAndShownWithoutThrowing(string kind)
    {
        Exception thrown = kind == "sync" ? new InvalidOperationException("同期の失敗") : new IOException("非同期の失敗");
        Func<Task> abort = kind == "sync"
            ? () => throw thrown
            : async () => { await Task.Yield(); throw thrown; };
        var (vm, r, d, l) = Create(abort);
        await vm.AbortForCloseAsync();
        Assert.Equal(1, r.AbortCalls);
        Assert.StartsWith("録画の中止で問題が起きました。画面を閉じます。", Assert.Single(d.Errors));
        var entry = Assert.Single(l.Entries, e => e.Message == "Recording abort failed while closing");
        Assert.Same(thrown, entry.Exception);
    }

    [Fact]
    public async Task SuccessShowsNothing()
    {
        var (vm, r, d, l) = Create(() => Task.CompletedTask);
        await vm.AbortForCloseAsync();
        Assert.Equal(1, r.AbortCalls);
        Assert.Empty(d.Errors);
        Assert.DoesNotContain(l.Entries, e => e.Message == "Recording abort failed while closing");
    }

    // ---- レビュー R1: 停止・破棄に失敗しても、録画プロセスの後始末を試みる（実 RecordingService に故障を注入） ----

    /// <summary>停止・破棄の失敗を注入できる録画プロセス。Exited の購読数も数える。</summary>
    private sealed class FaultyProcess(string file, string workDir, string stopFault, bool disposeFault) : IRecordingProcess
    {
        public int StopCalls, DisposeCalls, Subscribers;
        public bool Killed;
        public bool HasExited => Killed;
        public string ErrorTail => "";
        public event EventHandler? Exited { add => Subscribers++; remove => Subscribers--; }
        public Exception? StopError { get; } = stopFault == "none" ? null : new System.ComponentModel.Win32Exception(5, "停止の失敗");
        public Exception? DisposeError { get; } = disposeFault ? new InvalidOperationException("破棄の失敗") : null;

        public Task StartAsync(CancellationToken ct)
        {
            File.WriteAllBytes(Path.Combine(workDir, file), new byte[4096]);
            return Task.CompletedTask;
        }

        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            StopCalls++;
            if (stopFault == "sync") throw StopError!;
            if (stopFault == "async") { await Task.Yield(); throw StopError!; }
            Killed = true;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (DisposeError is not null) throw DisposeError;
            Killed = true; // 実装の DisposeAsync は Kill する
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultyFactory(string stopFault, bool disposeFault) : IRecordingProcessFactory
    {
        public List<FaultyProcess> Processes { get; } = [];
        public string? WorkDir;
        public IRecordingProcess Create(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory)
        {
            WorkDir = workingDirectory;
            var p = new FaultyProcess(arguments[^1], workingDirectory, stopFault, disposeFault);
            Processes.Add(p);
            return p;
        }
    }

    private sealed class NoRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoMetadata : IMediaMetadataService
    {
        public Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static async Task<(RecordingService Svc, FaultyFactory F, string Dir, ListLogger<RecordingService> L)> StartFaulty(
        string stopFault, bool disposeFault, bool pauseFirst = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mvs-r1-" + Guid.NewGuid().ToString("N"));
        var f = new FaultyFactory(stopFault, disposeFault);
        var l = new ListLogger<RecordingService>();
        var svc = new RecordingService(f, new NoRunner(), new NoMetadata(), l);
        await svc.StartAsync(new RecordingStartRequest("ffmpeg.exe", "ffprobe.exe",
            new ScreenRecordingOptions(new CaptureRegion(0, 0, 640, 360), 30, true, null), Path.Combine(dir, "rec.mp4"), new RecordingInfo()));
        return (svc, f, dir, l);
    }

    public static TheoryData<string, bool> CleanupFaults => new()
    {
        { "sync", false }, { "async", false }, { "none", true }, { "sync", true }, { "async", true },
    };

    [Theory]
    [MemberData(nameof(CleanupFaults))]
    public async Task AbortFailureStillDisposesProcessAndReleasesIt(string stopFault, bool disposeFault)
    {
        var (svc, f, dir, l) = await StartFaulty(stopFault, disposeFault);
        try
        {
            var p = Assert.Single(f.Processes);
            Assert.Equal(1, p.Subscribers);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.AbortAsync());

            // 最初の例外（停止の失敗、無ければ破棄の失敗）が外へ出る。
            Assert.Same(p.StopError ?? p.DisposeError, ex);
            Assert.Equal(1, p.StopCalls);
            Assert.Equal(1, p.DisposeCalls);       // 停止に失敗しても破棄（強制終了）を試みる
            Assert.Equal(0, p.Subscribers);        // Exited の購読を解除
            Assert.Equal(RecordingState.Failed, svc.State);
            Assert.False(Directory.Exists(f.WorkDir)); // 途中のファイルの削除も試みる
            if (p.StopError is not null) Assert.Contains(l.Entries, e => e.Exception == p.StopError && e.Level == LogLevel.Error);
            if (p.DisposeError is not null) Assert.Contains(l.Entries, e => e.Exception == p.DisposeError && e.Level == LogLevel.Error);

            // プロセスの保持を解いたため、もう一度中止しても同じプロセスを止め直さない。
            await svc.AbortAsync();
            Assert.Equal(1, p.StopCalls);
            Assert.Equal(1, p.DisposeCalls);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StopFailureWhenClosingDisposesTheProcessAndShowsOneError()
    {
        var (svc, f, dir, _) = await StartFaulty("async", disposeFault: false);
        try
        {
            var (vm, d, l) = CreateWith(svc);
            await vm.AbortForCloseAsync();
            var p = Assert.Single(f.Processes);
            Assert.True(p.Killed);
            Assert.Equal(1, p.DisposeCalls);
            var error = Assert.Single(d.Errors);
            Assert.Contains("ffmpeg", error, StringComparison.Ordinal); // プロセスが残る場合の案内
            Assert.Single(l.Entries, e => e.Message == "Recording abort failed while closing");
            Assert.Equal(RecordingState.Failed, svc.State);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAbortWhileRecordingOrPausedStillWorks(bool paused)
    {
        var (svc, f, dir, _) = await StartFaulty("none", disposeFault: false);
        try
        {
            if (paused)
            {
                await svc.PauseAsync();
                Assert.Equal(RecordingState.Paused, svc.State);
            }
            var (vm, d, _) = CreateWith(svc);
            await vm.AbortForCloseAsync();
            var p = Assert.Single(f.Processes);
            Assert.Equal(1, p.StopCalls);
            Assert.Equal(1, p.DisposeCalls);
            Assert.Equal(0, p.Subscribers);
            Assert.Empty(d.Errors);
            Assert.Equal(RecordingState.Failed, svc.State);
            Assert.False(Directory.Exists(f.WorkDir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
