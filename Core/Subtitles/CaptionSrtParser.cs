using System.Globalization;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Subtitles;

/// <summary>SRT の字幕ブロック 1 件（時刻は秒。出力動画＝カット後の動画の先頭から）。</summary>
public sealed record SrtEntry(int Number, double Start, double End, string Text, int LineIndex);

/// <summary>解析・検証で見つかった問題（貼り付けた文章の何行目か分かる）。</summary>
public sealed record SrtIssue(int? BlockNumber, int? LineIndex, string Message)
{
    /// <summary>画面に出す 1 件分の文（行番号は 1 始まり）。</summary>
    public string Display => LineIndex is { } l ? $"（{l + 1} 行目）{Message}" : Message;
}

/// <summary>Gemini の回答（または SRT 単体）を解析・検証した結果（v1.12）。</summary>
public sealed class SrtParseResult
{
    public IReadOnlyList<SrtEntry> Entries { get; init; } = [];
    public IReadOnlyList<SrtIssue> Errors { get; init; } = [];
    /// <summary>回答全体のコードブロックから SRT を取り出したか。</summary>
    public bool ExtractedFromCodeBlock { get; init; }
    public bool IsValid => Errors.Count == 0 && Entries.Count > 0;

    /// <summary>
    /// 既存の字幕データ（<see cref="SubtitleSegment"/>）にする。位置・文字サイズは新しい字幕の既定（アプリ設定）。
    /// 時刻は出力動画基準でそのまま使う（検証済みの前提）。
    /// </summary>
    public List<SubtitleSegment> ToSegments(SubtitleStyle defaults)
        => Entries.Select(e => new SubtitleSegment
        {
            Start = e.Start,
            End = e.End,
            Text = SubtitleValidator.NormalizeText(e.Text),
            Position = defaults.Position,
            FontSize = SubtitleStyle.NormalizeFontSize(defaults.FontSize),
        }).ToList();
}

/// <summary>
/// Gemini の回答全体・SRT 本文を解析して検証する（v1.12。VideoCaptionLight の <c>SubtitleParser</c> を元にした）。
/// 回答全体のコードブロック内の SRT を取り出し、ブロック外の文章（推奨スタイル・表など）は無視する。
/// 検証は ManualVideoStudio の字幕の規則（<see cref="SubtitleValidator"/>）に揃える: 表示時間の重なり・動画長の超過・
/// 最短表示時間・文字数・行数。不正な字幕を黙って削除・短縮せず、問題をすべて返す。
/// </summary>
public static class CaptionSrtParser
{
    private static readonly Regex FenceLine = new(
        @"^\s*(?<fence>`{3,}|~{3,})\s*(?<lang>[^\s`~]*)\s*$", RegexOptions.Compiled);

    // 数字は \d ではなく [0-9]（.NET の \d は全角数字にも一致し、int.Parse が失敗するため）。
    private static readonly Regex TimestampLine = new(
        @"^(?<sh>[0-9]{2}):(?<sm>[0-9]{2}):(?<ss>[0-9]{2}),(?<sms>[0-9]{3})\s*-->\s*(?<eh>[0-9]{2}):(?<em>[0-9]{2}):(?<es>[0-9]{2}),(?<ems>[0-9]{3})$",
        RegexOptions.Compiled);

    private static readonly Regex LooseTimestampLine = new(
        @"^[0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2}(?:[.,:][0-9]{1,3})?\s*-->\s*[0-9]{1,2}:[0-9]{1,2}:[0-9]{1,2}(?:[.,:][0-9]{1,3})?$",
        RegexOptions.Compiled);

    /// <param name="input">貼り付けた文章。</param>
    /// <param name="outputDuration">カット後の動画の長さ（秒）。0 以下なら長さの検証を省く。</param>
    public static SrtParseResult Parse(string? input, double outputDuration)
    {
        var lines = SplitLines(input ?? "");
        if (lines.All(string.IsNullOrWhiteSpace))
            return Fail("Gemini の回答（または SRT）が貼り付けられていません。", null, null, false);

        var region = LocateRegion(lines);
        if (region.Error is not null)
            return new SrtParseResult { Errors = [region.Error], ExtractedFromCodeBlock = region.FromCodeBlock };

        var entries = new List<SrtEntry>();
        int index = region.Start, expected = 1;
        while (true)
        {
            while (index < region.End && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index >= region.End) break;
            int blockStart = index;
            while (index < region.End && !string.IsNullOrWhiteSpace(lines[index])) index++;
            var (entry, error) = ParseBlock(lines, blockStart, index, expected);
            if (error is not null)
                return new SrtParseResult { Entries = entries, Errors = [error], ExtractedFromCodeBlock = region.FromCodeBlock };
            entries.Add(entry!);
            expected++;
        }
        if (entries.Count == 0)
            return Fail("字幕ブロックが見つかりません。", null, region.Start, region.FromCodeBlock);

        return new SrtParseResult { Entries = entries, Errors = Validate(entries, outputDuration), ExtractedFromCodeBlock = region.FromCodeBlock };
    }

    /// <summary>ManualVideoStudio の字幕の規則に照らして検証する（重なりは先に書かれた字幕との間で 1 回だけ報告する）。</summary>
    private static List<SrtIssue> Validate(IReadOnlyList<SrtEntry> entries, double outputDuration)
    {
        var issues = new List<SrtIssue>();
        var accepted = new List<SubtitleSegment>();
        foreach (var e in entries)
        {
            var seg = new SubtitleSegment { Start = e.Start, End = e.End, Text = SubtitleValidator.NormalizeText(e.Text) };
            var v = SubtitleValidator.Validate(seg.Start, seg.End, seg.Text, outputDuration, accepted);
            if (!v.IsValid)
                foreach (var m in v.Errors) issues.Add(new SrtIssue(e.Number, e.LineIndex, $"{e.Number}番目の字幕: {m}"));
            accepted.Add(seg);
        }
        return issues;
    }

    private static string[] SplitLines(string text)
    {
        text = text.Replace("﻿", "", StringComparison.Ordinal).Replace("​", "", StringComparison.Ordinal);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private sealed record Region(int Start, int End, bool FromCodeBlock, SrtIssue? Error);

    private sealed record CodeBlock(int FenceLine, int ContentStart, int ContentEnd, string Language);

    /// <summary>解析する範囲。コードフェンスが無ければ全体、あればその中の SRT ブロック 1 つ。</summary>
    private static Region LocateRegion(string[] lines)
    {
        var fences = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (FenceLine.IsMatch(lines[i])) fences.Add(i);
        if (fences.Count == 0) return new Region(0, lines.Length, false, null);

        var blocks = new List<CodeBlock>();
        var k = 0;
        // 開きフェンスを含めずにコピーした場合、最初のフェンスは閉じフェンス。その前が SRT らしければ 1 ブロックとみなす。
        if (fences.Count % 2 == 1 && LanguageOf(lines[fences[0]]).Length == 0 && LooksLikeSrt(lines, 0, fences[0]))
        {
            blocks.Add(new CodeBlock(-1, 0, fences[0], ""));
            k = 1;
        }
        for (; k < fences.Count; k += 2)
        {
            var open = fences[k];
            var close = k + 1 < fences.Count ? fences[k + 1] : lines.Length;
            blocks.Add(new CodeBlock(open, open + 1, close, LanguageOf(lines[open])));
        }

        var candidates = blocks.Where(b => b.Language.Equals("srt", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) candidates = blocks.Where(b => LooksLikeSrt(lines, b.ContentStart, b.ContentEnd)).ToList();
        if (candidates.Count == 0 && blocks.Count == 1) candidates.Add(blocks[0]);

        if (candidates.Count == 0)
            return new Region(0, 0, true, new SrtIssue(null, fences[0], "コードブロックの中に SRT が見つかりません。"));
        if (candidates.Count > 1)
            return new Region(0, 0, true, new SrtIssue(null, candidates[1].FenceLine, $"SRT のコードブロックが {candidates.Count} 個あります。1 つだけ貼り付けてください。"));
        var chosen = candidates[0];
        return new Region(chosen.ContentStart, chosen.ContentEnd, true, null);
    }

    private static string LanguageOf(string fenceLine) => FenceLine.Match(fenceLine).Groups["lang"].Value;

    private static bool LooksLikeSrt(string[] lines, int start, int end)
    {
        var nonBlank = Enumerable.Range(start, Math.Max(0, end - start))
            .Where(i => !string.IsNullOrWhiteSpace(lines[i])).Take(2).Select(i => lines[i].Trim()).ToArray();
        return nonBlank.Length == 2
            && int.TryParse(nonBlank[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && nonBlank[1].Contains("-->", StringComparison.Ordinal);
    }

    private static (SrtEntry? Entry, SrtIssue? Error) ParseBlock(string[] lines, int start, int end, int expected)
    {
        var label = $"{expected}番目の字幕ブロック";
        var numberText = lines[start].Trim();
        if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            if (TimestampLine.IsMatch(numberText) || LooseTimestampLine.IsMatch(numberText))
                return (null, new SrtIssue(expected, start, $"{label}に番号の行がありません。時刻の行の前に番号を入れてください。"));
            return (null, new SrtIssue(expected, start, $"{label}の 1 行目が番号ではありません:「{Shorten(numberText)}」。SRT 以外の文章が混ざっていないか確認してください。"));
        }
        if (number != expected)
            return (null, new SrtIssue(expected, start, $"{label}の番号が {number} になっています。番号は 1 から順に振ってください。"));
        if (end - start < 2)
            return (null, new SrtIssue(expected, start, $"{label}に時刻の行がありません。"));

        var timeText = lines[start + 1].Trim();
        var match = TimestampLine.Match(timeText);
        if (!match.Success)
        {
            var hint = LooseTimestampLine.IsMatch(timeText) ? "（時・分・秒は 2 桁、ミリ秒は 3 桁で、秒とミリ秒の区切りはカンマ「,」です）" : "";
            return (null, new SrtIssue(expected, start + 1, $"{label}のタイムスタンプを確認してください:「{Shorten(timeText)}」。形式: HH:MM:SS,mmm --> HH:MM:SS,mmm{hint}"));
        }
        foreach (var group in new[] { "sm", "ss", "em", "es" })
            if (int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) > 59)
                return (null, new SrtIssue(expected, start + 1, $"{label}のタイムスタンプで、分または秒が 59 を超えています:「{timeText}」"));

        var startTime = ToSeconds(match, "sh", "sm", "ss", "sms");
        var endTime = ToSeconds(match, "eh", "em", "es", "ems");
        if (endTime <= startTime)
            return (null, new SrtIssue(expected, start + 1, $"{label}の終了時刻が開始時刻より後になっていません:「{timeText}」"));

        var textLines = new List<string>();
        for (var i = start + 2; i < end; i++)
        {
            var line = lines[i].Trim();
            if (TimestampLine.IsMatch(line))
                return (null, new SrtIssue(expected, i, $"{label}の本文に時刻の行があります。字幕ブロックの間には空行を 1 行入れてください。"));
            textLines.Add(line);
        }
        if (textLines.Count == 0)
            return (null, new SrtIssue(expected, start + 1, $"{label}の字幕本文がありません。"));
        return (new SrtEntry(number, startTime, endTime, string.Join("\n", textLines), start), null);
    }

    private static double ToSeconds(Match m, string h, string min, string s, string ms)
        => int.Parse(m.Groups[h].Value, CultureInfo.InvariantCulture) * 3600.0
           + int.Parse(m.Groups[min].Value, CultureInfo.InvariantCulture) * 60.0
           + int.Parse(m.Groups[s].Value, CultureInfo.InvariantCulture)
           + int.Parse(m.Groups[ms].Value, CultureInfo.InvariantCulture) / 1000.0;

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static SrtParseResult Fail(string message, int? block, int? line, bool fromCode)
        => new() { Errors = [new SrtIssue(block, line, message)], ExtractedFromCodeBlock = fromCode };
}
