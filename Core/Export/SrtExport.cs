using System.Globalization;
using System.Text;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Export;

/// <summary>字幕ファイル（SRT）の内容と、書き出したブロックの件数。</summary>
public sealed record SrtDocument(string Text, int Count);

/// <summary>
/// 字幕ファイル（SRT）の書き出し（v1.22、仕様書 §44）。UI に依存しない。
/// 時刻は本編（カット後）基準の字幕に、冒頭タイトルの実際の長さを足して完成動画の時刻にする。
/// 本文は利用者が入力した改行だけを使い、焼き込み用の自動改行・位置・大きさ・帯などの見た目は出さない。
/// </summary>
public static class SrtExport
{
    public const string FileFilter = "SRT 字幕 (*.srt)|*.srt";

    /// <summary>BOM 付きの UTF-8。</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private const string NewLine = "\r\n";

    /// <summary>
    /// SRT の本文を作る。開始（同じなら終了）の昇順、同じ値は渡した順。番号は 1 から。改行は CRLF、各ブロックの後に空行 1 行。
    /// 本文が空になる字幕は出さず、件数にも数えない。渡した字幕は変更しない。
    /// </summary>
    public static SrtDocument Build(IEnumerable<SubtitleSegment> subtitles, double offsetSeconds = 0)
    {
        var sb = new StringBuilder();
        var number = 0;
        foreach (var s in subtitles.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            var body = NormalizeBody(s.Text);
            if (body.Length == 0) continue;
            var start = ToMilliseconds(s.Start + offsetSeconds);
            var end = ToMilliseconds(s.End + offsetSeconds);
            // 丸めで終了が開始以下になるのは長さ 1 ミリ秒未満の字幕だけ（検証で最短 0.2 秒のため通常は起きない）。
            if (end <= start && s.End > s.Start) end = start + 1;
            number++;
            sb.Append(number.ToString(CultureInfo.InvariantCulture)).Append(NewLine)
              .Append(FormatTime(start)).Append(" --> ").Append(FormatTime(end)).Append(NewLine)
              .Append(body).Append(NewLine)
              .Append(NewLine);
        }
        return new SrtDocument(sb.ToString(), number);
    }

    /// <summary>
    /// 本文を SRT 用に整える: 行に分け、各行の前後の空白を除き、空の行を除いて CRLF でつなぐ。
    /// 行の前後の空白（字下げを含む）を除くのは、SRT の表示先でほぼ意味を持たず、読む側（<see cref="Subtitles.CaptionSrtParser"/>）も除くため。
    /// 元の本文とは「各行の前後の空白と空行を除いた本文」同士で一致する（AC-8。2026-11-16 利用者判断、レビュー R2）。
    /// </summary>
    public static string NormalizeBody(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join(NewLine, lines);
    }

    /// <summary>秒を整数のミリ秒にする（0.5 ミリ秒は 0 から遠い方へ。負は 0）。大小関係を逆にしない。</summary>
    public static long ToMilliseconds(double seconds)
    {
        var ms = Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        return ms <= 0 || double.IsNaN(ms) ? 0 : (long)ms;
    }

    /// <summary>HH:MM:SS,mmm（時は 2 桁以上）。</summary>
    public static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;
        var h = milliseconds / 3_600_000;
        var m = milliseconds / 60_000 % 60;
        var s = milliseconds / 1000 % 60;
        var ms = milliseconds % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00},{ms:000}");
    }

    /// <summary>
    /// 字幕の時刻に足す秒数（MP4 の先頭にあるタイトルの実際の長さ）。タイトルなしは 0。
    /// MP4 の書き出しはタイトルの画像を元動画の fps で <see cref="FFmpegArguments.TitleFrameCount"/> コマだけ繰り返して本編の前に連結するため、
    /// 本編の開始は「コマ数 ÷ fps」（fps が 0 以下なら 30）。SRT は MP4 の時間軸を基準にする。
    /// GIF は品質ごとの fps（8・12・15）でコマ数を決めるため、元動画が分数の fps だと GIF の 1 コマ以内ずれる（§44.4、レビュー R5）。
    /// 冒頭フェードは本編の明るさだけを変え、長さを変えない。
    /// </summary>
    public static double TitleOffsetSeconds(VideoTitle? title, double fps)
    {
        if (title is not { HasTitle: true }) return 0;
        var effectiveFps = fps > 0 ? fps : 30;
        var frames = FFmpegArguments.TitleFrameCount(new TitleFilterPlan("", VideoTitle.NormalizeSeconds(title.Seconds), 0, effectiveFps));
        return frames / effectiveFps;
    }

    /// <summary>既定の保存先: MP4 の既定（<see cref="VideoExportService.DefaultOutputPath"/>）の拡張子を .srt にしたもの。</summary>
    public static string DefaultOutputPath(string inputPath, ExportSettings settings, string? projectPath = null)
        => Path.ChangeExtension(VideoExportService.DefaultOutputPath(inputPath, settings, projectPath), ".srt");

    /// <summary>BOM 付き UTF-8 で原子的に書く（一時ファイルに書いてから置き換える）。</summary>
    public static void Write(string path, SrtDocument document) => AtomicFile.WriteAllText(path, document.Text, FileEncoding);
}
