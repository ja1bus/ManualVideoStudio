using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;

namespace ManualVideoStudio.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(7.12, "00:00:07.120")]
    [InlineData(80.2, "00:01:20.200")]
    [InlineData(3723.4567, "01:02:03.457")]
    [InlineData(59.9996, "00:01:00.000")]
    [InlineData(-5, "00:00:00.000")]
    public void Format(double seconds, string expected) => Assert.Equal(expected, TimeFormat.Format(seconds));

    [Theory]
    [InlineData("00:00:07.120", 7.12)]
    [InlineData("00:01:20.200", 80.2)]
    [InlineData("1:20.2", 80.2)]
    [InlineData("01:02:03", 3723)]
    [InlineData("90.5", 90.5)]
    [InlineData(" 12 ", 12)]
    [InlineData("00:00:07,120", 7.12)] // SRT 形式
    public void Parse_Valid(string text, double expected)
    {
        Assert.True(TimeFormat.TryParse(text, out var v));
        Assert.Equal(expected, v, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("00:75:00")]
    [InlineData("00:00:61")]
    [InlineData("-1")]
    [InlineData("1:2:3:4")]
    public void Parse_Invalid(string? text) => Assert.False(TimeFormat.TryParse(text, out _));

    [Fact]
    public void RoundTrip()
    {
        foreach (var s in new[] { 0.001, 1.5, 61.25, 3599.999, 7200.5 })
            Assert.Equal(s, TimeFormat.Parse(TimeFormat.Format(s)), 3);
    }

    [Fact]
    public void FFmpegSeconds_IsCultureInvariant()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("12.345", TimeFormat.ToFFmpegSeconds(12.3451));
            Assert.Equal("3", TimeFormat.ToFFmpegSeconds(3));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public void FormatShort_DropsZeroHours()
    {
        Assert.Equal("01:20.200", TimeFormat.FormatShort(80.2));
        Assert.Equal("01:00:00.000", TimeFormat.FormatShort(3600));
    }
}

public class FilenameSanitizerTests
{
    [Fact]
    public void ReplacesInvalidCharacters()
        => Assert.Equal("設定＞ネットワーク：Wi-Fi＼a／b＊c？d”e＜f｜g",
            FilenameSanitizer.Sanitize("設定>ネットワーク:Wi-Fi\\a/b*c?d\"e<f|g"));

    [Fact]
    public void NoInvalidFileNameCharsRemain()
    {
        var s = FilenameSanitizer.Sanitize("a\\/:*?\"<>|\t\r\nb\0c");
        Assert.DoesNotContain(s, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    [Theory]
    [InlineData("末尾ピリオド...", "末尾ピリオド")]
    [InlineData("末尾スペース   ", "末尾スペース")]
    [InlineData("末尾混在. . .", "末尾混在")]
    [InlineData("  前後空白  ", "前後空白")]
    [InlineData("改行\r\nを含む", "改行 を含む")]
    [InlineData("連続   空白", "連続 空白")]
    [InlineData("全角空白で終わる　", "全角空白で終わる")]
    public void TrimsAndNormalizesWhitespace(string input, string expected)
        => Assert.Equal(expected, FilenameSanitizer.Sanitize(input));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("COM10", "COM10")]
    public void HandlesReservedNames(string input, string expected)
        => Assert.Equal(expected, FilenameSanitizer.Sanitize(input));

    [Fact]
    public void TruncatesLongText()
    {
        var s = FilenameSanitizer.Sanitize(new string('あ', 300), 80);
        Assert.Equal(80, s.Length);
    }

    [Fact]
    public void TruncationDoesNotSplitSurrogatePairs()
    {
        var input = "ab" + string.Concat(Enumerable.Repeat("😀", 10));
        var s = FilenameSanitizer.Sanitize(input, 5);
        Assert.Equal("ab😀", s); // 2 + 2 = 4、次の絵文字で 6 になるため切る
        Assert.False(char.IsHighSurrogate(s[^1]));
    }

    [Fact]
    public void TruncationThenTrimsTrailingPeriod()
        => Assert.Equal("abc", FilenameSanitizer.Sanitize("abc. def", 4));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void EmptyResults(string? input) => Assert.Equal("", FilenameSanitizer.Sanitize(input));

    [Fact]
    public void KeepsJapaneseAndSymbolsThatAreValid()
        => Assert.Equal("「Wi-Fi」設定を開く（手順1）＆確認", FilenameSanitizer.Sanitize("「Wi-Fi」設定を開く（手順1）＆確認"));
}

public class OutputFilenameBuilderTests
{
    [Theory]
    [InlineData(1, "Windowsの設定画面を開く", ImageFormat.Png, "001_Windowsの設定画面を開く.png")]
    [InlineData(2, "ネットワークとインターネットを選択", ImageFormat.Png, "002_ネットワークとインターネットを選択.png")]
    [InlineData(3, "Wi-Fi設定を開く", ImageFormat.Jpeg, "003_Wi-Fi設定を開く.jpg")]
    [InlineData(12, "", ImageFormat.Png, "012.png")]
    [InlineData(5, "  ", ImageFormat.Png, "005.png")]
    [InlineData(7, "設定: 開く?", ImageFormat.Png, "007_設定： 開く？.png")]
    [InlineData(1234, "a", ImageFormat.Png, "1234_a.png")]
    public void Build(int seq, string caption, ImageFormat format, string expected)
        => Assert.Equal(expected, OutputFilenameBuilder.Build(seq, caption, format));

    [Fact]
    public void DigitsGrowForLargeCounts()
    {
        Assert.Equal(3, OutputFilenameBuilder.DigitsFor(5));
        Assert.Equal(3, OutputFilenameBuilder.DigitsFor(999));
        Assert.Equal(4, OutputFilenameBuilder.DigitsFor(1000));
        Assert.Equal("0001_x.png", OutputFilenameBuilder.Build(1, "x", ImageFormat.Png, 4));
    }

    [Fact]
    public void RejectsInvalidSequence()
        => Assert.Throws<ArgumentOutOfRangeException>(() => OutputFilenameBuilder.Build(0, "x", ImageFormat.Png));

    [Fact]
    public void ReservedCaptionIsSanitizedIndependentlyOfPrefix()
        => Assert.Equal("001__CON.png", OutputFilenameBuilder.Build(1, "CON", ImageFormat.Png));
}
