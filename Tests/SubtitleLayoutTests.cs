using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Core.Subtitles;

namespace ManualVideoStudio.Tests;

/// <summary>字幕の自動折り返し（v1.5。改行位置をアプリで決める）。幅は 1 文字 = 1 で測る。</summary>
public class SubtitleLineBreakerTests
{
    private static IReadOnlyList<string> Break(string text, double max) => SubtitleLineBreaker.Break(text, max, s => s.Length);

    [Fact]
    public void ShortTextIsOneLine() => Assert.Equal(["ボタンを押します"], Break("ボタンを押します", 20));

    [Fact]
    public void LongJapaneseTextIsWrappedWithinWidth()
    {
        var lines = Break("ここでは設定画面を開いて、ネットワークの項目を選んでから保存ボタンを押してください。", 12);
        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.True(l.Length <= 12, l));
        Assert.Equal("ここでは設定画面を開いて、ネットワークの項目を選んでから保存ボタンを押してください。", string.Concat(lines));
    }

    [Fact]
    public void PunctuationDoesNotStartALine()
    {
        // 10 文字目の後に「、」が来る: 「、」を行頭に置かず、1 文字前で改行する。
        var lines = Break("あいうえおかきくけこ、さしすせそ", 10);
        Assert.Equal(["あいうえおかきくけ", "こ、さしすせそ"], lines);
        Assert.DoesNotContain(lines, l => l.StartsWith('、'));
    }

    [Fact]
    public void SmallKanaAndClosingBracketDoNotStartALine()
    {
        Assert.DoesNotContain(Break("あいうえおかきくけッてすと", 10), l => l.StartsWith('ッ'));
        Assert.DoesNotContain(Break("あいうえお（かきく）けこさしす", 10), l => l.StartsWith('）'));
    }

    [Fact]
    public void OpeningBracketDoesNotEndALine()
    {
        var lines = Break("あいうえお「かきくけこ」です", 6);
        Assert.DoesNotContain(lines, l => l.EndsWith('「'));
    }

    [Theory]
    [InlineData("あいうえおかきくIPアドレス", "IP")]
    [InlineData("あいうえおかきWi-Fiの設定", "Wi-Fi")]
    [InlineData("あいうえお192.168.1.1へ", "192.168.1.1")]
    public void AlphanumericWordIsNotSplit(string text, string word)
    {
        var lines = Break(text, 12);
        Assert.Contains(lines, l => l.Contains(word, StringComparison.Ordinal));
    }

    [Fact]
    public void WordLongerThanLineIsSplit()
    {
        var lines = Break("abcdefghijklmnop", 5);
        Assert.Equal(["abcde", "fghij", "klmno", "p"], lines);
    }

    [Fact]
    public void BreaksAtSpaceAndDropsIt() => Assert.Equal(["hello world", "foo bar"], Break("hello world foo bar", 11));

    [Fact]
    public void UserNewlinesAreKept() => Assert.Equal(["1行目", "2行目"], Break("1行目\n2行目", 20));
}

public class SubtitleLayoutAndOffsetTests
{
    private static string Event(SubtitleSegment s, SubtitleStyle? style = null)
    {
        s.Start = 1;
        s.End = 2;
        var ass = AssSubtitleBuilder.Build([s], style ?? new SubtitleStyle(), 1920, 1080);
        return ass.Split('\n').Single(l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
    }

    [Fact]
    public void BottomOffsetUpRaisesMarginV()
        => Assert.Contains(",Default,,0,0,108,,{\\an2\\fs48", Event(new SubtitleSegment { Text = "a", VerticalOffset = 54 }));

    [Fact]
    public void TopOffsetUpLowersMarginV()
        => Assert.Contains(",Default,,0,0,27,,{\\an8\\fs48", Event(new SubtitleSegment { Text = "a", Position = SubtitlePosition.Top, VerticalOffset = 27 }));

    [Fact]
    public void BottomOffsetDownStopsAtScreenEdge()
        => Assert.Contains(",Default,,0,0,1,,{\\an2", Event(new SubtitleSegment { Text = "a", VerticalOffset = -270 }));

    [Fact]
    public void MiddleWithoutOffsetIsCenteredAndWithOffsetIsPositioned()
    {
        Assert.Contains("{\\an5\\fs48", Event(new SubtitleSegment { Text = "a", Position = SubtitlePosition.Middle }));
        Assert.Contains("{\\an5\\pos(960,486)\\fs48", Event(new SubtitleSegment { Text = "a", Position = SubtitlePosition.Middle, VerticalOffset = 54 }));
    }

    [Fact]
    public void MarginSettingIsUsedAndScaledToVideoHeight()
    {
        Assert.Contains(",Default,,0,0,108,,", Event(new SubtitleSegment { Text = "a" }, new SubtitleStyle { MarginVertical = 108 }));
        var ass = AssSubtitleBuilder.Build([new SubtitleSegment { Start = 1, End = 2, Text = "a", VerticalOffset = 27 }], new SubtitleStyle(), 1280, 720);
        Assert.Contains(",Default,,0,0,54,,", ass); // (54 + 27) × 720 / 1080
    }

    [Fact]
    public void LongTextIsBrokenWithExplicitNewlines()
    {
        var text = string.Concat(Enumerable.Repeat("設定画面を開いて保存ボタンを押します。", 4));
        var e = Event(new SubtitleSegment { Text = text });
        var body = e[(e.IndexOf('}') + 1)..];
        var lines = body.Split("\\N");
        Assert.True(lines.Length >= 2, e);
        // 見積もり（全角 = 文字サイズ 48px）で 1 行の幅 (1920 - 192) × 0.96 に収まる = 34 文字以下。
        Assert.All(lines, l => Assert.True(l.Length <= 34, l));
    }

    [Fact]
    public void LayoutIsSharedWithPreview()
    {
        var l = AssSubtitleBuilder.Layout(new SubtitleSegment { Position = SubtitlePosition.Middle, VerticalOffset = -54 }, 1920, 1080);
        Assert.Equal((5, 594.0), (l.AssAlignment, l.CenterY));
    }

    [Fact]
    public void StyleNormalizeFixesOutOfRangeValues()
    {
        var st = new SubtitleStyle { MarginVertical = 9999, FontSize = 50, Preset = (SubtitlePreset)99, Position = (SubtitlePosition)9, FontFamily = " " };
        st.Normalize();
        Assert.Equal((270.0, 48.0, SubtitleStyle.DefaultPreset, SubtitlePosition.Bottom, "Meiryo"), (st.MarginVertical, st.FontSize, st.Preset, st.Position, st.FontFamily));
        Assert.Equal(27, SubtitleStyle.NormalizeOffset(20));
        Assert.Equal(SubtitleStyle.MaxOffset, SubtitleStyle.NormalizeOffset(10000));
    }
}

public class SubtitleLayoutEditingTests
{
    private static (ProjectEditor Editor, SubtitleSegment Segment) Setup()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        Assert.True(ed.ConfirmCuts().Success);
        var (_, seg) = ed.AddSubtitle(1, 3, "字幕");
        return (ed, seg!);
    }

    [Fact]
    public void PositionSizeAndOffsetAreUndoable()
    {
        var (ed, seg) = Setup();
        Assert.True(ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Top, 48, 0).Success);
        Assert.Equal("字幕の位置の変更", ed.History.UndoLabel);
        Assert.True(ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Top, 48, 27).Success);
        Assert.Equal("字幕の上下のずらしの変更", ed.History.UndoLabel);
        Assert.True(ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Top, 64, 27).Success);
        Assert.Equal("字幕の文字サイズの変更", ed.History.UndoLabel);
        var s = ed.Project.SubtitleSegments.Single();
        Assert.Equal((SubtitlePosition.Top, 64.0, 27.0), (s.Position, s.FontSize, s.VerticalOffset));
        ed.Undo();
        ed.Undo();
        ed.Undo();
        s = ed.Project.SubtitleSegments.Single();
        Assert.Equal((SubtitlePosition.Bottom, 48.0, 0.0), (s.Position, s.FontSize, s.VerticalOffset));
    }

    [Fact]
    public void SameValuesAreNotRecordedAndOffsetIsClamped()
    {
        var (ed, seg) = Setup();
        var before = ed.History.UndoLabel;
        Assert.True(ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Bottom, 48, 0).Success);
        Assert.Equal(before, ed.History.UndoLabel);
        ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Bottom, 48, 99999);
        Assert.Equal(SubtitleStyle.MaxOffset, ed.Project.SubtitleSegments.Single().VerticalOffset);
    }

    [Fact]
    public void UpdateFromDialogKeepsOffset()
    {
        var (ed, seg) = Setup();
        ed.SetSubtitleLayout(seg.Id, SubtitlePosition.Bottom, 48, 54);
        Assert.True(ed.UpdateSubtitle(seg.Id, 1, 4, "変更", SubtitlePosition.Bottom, 48).Success);
        Assert.Equal(54, ed.Project.SubtitleSegments.Single().VerticalOffset);
    }

    [Fact]
    public void RequiresSubtitlePhase()
    {
        var ed = new ProjectEditor(TestProjects.Create(60));
        Assert.False(ed.SetSubtitleLayout("none", SubtitlePosition.Top, 48, 0).Success);
    }
}

public class SubtitleSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvs-subset-" + Guid.NewGuid().ToString("N"));

    public SubtitleSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void OldSettingsWithoutSubtitleGetDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "language": "日本語" }""");
        var s = new SettingsService(path).Load();
        Assert.Equal((SubtitlePreset.TranslucentBand, "Meiryo", 54.0, 48.0, SubtitlePosition.Bottom),
            (s.Subtitle.Preset, s.Subtitle.FontFamily, s.Subtitle.MarginVertical, s.Subtitle.FontSize, s.Subtitle.Position));
    }

    [Fact]
    public void SubtitleSettingsRoundTrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Load();
        svc.Current.Subtitle = new SubtitleStyle { Preset = SubtitlePreset.TranslucentBand, FontFamily = "BIZ UDPGothic", MarginVertical = 108, FontSize = 64, Position = SubtitlePosition.Top };
        svc.Save();
        var s = new SettingsService(path).Load().Subtitle;
        Assert.Equal((SubtitlePreset.TranslucentBand, "BIZ UDPGothic", 108.0, 64.0, SubtitlePosition.Top), (s.Preset, s.FontFamily, s.MarginVertical, s.FontSize, s.Position));
    }
}

public class SubtitleFontCheckTests
{
    [Fact]
    public void SelectedFontIsReadFromStderr()
    {
        const string err = "[Parsed_subtitles_0 @ 0] fontselect: (Arial, 400, 0) -> ArialMT, 0, ArialMT\n[Parsed_subtitles_0 @ 0] fontselect: (Yu Gothic Medium, 400, 0) -> YuGothic-Medium, 0, YuGothic-Medium\n";
        Assert.Equal("YuGothic-Medium", SubtitleSampleRenderer.SelectedFont(err, "Yu Gothic Medium"));
        Assert.Null(SubtitleSampleRenderer.SelectedFont(err, "Meiryo"));
    }

    [Theory]
    [InlineData("Meiryo", "Meiryo", true)]
    [InlineData("BIZ UDPGothic", "BIZ-UDPGothic", true)]
    [InlineData("Yu Gothic", "YuGothic-Regular", true)]
    [InlineData("Comic Font", "ArialMT", false)]
    public void ExpectedFontByName(string requested, string selected, bool expected)
        => Assert.Equal(expected, SubtitleSampleRenderer.IsExpectedFont(requested, selected, null));

    [Fact]
    public void PostScriptNamesFromFontFileDecide()
        => Assert.True(SubtitleSampleRenderer.IsExpectedFont("AR P丸ゴシック体E", "YentiEGP4-Extra-SJIS", ["YentiEGP4-Extra-SJIS"]));
}
