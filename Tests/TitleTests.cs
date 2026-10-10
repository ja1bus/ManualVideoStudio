using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Tests;

/// <summary>動画冒頭の黒背景タイトル（v1.15）: モデルの正規化・既定値。</summary>
public class VideoTitleModelTests
{
    [Fact]
    public void DefaultsAreEmptyTwoSecondsFadeOnMeiryo72()
    {
        var t = new VideoTitle();
        Assert.Equal("", t.Text);
        Assert.False(t.HasTitle);
        Assert.Equal(2, t.Seconds);
        Assert.True(t.FadeIn);
        Assert.Equal("Meiryo", t.FontFamily);
        Assert.Equal(72, t.FontSize);
        Assert.Equal(0.3, VideoTitle.FadeSeconds);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\n \r\n\t", false)]
    [InlineData("タイトル", true)]
    [InlineData("  a  ", true)]
    public void BlankTextMeansNoTitle(string text, bool expected) => Assert.Equal(expected, new VideoTitle { Text = text }.HasTitle);

    [Fact]
    public void NormalizeTrimsLinesAndDropsBlankLines()
        => Assert.Equal("1 行目\n2 行目", VideoTitle.Normalize("  1 行目 \r\n\r\n  2 行目\n\n"));

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(600, 5)]
    public void SecondsAreOneToFive(int input, int expected) => Assert.Equal(expected, VideoTitle.NormalizeSeconds(input));

    [Fact]
    public void SecondsChoicesAreOneToFive() => Assert.Equal([1, 2, 3, 4, 5], VideoTitle.SecondsChoices);

    [Fact]
    public void FontSizesAreTheTitleList_NotTheSubtitleList()
    {
        Assert.Equal([48, 56, 64, 72, 88, 104, 120], VideoTitle.FontSizes);
        Assert.NotEqual(SubtitleStyle.FontSizes.Select(s => (int)s), VideoTitle.FontSizes);
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(-5, 72)]
    [InlineData(10, 48)]
    [InlineData(60, 56)]
    [InlineData(72, 72)]
    [InlineData(200, 120)]
    public void FontSizeSnapsToTheList(int input, int expected) => Assert.Equal(expected, VideoTitle.NormalizeFontSize(input));

    [Theory]
    [InlineData(null, "Meiryo")]
    [InlineData("", "Meiryo")]
    [InlineData("  ", "Meiryo")]
    [InlineData("Yu Gothic UI", "Yu Gothic UI")]
    public void FontFamilyFallsBackToMeiryo(string? input, string expected) => Assert.Equal(expected, VideoTitle.NormalizeFontFamily(input));

    [Fact]
    public void FadeIsFixedAndShrinksForShortMain()
    {
        var t = new VideoTitle { Text = "a", FadeIn = true };
        Assert.Equal(0.3, t.EffectiveFadeSeconds(10));
        Assert.Equal(0.3, t.EffectiveFadeSeconds(0.3));
        Assert.Equal(0.2, t.EffectiveFadeSeconds(0.2));
        Assert.Equal(0, new VideoTitle { Text = "a", FadeIn = false }.EffectiveFadeSeconds(10));
        Assert.Equal(0, new VideoTitle { Text = "", FadeIn = true }.EffectiveFadeSeconds(10));
    }

    [Fact]
    public void TotalDurationAddsTitleSecondsRegardlessOfFade()
    {
        Assert.Equal(12, new VideoTitle { Text = "a", Seconds = 2, FadeIn = true }.TotalDuration(10));
        Assert.Equal(12, new VideoTitle { Text = "a", Seconds = 2, FadeIn = false }.TotalDuration(10));
        Assert.Equal(10, new VideoTitle { Text = "", Seconds = 2 }.TotalDuration(10));
    }
}

/// <summary>タイトルの配置（上端 1/3・幅 80%・最大 2 行・高さに比例）。</summary>
public class TitleLayoutTests
{
    private static TitleLayoutResult L(string text, int w = 1920, int h = 1080, int size = 72)
        => TitleLayout.Compute(text, w, h, (s, px) => EstimatedTextMeasurer.Instance.Measure(s, "Meiryo", px), size);

    [Fact]
    public void FontSizeScalesWithHeight()
    {
        Assert.Equal(72, L("a").FontSizePx, 6);
        Assert.Equal(36, L("a", 960, 540).FontSizePx, 6);
        Assert.Equal(144, L("a", 3840, 2160).FontSizePx, 6);
        Assert.Equal(120, L("a", 1920, 1080, 120).FontSizePx, 6);
    }

    [Fact]
    public void TopIsOneThirdAndWidthIsEightyPercent()
    {
        var r = L("a", 1280, 720);
        Assert.Equal(240, r.TopPx, 6);
        Assert.Equal(1024, r.MaxLineWidthPx, 6);
    }

    [Fact]
    public void ShortTextIsOneLineAndFits()
    {
        var r = L("操作の流れ");
        Assert.Single(r.Lines);
        Assert.True(r.Fits);
        Assert.Null(TitleLayout.TooLongMessage(r));
    }

    [Fact]
    public void ExplicitBreakGivesTwoLines()
    {
        var r = L("1 行目\n2 行目");
        Assert.Equal(["1 行目", "2 行目"], r.Lines);
        Assert.True(r.Fits);
    }

    [Fact]
    public void LongTextWrapsToTwoLines()
    {
        // 72px の全角 = 72px。1920 の 80% = 1536px → 1 行 21 文字。30 文字は 2 行。
        var r = L(new string('あ', 30));
        Assert.Equal(2, r.Lines.Count);
        Assert.True(r.Fits);
    }

    [Fact]
    public void TextOverTwoLinesDoesNotFitAndIsReported()
    {
        var r = L(new string('あ', 50));
        Assert.True(r.Lines.Count > 2);
        Assert.False(r.Fits);
        Assert.Contains("2 行に収まりません", TitleLayout.TooLongMessage(r));
    }

    [Fact]
    public void BiggerFontShrinksCapacity()
    {
        var text = new string('あ', 30);
        Assert.True(L(text, size: 72).Fits);
        Assert.False(L(text, size: 120).Fits);   // 1 行 12 文字 → 3 行
    }

    [Fact]
    public void ThreeExplicitLinesDoNotFit() => Assert.False(L("a\nb\nc").Fits);
}

/// <summary>ProjectEditor.SetTitle: Undo/Redo・変更なしの扱い・字幕との独立。</summary>
public class TitleEditorTests
{
    [Fact]
    public void SetTitleIsOneUndoStep()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        Assert.True(ed.SetTitle("操作説明", 3, false, "Yu Gothic UI", 88).Success);
        Assert.True(ed.IsDirty);
        Assert.Equal(1, ed.History.UndoCount);
        Assert.Equal("タイトルの追加", ed.History.UndoLabel);
        var t = ed.Project.Title;
        Assert.Equal(("操作説明", 3, false, "Yu Gothic UI", 88), (t.Text, t.Seconds, t.FadeIn, t.FontFamily, t.FontSize));

        ed.Undo();
        Assert.False(ed.Project.Title.HasTitle);
        Assert.Equal((2, true, "Meiryo", 72), (ed.Project.Title.Seconds, ed.Project.Title.FadeIn, ed.Project.Title.FontFamily, ed.Project.Title.FontSize));
        ed.Redo();
        Assert.Equal(("操作説明", 3, false), (ed.Project.Title.Text, ed.Project.Title.Seconds, ed.Project.Title.FadeIn));
    }

    [Fact]
    public void NoChangeAddsNoHistoryAndNoDirty()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("A", 2, true);
        ed.MarkSaved();
        var count = ed.History.UndoCount;
        ed.SetTitle(" A \n", 2, true);   // 正規化すると同じ
        Assert.Equal(count, ed.History.UndoCount);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void BlankFromEmptyIsNoChange()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("   ", 2, true);
        Assert.Equal(0, ed.History.UndoCount);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void ClearingTextKeepsOtherSettingsAndIsOneUndo()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("A", 4, false, "Yu Gothic UI", 104);
        ed.SetTitle("", 4, false, "Yu Gothic UI", 104);
        Assert.Equal("タイトルの削除", ed.History.UndoLabel);
        var t = ed.Project.Title;
        Assert.False(t.HasTitle);
        Assert.Equal((4, false, "Yu Gothic UI", 104), (t.Seconds, t.FadeIn, t.FontFamily, t.FontSize));
        ed.Undo();
        Assert.Equal("A", ed.Project.Title.Text);
    }

    [Fact]
    public void ChangingOnlySecondsIsRecordedEvenWithEmptyText()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("", 4, true);
        Assert.Equal(1, ed.History.UndoCount);
        Assert.Equal(4, ed.Project.Title.Seconds);
    }

    [Fact]
    public void OutOfRangeValuesAreNormalized()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("A", 99, true, "", 13);
        Assert.Equal((5, "Meiryo", 48), (ed.Project.Title.Seconds, ed.Project.Title.FontFamily, ed.Project.Title.FontSize));
    }

    [Fact]
    public void TitleDoesNotChangeSubtitleOrShapeOrCutTimes()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        ed.AddCut(10, 20, SegmentAction.Remove, 0);
        ed.ConfirmCuts();
        ed.AddSubtitle(5, 8, "字幕");
        ed.SetTitle("タイトル", 5, true);
        var s = ed.Project.SubtitleSegments.Single();
        Assert.Equal((5d, 8d), (s.Start, s.End));
        var c = ed.Project.CutSegments.Single();
        Assert.Equal((10d, 20d), (c.Start, c.End));
        Assert.Equal(50, ed.OutputDuration);   // 本編の長さはタイトルで変わらない
        ed.SetTitle("タイトル", 1, false);
        Assert.Equal((5d, 8d), (s.Start, s.End));
    }

    [Fact]
    public void SubtitleUndoDoesNotRemoveTitleSetBeforeIt()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        ed.ConfirmCuts();
        ed.SetTitle("T", 2, true);
        ed.AddSubtitle(1, 2, "x");
        ed.Undo();
        Assert.Equal("T", ed.Project.Title.Text);
        Assert.Empty(ed.Project.SubtitleSegments);
    }

    [Fact]
    public void DiscardingSubtitlesKeepsTitle()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        ed.ConfirmCuts();
        ed.AddSubtitle(1, 2, "x");
        ed.SetTitle("T", 3, false);
        ed.DiscardSubtitlesAndReturnToCutEditing();
        Assert.Equal(("T", 3, false), (ed.Project.Title.Text, ed.Project.Title.Seconds, ed.Project.Title.FadeIn));
        ed.Undo();   // 字幕の全破棄を戻してもタイトルは残る
        Assert.Equal("T", ed.Project.Title.Text);
    }
}

/// <summary>保存・再読込・旧形式・編集の複製。</summary>
public sealed class TitlePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-title-" + Guid.NewGuid().ToString("N"));

    public TitlePersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private ProjectDocument Doc()
    {
        var video = Path.Combine(_dir, "v.mp4");
        File.WriteAllBytes(video, new byte[100]);
        var p = TestProjects.Create();
        p.SourceVideo.Path = video;
        p.SourceVideo.FileSize = 100;
        return p;
    }

    [Fact]
    public void SchemaVersionIsEight() => Assert.Equal(8, ProjectDocument.CurrentSchemaVersion);

    [Fact]
    public void RoundTripKeepsTitleAndWritesTitleObject()
    {
        var p = Doc();
        new ProjectEditor(p).SetTitle("日本語の\nタイトル", 4, false, "Yu Gothic UI", 104);
        var path = Path.Combine(_dir, "a.mvsproj.json");
        new ProjectService().Save(p, path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"title\"", text);
        Assert.Contains("\"fontFamily\": \"Yu Gothic UI\"", text);
        Assert.DoesNotContain("hasTitle", text, StringComparison.OrdinalIgnoreCase);   // 派生値は保存しない
        Assert.DoesNotContain("normalizedText", text, StringComparison.OrdinalIgnoreCase);

        var t = new ProjectService().Load(path).Project.Title;
        Assert.Equal(("日本語の\nタイトル", 4, false, "Yu Gothic UI", 104), (t.Text, t.Seconds, t.FadeIn, t.FontFamily, t.FontSize));
        Assert.Equal(8, new ProjectService().Load(path).Project.SchemaVersion);
    }

    [Fact]
    public void OldSchemaWithoutTitleLoadsAsNoTitle()
    {
        var p = Doc();
        var path = Path.Combine(_dir, "old.mvsproj.json");
        new ProjectService().Save(p, path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("title");
        node["schemaVersion"] = 7;
        File.WriteAllText(path, node.ToJsonString());

        var loaded = new ProjectService().Load(path).Project;
        Assert.False(loaded.Title.HasTitle);
        Assert.Equal((2, true, "Meiryo", 72), (loaded.Title.Seconds, loaded.Title.FadeIn, loaded.Title.FontFamily, loaded.Title.FontSize));
    }

    [Fact]
    public void BrokenTitleValuesAreNormalizedOnLoad()
    {
        var p = Doc();
        var path = Path.Combine(_dir, "broken.mvsproj.json");
        new ProjectService().Save(p, path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["title"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "text": null, "seconds": 99, "fadeIn": true, "fontFamily": "", "fontSize": 5 }""");
        File.WriteAllText(path, node.ToJsonString());
        var t = new ProjectService().Load(path).Project.Title;
        Assert.Equal(("", 5, "Meiryo", 48), (t.Text, t.Seconds, t.FontFamily, t.FontSize));

        node["title"] = null;   // null でも落ちない
        File.WriteAllText(path, node.ToJsonString());
        Assert.False(new ProjectService().Load(path).Project.Title.HasTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditCopyKeepsTitle_EvenWhenRestartingFromCuts(bool restartFromCuts)
    {
        var p = Doc();
        var ed = new ProjectEditor(p);
        ed.ConfirmCuts();
        ed.AddSubtitle(1, 2, "字幕");
        ed.SetTitle("コピー元", 3, false, "Yu Gothic UI", 56);
        var copy = ProjectCatalog.PrepareCopy(p, restartFromCuts, new ExportSettings());
        Assert.Equal(("コピー元", 3, false, "Yu Gothic UI", 56), (copy.Title.Text, copy.Title.Seconds, copy.Title.FadeIn, copy.Title.FontFamily, copy.Title.FontSize));
        Assert.NotSame(p.Title, copy.Title);
        Assert.Equal(restartFromCuts, copy.SubtitleSegments.Count == 0);
    }
}

/// <summary>書き出し引数: タイトルなしは従来と同一、ありは連結・フェード・無音。</summary>
public class TitleArgumentTests
{
    private const string Input = @"D:\in\操作 動画.mp4";
    private static readonly TitleFilterPlan Plan = new("title-image", 2, 0.3, 30);

    private static VideoMetadata Meta(bool audio = true, double dur = 60) => new()
    {
        Duration = dur, Width = 1920, Height = 1080, Fps = 30, HasAudio = audio, HasVideo = true,
    };

    private static string Filter(ExportCommand c)
    {
        var a = c.Arguments.ToList();
        var i = a.FindIndex(x => x is "-filter_complex" or "-/filter_complex" or "-filter_complex_script");
        return c.FilterScript ?? a[i + 1];
    }

    private static IReadOnlyList<TimeRange> Keep(double dur = 60, params CutSegment[] cuts) => EditTimelineCalculator.ComputeKeepRanges(dur, cuts);

    [Fact]
    public void WithoutTitleArgumentsAreUnchanged()
    {
        var keep = Keep(60, new CutSegment { Start = 10, End = 20 });
        var a = FFmpegArguments.Export(Input, keep, Meta(), "o.mp4", new VideoEncodeOptions(), 9).Arguments;
        var b = FFmpegArguments.Export(Input, keep, Meta(), "o.mp4", new VideoEncodeOptions(), 9, null, 0, null, null).Arguments;
        Assert.Equal(a, b);
        Assert.DoesNotContain("title-image", a);
        Assert.DoesNotContain("fade=", Filter(new ExportCommand(a, null, null)));
    }

    [Fact]
    public void Mp4WithTitleAddsInputFadeAndConcat()
    {
        var keep = Keep(60, new CutSegment { Start = 10, End = 20 });
        var c = FFmpegArguments.Export(Input, keep, Meta(), "o.mp4", new VideoEncodeOptions(), 9, title: Plan);
        var a = c.Arguments.ToList();
        var i = a.IndexOf("title-image");
        Assert.True(i > 0);
        Assert.Equal(["-f", "png_pipe", "-i"], a.GetRange(i - 3, 3));   // 拡張子なしの画像は形式を明示して読む
        var f = Filter(c);
        Assert.Contains("[outv]fade=t=in:st=0:d=0.3:color=black,fps=30,setsar=1,format=yuv420p[tm]", f);
        Assert.Contains("[1:v]loop=loop=59:size=1:start=0,setpts=N/(30*TB)", f);   // 2 秒 × 30fps = 60 コマ
        Assert.Contains("anullsrc=r=48000:cl=stereo:d=2[ta]", f);                    // タイトル区間は無音
        Assert.Contains("[tt][ta][tm][ma]concat=n=2:v=1:a=1[fv][fa]", f);
        Assert.DoesNotContain("afade", f);                                          // 音声フェードなし
        Assert.Contains("[outa]aresample=48000", f);
        var maps = a.Select((x, k) => (x, k)).Where(t => t.x == "-map").Select(t => a[t.k + 1]).ToList();
        Assert.Equal(["[fv]", "[fa]"], maps);
    }

    [Fact]
    public void FadeOffHasNoFadeFilter()
    {
        var f = Filter(FFmpegArguments.Export(Input, Keep(), Meta(), "o.mp4", new VideoEncodeOptions(), 9, title: Plan with { FadeSeconds = 0 }));
        Assert.DoesNotContain("fade=", f);
        Assert.Contains("[tt][ta][tm][ma]concat", f);
    }

    [Fact]
    public void ShortMainFadeIsClamped()
    {
        // タイトル側が渡すフェードは 0.3 だが、本編の長さ（0.2）を超えない。
        var keep = Keep(60, new CutSegment { Start = 0.2, End = 60 });
        var f = Filter(FFmpegArguments.Export(Input, keep, Meta(), "o.mp4", new VideoEncodeOptions(), 9, title: Plan));
        Assert.Contains("fade=t=in:st=0:d=0.2:color=black", f);
    }

    [Fact]
    public void NoAudioSourceUsesVideoOnlyConcat()
    {
        var c = FFmpegArguments.Export(Input, Keep(), Meta(audio: false), "o.mp4", new VideoEncodeOptions(), 9, title: Plan);
        var f = Filter(c);
        Assert.Contains("[tt][tm]concat=n=2:v=1:a=0[fv]", f);
        Assert.DoesNotContain("anullsrc", f);
        Assert.Contains("-an", c.Arguments);
        Assert.DoesNotContain("[fa]", c.Arguments);
    }

    [Fact]
    public void TitleInputComesAfterShapeImages()
    {
        var shapes = new ShapeFilterPlan([new ShapeOverlayInput("shape-0", 1, 2, 10, 10), new ShapeOverlayInput("shape-1", 1, 2, 10, 10)], []);
        var c = FFmpegArguments.Export(Input, Keep(), Meta(), "o.mp4", new VideoEncodeOptions(), 9, shapes: shapes, title: Plan);
        var a = c.Arguments.ToList();
        Assert.True(a.IndexOf("shape-1") < a.IndexOf("title-image"));
        Assert.Contains("[3:v]loop=", Filter(c));   // 入力 0=動画, 1・2=図形, 3=タイトル
    }

    [Fact]
    public void SubtitleStaysInMainChainBeforeTitle()
    {
        var f = Filter(FFmpegArguments.Export(Input, Keep(60, new CutSegment { Start = 10, End = 20 }), Meta(), "o.mp4", new VideoEncodeOptions(), 9,
            "subtitles.ass", 0, null, Plan));
        Assert.True(f.IndexOf("subtitles=filename=subtitles.ass", StringComparison.Ordinal) < f.IndexOf("fade=t=in", StringComparison.Ordinal));
    }

    [Fact]
    public void FpsTextUsesRationalForNtsc()
    {
        Assert.Equal("30000/1001", FFmpegArguments.FpsText(29.97));
        Assert.Equal("24000/1001", FFmpegArguments.FpsText(23.976));
        Assert.Equal("30", FFmpegArguments.FpsText(30));
        Assert.Equal("30", FFmpegArguments.FpsText(0));
        Assert.Equal("12.5", FFmpegArguments.FpsText(12.5));
    }

    [Fact]
    public void GifTitleScalesTitleLikeMainAndShiftsPaletteIndex()
    {
        var preset = GifPreset.Of(GifQuality.Standard);
        var plan = Plan with { Fps = preset.Fps };
        var keep = Keep(60, new CutSegment { Start = 10, End = 20 });
        var p1 = FFmpegArguments.GifPalette(Input, keep, Meta(), null, preset, "pal", 9, 0, null, plan);
        var f1 = Filter(p1);
        Assert.Contains("palettegen", f1);
        Assert.Contains("[tfull]palettegen", f1);
        Assert.Contains($"[1:v]loop=", f1);
        Assert.Equal(2, Regex(f1, "scale=w='min"));   // 本編とタイトルに同じ縮小
        Assert.Contains("title-image", p1.Arguments);

        var p2 = FFmpegArguments.GifEncode(Input, keep, Meta(), null, preset, "pal", "o.gif", 9, 0, null, plan);
        var f2 = Filter(p2);
        Assert.Contains("[gifsrc][2:v]paletteuse", f2);   // 入力 0=動画, 1=タイトル, 2=パレット
        Assert.Contains("[tfull]null[gifsrc]", f2.Replace("[outv]", "[gifsrc]"));
    }

    [Fact]
    public void GifWithoutTitleIsUnchanged()
    {
        var preset = GifPreset.Of(GifQuality.Standard);
        var keep = Keep(60, new CutSegment { Start = 10, End = 20 });
        var a = FFmpegArguments.GifEncode(Input, keep, Meta(), null, preset, "pal", "o.gif", 9).Arguments;
        var b = FFmpegArguments.GifEncode(Input, keep, Meta(), null, preset, "pal", "o.gif", 9, 0, null, null).Arguments;
        Assert.Equal(a, b);
        Assert.Contains("[gifsrc][1:v]paletteuse", Filter(new ExportCommand(a, null, null)));
    }

    private static int Regex(string s, string pattern) => System.Text.RegularExpressions.Regex.Matches(s, System.Text.RegularExpressions.Regex.Escape(pattern)).Count;
}

/// <summary>ExportWorkspace と進捗の長さ。</summary>
public class TitleWorkspaceTests
{
    [Fact]
    public async Task WorkspaceWritesTitleImageWithoutExtension()
    {
        var png = new byte[] { 1, 2, 3 };
        using var ws = await ExportWorkspace.CreateAsync(null, null, 640, 360, null, default, null, new VideoTitleImage(2, 0.3, png), 30);
        Assert.NotNull(ws.Directory);
        Assert.NotNull(ws.Title);
        var file = Path.Combine(ws.Directory!, ws.Title!.File);
        Assert.Equal("", Path.GetExtension(file));
        Assert.Equal(png, File.ReadAllBytes(file));
        Assert.Equal((2, 0.3, 30d), (ws.Title.Seconds, ws.Title.FadeSeconds, ws.Title.Fps));
        var dir = ws.Directory;
        ws.Dispose();
        Assert.False(Directory.Exists(dir));   // 後始末
    }

    [Fact]
    public async Task NoTitleNoSubtitleNoShapesCreatesNoFolder()
    {
        using var ws = await ExportWorkspace.CreateAsync(null, null, 640, 360, null, default);
        Assert.Null(ws.Directory);
        Assert.Null(ws.Title);
    }

    [Fact]
    public void CaptionRequestDoesNotCarryTitle()
    {
        // 字幕依頼用動画は VideoExportRequest を Title なしで作る（既定値 null）。
        var r = new VideoExportRequest("f", 9, "i", new VideoMetadata(), [], "o", new VideoEncodeOptions());
        Assert.Null(r.Title);
    }
}

/// <summary>タイトルの描画（プレビューと書き出しで共通）。</summary>
public class TitleRendererTests
{
    private static (byte[] Px, int W, int H) Pixels(BitmapSource src)
    {
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return (px, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static (int Count, int MinY, int MaxY, int MinX, int MaxX) Bright((byte[] Px, int W, int H) f)
    {
        int count = 0, minY = int.MaxValue, maxY = -1, minX = int.MaxValue, maxX = -1;
        for (int y = 0; y < f.H; y++)
            for (int x = 0; x < f.W; x++)
            {
                int i = (y * f.W + x) * 4;
                if (f.Px[i] > 128 && f.Px[i + 1] > 128 && f.Px[i + 2] > 128)
                {
                    count++; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                }
            }
        return (count, minY, maxY, minX, maxX);
    }

    private static byte[] RgbOnly(byte[] px) => px.Where((_, i) => i % 4 != 3).ToArray();

    [Fact]
    public Task EmptyTextIsPureBlack() => StaThread.RunAsync(() =>
    {
        var f = Pixels(TitleRenderer.Render("", 640, 360));
        Assert.Equal((640, 360), (f.W, f.H));
        Assert.Equal(0, Bright(f).Count);
        Assert.All(Enumerable.Range(0, f.W * f.H).Where(i => i % 997 == 0), i => Assert.Equal(0, f.Px[i * 4]));
    });

    [Fact]
    public Task TextIsWhiteCenteredAndStartsAroundOneThird() => StaThread.RunAsync(() =>
    {
        var f = Pixels(TitleRenderer.Render("操作説明", 1280, 720));
        var b = Bright(f);
        Assert.True(b.Count > 500);
        Assert.InRange(b.MinY, 240, 240 + 60);                    // 上端 = 高さの 1/3（240）から文字が始まる
        Assert.True(b.MinY >= 240, "文字領域の上端より上には描かない");
        var centerX = (b.MinX + b.MaxX) / 2.0;
        Assert.InRange(centerX, 640 - 20, 640 + 20);               // 横は中央揃え
        Assert.True(b.MaxX - b.MinX < 1280 * 0.8);                 // 幅 80% 以内
    });

    [Fact]
    public Task TwoLinesAreDrawnBelowEachOther() => StaThread.RunAsync(() =>
    {
        var one = Bright(Pixels(TitleRenderer.Render("上", 1280, 720)));
        var two = Bright(Pixels(TitleRenderer.Render("上\n下", 1280, 720)));
        Assert.True(two.MaxY - two.MinY > (one.MaxY - one.MinY) * 1.8);
    });

    [Fact]
    public Task SizeScalesWithVideoHeight() => StaThread.RunAsync(() =>
    {
        var small = Bright(Pixels(TitleRenderer.Render("あ", 640, 360)));
        var big = Bright(Pixels(TitleRenderer.Render("あ", 1280, 720)));
        var ratio = (double)(big.MaxY - big.MinY) / (small.MaxY - small.MinY);
        Assert.InRange(ratio, 1.8, 2.2);
    });

    [Fact]
    public Task FontSizeChoiceChangesTheDrawnSize() => StaThread.RunAsync(() =>
    {
        var s48 = Bright(Pixels(TitleRenderer.Render("あ", 1280, 720, "Meiryo", 48)));
        var s120 = Bright(Pixels(TitleRenderer.Render("あ", 1280, 720, "Meiryo", 120)));
        var ratio = (double)(s120.MaxY - s120.MinY) / (s48.MaxY - s48.MinY);
        Assert.InRange(ratio, 2.2, 2.8);   // 120 / 48 = 2.5
    });

    [Fact]
    public Task MissingFontFallsBackToMeiryoAndDrawsTheSame() => StaThread.RunAsync(() =>
    {
        var missing = Pixels(TitleRenderer.Render("操作", 640, 360, "No Such Font ZZZ", 72));
        var meiryo = Pixels(TitleRenderer.Render("操作", 640, 360, "Meiryo", 72));
        Assert.Equal(meiryo.Px, missing.Px);
    });

    [Fact]
    public Task PreviewAndExportUseTheSameRendering() => StaThread.RunAsync(() =>
    {
        var title = new VideoTitle { Text = "タイトル\n二行目", FontFamily = "Meiryo", FontSize = 88 };
        var (image, error) = TitleRenderer.BuildExportImage(title, 640, 360, 30);
        Assert.Null(error);
        Assert.NotNull(image);
        var decoded = Pixels(BitmapDecoder.Create(new MemoryStream(image!.Png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]);
        var direct = Pixels(TitleRenderer.Render("タイトル\n二行目", 640, 360, "Meiryo", 88));
        Assert.Equal(direct.W, decoded.W);
        Assert.Equal(direct.Px.Length, decoded.Px.Length);
        // PNG（可逆）を経由しても画素が同じ。
        Assert.Equal(RgbOnly(direct.Px), RgbOnly(decoded.Px));   // 書き出しの PNG は不透明（アルファなし）なので RGB だけ比べる
        Assert.Equal((2, 0.3), (image.Seconds, image.FadeSeconds));
    });

    [Fact]
    public Task ExportImageIsNullWithoutTitleAndErrorsWhenTooLong() => StaThread.RunAsync(() =>
    {
        Assert.Equal((null, null), TitleRenderer.BuildExportImage(new VideoTitle { Text = "  " }, 640, 360, 10));
        var (img, err) = TitleRenderer.BuildExportImage(new VideoTitle { Text = new string('あ', 80) }, 640, 360, 10);
        Assert.Null(img);
        Assert.Contains("2 行に収まりません", err);
        Assert.NotNull(TitleRenderer.BuildExportImage(new VideoTitle { Text = "a" }, 0, 0, 10).Error);
    });

    [Fact]
    public Task ShortMainShrinksExportFade() => StaThread.RunAsync(() =>
    {
        var (img, _) = TitleRenderer.BuildExportImage(new VideoTitle { Text = "a", FadeIn = true }, 640, 360, 0.2);
        Assert.Equal(0.2, img!.FadeSeconds);
        var (off, _) = TitleRenderer.BuildExportImage(new VideoTitle { Text = "a", FadeIn = false }, 640, 360, 10);
        Assert.Equal(0, off!.FadeSeconds);
    });
}

/// <summary>編集画面の ViewModel（クリア・OK・キャンセル・入力検証）。</summary>
public class TitleEditViewModelTests
{
    private static async Task WithVm(ProjectEditor ed, Func<TitleEditViewModel, Task> body)
    {
        await StaThread.RunAsync(() =>
        {
            var vm = new TitleEditViewModel(ed, 1920, 1080);
            body(vm).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public Task StartsFromProjectTitleAndClearButtonFollowsText()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        return WithVm(ed, vm =>
        {
            Assert.False(vm.CanClear);
            Assert.False(vm.ClearCommand.CanExecute(null));
            vm.Text = "あ";
            Assert.True(vm.ClearCommand.CanExecute(null));
            Assert.Equal((2, true, 72), (vm.Seconds, vm.FadeIn, vm.FontSize));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task ClearEmptiesTextOnlyAndOkRemovesTitleKeepingSettings()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("既存", 4, false, "Yu Gothic UI", 104);
        return WithVm(ed, vm =>
        {
            bool? closed = null;
            vm.CloseRequested += ok => closed = ok;
            vm.ClearCommand.Execute(null);
            Assert.Equal("", vm.Text);
            Assert.True(ed.Project.Title.HasTitle);   // クリアだけでは反映しない
            vm.OkCommand.Execute(null);
            Assert.True(closed);
            Assert.False(ed.Project.Title.HasTitle);
            Assert.Equal((4, false, "Yu Gothic UI", 104), (ed.Project.Title.Seconds, ed.Project.Title.FadeIn, ed.Project.Title.FontFamily, ed.Project.Title.FontSize));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task CancelChangesNothing()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.SetTitle("既存", 3, true);
        var undo = ed.History.UndoCount;
        return WithVm(ed, vm =>
        {
            bool? closed = null;
            vm.CloseRequested += ok => closed = ok;
            vm.Text = "変更後";
            vm.Seconds = 5;
            vm.FadeIn = false;
            vm.FontSize = 120;
            vm.CancelCommand.Execute(null);
            Assert.False(closed);
            Assert.Equal(("既存", 3, true, 72), (ed.Project.Title.Text, ed.Project.Title.Seconds, ed.Project.Title.FadeIn, ed.Project.Title.FontSize));
            Assert.Equal(undo, ed.History.UndoCount);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task OkAppliesEverythingAsOneUndoStep()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        return WithVm(ed, vm =>
        {
            vm.Text = "新しい\nタイトル";
            vm.Seconds = 5;
            vm.FadeIn = false;
            vm.FontSize = 56;
            vm.OkCommand.Execute(null);
            Assert.Equal(1, ed.History.UndoCount);
            Assert.Equal(("新しい\nタイトル", 5, false, 56), (ed.Project.Title.Text, ed.Project.Title.Seconds, ed.Project.Title.FadeIn, ed.Project.Title.FontSize));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task TooLongTextBlocksOkAndShowsGuidance()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        return WithVm(ed, vm =>
        {
            bool? closed = null;
            vm.CloseRequested += ok => closed = ok;
            vm.Text = new string('あ', 80);
            Assert.Contains("2 行に収まりません", vm.LayoutWarning);
            vm.OkCommand.Execute(null);
            Assert.Null(closed);
            Assert.Contains("2 行に収まりません", vm.Error);
            Assert.False(ed.Project.Title.HasTitle);
            // 短くすれば通る。
            vm.Text = "あ";
            vm.OkCommand.Execute(null);
            Assert.True(closed);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task BiggerFontCanMakeTextNoLongerFit()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        return WithVm(ed, vm =>
        {
            vm.Text = new string('あ', 30);
            Assert.Equal("", vm.LayoutWarning);
            vm.FontSize = 120;
            Assert.NotEqual("", vm.LayoutWarning);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task OkWithoutChangeDoesNotTouchHistoryOrDirty()
    {
        var ed = new ProjectEditor(TestProjects.Create());
        ed.MarkSaved();
        return WithVm(ed, vm =>
        {
            vm.OkCommand.Execute(null);
            Assert.Equal(0, ed.History.UndoCount);
            Assert.False(ed.IsDirty);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task DurationTextSeparatesMainAndCompleted()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        return WithVm(ed, vm =>
        {
            Assert.Contains("タイトルなし", vm.DurationText);
            vm.Text = "a";
            Assert.Contains("本編", vm.DurationText);
            Assert.Contains("タイトル 2 秒", vm.DurationText);
            Assert.Contains("完成動画", vm.DurationText);
            return Task.CompletedTask;
        });
    }
}

/// <summary>主画面: ボタンの状態・コマンド・長さ表示。</summary>
public class TitleMainViewModelTests
{
    [Fact]
    public async Task ButtonShowsNoTitleThenCheckedStateThenBack()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync();
        Assert.False(vm.HasTitle);
        Assert.Equal("タイトル…", vm.TitleButtonText);
        Assert.Equal(Loc.Get("Toolbar.Title.Tip"), vm.TitleButtonTip);
        Assert.True(vm.EditTitleCommand.CanExecute(null));

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        windows.TitleEdit = _ => { vm.Editor!.SetTitle("操作説明\n入門", 3, false); return true; };
        vm.EditTitleCommand.Execute(null);
        Assert.True(vm.HasTitle);
        Assert.Equal("✓ タイトルあり", vm.TitleButtonText);
        Assert.Contains("操作説明", vm.TitleButtonTip);
        Assert.Contains("3 秒", vm.TitleButtonTip);
        Assert.Contains("フェードなし", vm.TitleButtonTip);
        Assert.Contains(nameof(MainViewModel.HasTitle), changed);
        Assert.Contains(nameof(MainViewModel.TitleButtonText), changed);
        Assert.Contains("設定しました", vm.StatusMessage);

        vm.UndoCommand.Execute(null);   // Undo でもボタンが戻る
        Assert.False(vm.HasTitle);
        Assert.Equal("タイトル…", vm.TitleButtonText);
        vm.RedoCommand.Execute(null);
        Assert.True(vm.HasTitle);

        windows.TitleEdit = _ => { vm.Editor!.SetTitle("", 3, false); return true; };
        vm.EditTitleCommand.Execute(null);
        Assert.False(vm.HasTitle);
        Assert.Contains("外しました", vm.StatusMessage);
    }

    [Fact]
    public async Task FadeTooltipSaysOn()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        vm.Editor!.SetTitle("A", 2, true);
        Assert.Contains("フェードあり", vm.TitleButtonTip);
    }

    [Theory]
    [InlineData(UiModes.Simple)]
    [InlineData(UiModes.Full)]
    public async Task AvailableInBothModesAndBothPhases(string mode)
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync(mode);
        Assert.True(vm.EditTitleCommand.CanExecute(null));   // カット編集中
        vm.ConfirmCutsCommand.Execute(null);
        Assert.True(vm.IsSubtitlePhase);
        Assert.True(vm.EditTitleCommand.CanExecute(null));   // 字幕編集中
    }

    [Fact]
    public async Task CancelLeavesStatusAndHistoryAlone()
    {
        var (vm, _, windows) = await MainViewModelPhaseTests.OpenAsync();
        var status = vm.StatusMessage;
        windows.TitleEdit = _ => false;
        vm.EditTitleCommand.Execute(null);
        Assert.Equal(status, vm.StatusMessage);
        Assert.Equal(0, vm.Editor!.History.UndoCount);
    }

    [Fact]
    public async Task DurationTextSeparatesMainAndCompletedLength()
    {
        var (vm, _, _) = await MainViewModelPhaseTests.OpenAsync();
        Assert.StartsWith("出力予定", vm.OutputDurationText);
        vm.Editor!.SetTitle("A", 4, true);
        Assert.StartsWith("本編", vm.OutputDurationText);
        Assert.Contains("完成動画", vm.OutputDurationText);
        Assert.Contains("タイトル 4 秒を含む", vm.OutputDurationText);
    }

    [Fact]
    public void EveryTitleStringKeyExists()
    {
        foreach (var k in new[] { "Toolbar.Title", "Toolbar.Title.On", "Toolbar.Title.Tip", "Toolbar.Title.TipOn", "Title.Window", "Title.Intro", "Title.Text",
                     "Title.Hint", "Title.Seconds", "Title.SecondsUnit", "Title.Fade", "Title.Fade.Tip", "Title.FadeOn", "Title.FadeOff", "Title.Preview",
                     "Title.Clear", "Title.Clear.Tip", "Title.Font", "Title.FontSize", "Title.FontSize.Tip", "Title.FontLoading", "Title.Capacity",
                     "Status.TitleSet", "Status.TitleRemoved" })
            Assert.False(Loc.Get(k).StartsWith('!'), k);
    }
}
