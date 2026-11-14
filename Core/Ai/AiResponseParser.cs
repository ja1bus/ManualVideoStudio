using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.Core.Ai;

public enum JsonExtractionMethod
{
    None,
    /// <summary>回答全体が JSON。</summary>
    Whole,
    /// <summary>Markdown の ```json コードブロック。</summary>
    CodeBlock,
    /// <summary>最初の { から最後の } まで。</summary>
    BraceRange,
}

public sealed record JsonExtraction(bool Success, string Json, JsonExtractionMethod Method, string Error)
{
    public string MethodLabel => Method switch
    {
        JsonExtractionMethod.Whole => "回答全体を JSON として解析",
        JsonExtractionMethod.CodeBlock => "コードブロック内の JSON を抽出",
        JsonExtractionMethod.BraceRange => "最初の { から最後の } までを抽出",
        _ => "",
    };
}

/// <summary>StepText は回答に step_text（または stepText）がある場合だけ（v1.9）。</summary>
public sealed record ParsedCaption(int Id, string? Timestamp, string Caption, string? StepText = null);

public sealed record CaptionParseResult(bool Success, string Error, JsonExtraction? Extraction,
    IReadOnlyList<ParsedCaption> Items, IReadOnlyList<string> Warnings);

public sealed record ParsedCut(string? Start, string? End, string? Action, double? KeepSeconds, double? Confidence, string Reason);

public sealed record CutParseResult(bool Success, string Error, JsonExtraction? Extraction,
    IReadOnlyList<ParsedCut> Items, IReadOnlyList<string> Warnings);

/// <summary>区間方式の回答の 1 件（v1.21）。Category は小文字に正規化。Start / End は区間の一部だけのときの時刻（無ければ null）。</summary>
public sealed record ParsedSegment(int Id, string Category, double? Confidence, string Reason, string? Start = null, string? End = null);

/// <summary>
/// 不要区間の解析の回答（v1.21）。IsSegmentMode なら Segments（区間方式）、そうでなければ Cuts（従来の時刻方式）に結果が入る。
/// </summary>
public sealed record CutAnalysisParseResult(bool Success, string Error, JsonExtraction? Extraction, bool IsSegmentMode,
    IReadOnlyList<ParsedSegment> Segments, IReadOnlyList<ParsedCut> Cuts, IReadOnlyList<string> Warnings)
{
    /// <summary>認識した件数（方式によらない）。</summary>
    public int Count => IsSegmentMode ? Segments.Count : Cuts.Count;
}

public sealed record ParsedReview(string TargetId, string Verdict, string Comment, string? SuggestedStart, string? SuggestedEnd);

public sealed record ReviewParseResult(bool Success, string Error, JsonExtraction? Extraction, string Summary,
    IReadOnlyList<ParsedReview> Reviews, IReadOnlyList<ParsedCut> Cuts, IReadOnlyList<string> Warnings);

/// <summary>
/// Gemini Chat の回答から JSON を取り出して解析する。
/// 取り出しは (1) 全体 → (2) ```json コードブロック → (3) 最初の { ～最後の } の順。
/// 壊れた JSON の推測修復（引用符の補完・括弧の追加など）は行わない。末尾カンマのみ許容する。
/// </summary>
public static partial class AiResponseParser
{
    public const string NotJsonMessage = "JSONとして解析できませんでした。";

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    public static JsonExtraction ExtractJson(string? response)
    {
        var text = (response ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) return new JsonExtraction(false, "", JsonExtractionMethod.None, "回答が空です。");

        if (IsJsonObjectOrArray(text, out _)) return new JsonExtraction(true, text, JsonExtractionMethod.Whole, "");

        string? lastError = null;
        foreach (Match m in CodeBlockPattern().Matches(text))
        {
            var block = m.Groups["body"].Value.Trim();
            if (IsJsonObjectOrArray(block, out var err)) return new JsonExtraction(true, block, JsonExtractionMethod.CodeBlock, "");
            lastError ??= err;
        }

        int first = text.IndexOf('{');
        int last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            var range = text[first..(last + 1)];
            if (IsJsonObjectOrArray(range, out var err)) return new JsonExtraction(true, range, JsonExtractionMethod.BraceRange, "");
            lastError ??= err;
        }
        var detail = lastError is null ? "回答に JSON が見つかりません。" : $"詳細: {lastError}";
        return new JsonExtraction(false, "", JsonExtractionMethod.None, $"{NotJsonMessage}{Environment.NewLine}{detail}");
    }

    private static bool IsJsonObjectOrArray(string text, out string? error)
    {
        error = null;
        if (text.Length == 0 || (text[0] != '{' && text[0] != '[')) { error = "JSON は { または [ で始まる必要があります。"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(text, DocOptions);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------- CaptureCaption ----------------

    public static CaptionParseResult ParseCaptions(string? response)
    {
        var ex = ExtractJson(response);
        if (!ex.Success) return new CaptionParseResult(false, ex.Error, ex, [], []);
        using var doc = JsonDocument.Parse(ex.Json, DocOptions);
        if (!TryGetArray(doc.RootElement, "captures", out var array))
            return new CaptionParseResult(false, "JSON に \"captures\" 配列がありません。", ex, [], []);

        var items = new List<ParsedCaption>();
        var warnings = new List<string>();
        int index = 0;
        foreach (var e in array.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) { warnings.Add($"{index} 件目: オブジェクトではないため無視しました。"); continue; }
            if (!TryGetInt(e, "id", out var id)) { warnings.Add($"{index} 件目: id が数値ではないため無視しました。"); continue; }
            var caption = GetString(e, "caption");
            if (caption is null) { warnings.Add($"{index} 件目（id={id}）: caption がないため無視しました。"); continue; }
            items.Add(new ParsedCaption(id, GetString(e, "timestamp"), caption.Trim(), GetString(e, "step_text") ?? GetString(e, "stepText")));
        }
        if (items.Count == 0) return new CaptionParseResult(false, "有効なキャプションが 1 件もありません。", ex, items, warnings);
        return new CaptionParseResult(true, "", ex, items, warnings);
    }

    // ---------------- CutAnalysis / CutReview ----------------

    public static CutParseResult ParseCuts(string? response)
    {
        var ex = ExtractJson(response);
        if (!ex.Success) return new CutParseResult(false, ex.Error, ex, [], []);
        using var doc = JsonDocument.Parse(ex.Json, DocOptions);
        if (!TryGetArray(doc.RootElement, "cuts", out var array))
            return new CutParseResult(false, "JSON に \"cuts\" 配列がありません。", ex, [], []);
        var warnings = new List<string>();
        var items = ReadCuts(array, warnings);
        // 候補 0 件は「不要区間なし」という正当な回答。
        return new CutParseResult(true, "", ex, items, warnings);
    }

    /// <summary>
    /// 不要区間の解析の回答（v1.21）。ルートのオブジェクトに "segments" 配列があれば区間方式、無ければ従来の
    /// <see cref="ParseCuts"/>（時刻方式。古い回答・編集済みテンプレート・履歴の再解析のため）。"steps" は読み飛ばす。
    /// </summary>
    public static CutAnalysisParseResult ParseCutAnalysis(string? response)
    {
        var ex = ExtractJson(response);
        if (!ex.Success) return new CutAnalysisParseResult(false, ex.Error, ex, false, [], [], []);
        using (var doc = JsonDocument.Parse(ex.Json, DocOptions))
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("segments", out var seg) && seg.ValueKind == JsonValueKind.Array)
            {
                var warnings = new List<string>();
                var items = ReadSegments(seg, warnings);
                // 0 件は「不要区間なし」という正当な回答。
                return new CutAnalysisParseResult(true, "", ex, true, items, [], warnings);
            }
        }
        var cuts = ParseCuts(response);
        if (!cuts.Success)
            return new CutAnalysisParseResult(false, "JSON に \"segments\" 配列も \"cuts\" 配列もありません。", cuts.Extraction, false, [], [], cuts.Warnings);
        return new CutAnalysisParseResult(true, "", cuts.Extraction, false, [], cuts.Items, cuts.Warnings);
    }

    private static List<ParsedSegment> ReadSegments(JsonElement array, List<string> warnings)
    {
        var items = new List<ParsedSegment>();
        int index = 0;
        foreach (var e in array.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) { warnings.Add($"{index} 件目: オブジェクトではないため無視しました。"); continue; }
            if (!TryGetInt(e, "id", out var id)) { warnings.Add($"{index} 件目: id が整数ではないため無視しました。"); continue; }
            items.Add(new ParsedSegment(
                id,
                (GetString(e, "category") ?? "").Trim().ToLowerInvariant(),
                GetDouble(e, "confidence"),
                GetString(e, "reason") ?? "",
                GetTimeText(e, "start"),
                GetTimeText(e, "end")));
        }
        return items;
    }

    public static ReviewParseResult ParseReview(string? response)
    {
        var ex = ExtractJson(response);
        if (!ex.Success) return new ReviewParseResult(false, ex.Error, ex, "", [], [], []);
        using var doc = JsonDocument.Parse(ex.Json, DocOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new ReviewParseResult(false, "JSON のルートがオブジェクトではありません。", ex, "", [], [], []);
        var warnings = new List<string>();
        var reviews = new List<ParsedReview>();
        if (root.TryGetProperty("reviews", out var rv) && rv.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in rv.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                reviews.Add(new ParsedReview(
                    GetString(e, "target_id") ?? "",
                    (GetString(e, "verdict") ?? "").Trim().ToLowerInvariant(),
                    GetString(e, "comment") ?? "",
                    GetString(e, "suggested_start"),
                    GetString(e, "suggested_end")));
            }
        }
        var cuts = root.TryGetProperty("cuts", out var cv) && cv.ValueKind == JsonValueKind.Array ? ReadCuts(cv, warnings) : [];
        var summary = GetString(root, "summary") ?? "";
        if (reviews.Count == 0 && cuts.Count == 0 && summary.Length == 0)
            return new ReviewParseResult(false, "JSON に summary / reviews / cuts のいずれもありません。", ex, "", [], [], warnings);
        return new ReviewParseResult(true, "", ex, summary, reviews, cuts, warnings);
    }

    private static List<ParsedCut> ReadCuts(JsonElement array, List<string> warnings)
    {
        var items = new List<ParsedCut>();
        int index = 0;
        foreach (var e in array.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) { warnings.Add($"{index} 件目: オブジェクトではないため無視しました。"); continue; }
            items.Add(new ParsedCut(
                GetTimeText(e, "start"),
                GetTimeText(e, "end"),
                GetString(e, "action")?.Trim().ToLowerInvariant(),
                GetDouble(e, "keep_seconds"),
                GetDouble(e, "confidence"),
                GetString(e, "reason") ?? ""));
        }
        return items;
    }

    // ---------------- helpers ----------------

    /// <summary>ルートがオブジェクトなら name の配列、ルートが配列ならそれ自体。</summary>
    private static bool TryGetArray(JsonElement root, string name, out JsonElement array)
    {
        array = default;
        if (root.ValueKind == JsonValueKind.Array) { array = root; return true; }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
        {
            array = a;
            return true;
        }
        return false;
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    /// <summary>時刻は文字列（"00:01:20.200"）または数値秒を許容する。</summary>
    private static string? GetTimeText(JsonElement e, string name) => GetString(e, name);

    private static bool TryGetInt(JsonElement e, string name, out int value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out value);
        return p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    [GeneratedRegex(@"```[ \t]*(?:json|JSON|Json)?[ \t]*\r?\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockPattern();
}
