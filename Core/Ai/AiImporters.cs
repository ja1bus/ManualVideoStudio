using System.Globalization;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;

namespace ManualVideoStudio.Core.Ai;

public enum CaptionMatchStatus
{
    /// <summary>取り込み可能。</summary>
    Ok,
    /// <summary>プロジェクトに存在しない id。</summary>
    UnknownId,
    /// <summary>同じ id が回答内で重複（2 件目以降）。</summary>
    DuplicateId,
    /// <summary>キャプションが空。</summary>
    Empty,
}

/// <summary>キャプション取り込みプレビューの 1 行。NewCaption は利用者が編集できる。</summary>
public sealed class CaptionImportRow
{
    public int Id { get; init; }
    public int? Sequence { get; init; }
    /// <summary>アプリ側の画像時刻（SelectedFrameTime）。取り込みでは変更しない。</summary>
    public double? AppTime { get; init; }
    public string? AiTimestamp { get; init; }
    public string CurrentCaption { get; init; } = "";
    public string NewCaption { get; set; } = "";
    /// <summary>現在の操作文（v1.9）。</summary>
    public string CurrentStepText { get; init; } = "";
    /// <summary>回答の操作文（v1.9）。回答に無い・空なら null（取り込みで操作文を変えない）。</summary>
    public string? NewStepText { get; set; }
    public CaptionMatchStatus Status { get; init; }
    public string Note { get; init; } = "";
    public bool Include { get; set; }

    public bool CanImport => Status == CaptionMatchStatus.Ok;
}

public sealed record CaptionMatchResult(IReadOnlyList<CaptionImportRow> Rows, IReadOnlyList<int> MissingIds)
{
    public int ImportableCount => Rows.Count(r => r.CanImport);
}

/// <summary>
/// Gemini のキャプション回答を CapturePoint と照合する。主キーは id。
/// Gemini の timestamp は画像位置として信用せず、差がある場合は注記のみ行う（時刻は変更しない）。
/// </summary>
public static class CaptionImporter
{
    /// <summary>この秒数以上ずれていたら注記する。</summary>
    public const double TimestampNoteThreshold = 0.5;

    public static CaptionMatchResult Match(IReadOnlyList<ParsedCaption> parsed, IReadOnlyList<CapturePoint> points)
    {
        var byId = points.ToDictionary(p => p.Id);
        var seen = new HashSet<int>();
        var rows = new List<CaptionImportRow>();
        foreach (var item in parsed)
        {
            byId.TryGetValue(item.Id, out var point);
            CaptionMatchStatus status;
            string note = "";
            var caption = item.Caption.ReplaceLineEndings(" ").Trim();
            if (point is null) { status = CaptionMatchStatus.UnknownId; note = "この id の画像ポイントはありません。"; }
            else if (!seen.Add(item.Id)) { status = CaptionMatchStatus.DuplicateId; note = "同じ id が重複しています（最初の 1 件のみ取り込みます）。"; }
            else if (caption.Length == 0) { status = CaptionMatchStatus.Empty; note = "キャプションが空です。"; }
            else
            {
                status = CaptionMatchStatus.Ok;
                if (item.Timestamp is not null && TimeFormat.TryParse(item.Timestamp, out var aiTime)
                    && Math.Abs(aiTime - point.SelectedFrameTime) >= TimestampNoteThreshold)
                    note = $"回答の時刻 {item.Timestamp} はアプリの時刻 {TimeFormat.Format(point.SelectedFrameTime)} と異なります（画像位置は変更しません）。";
                if (FilenameSanitizer.Sanitize(caption) != caption)
                    note = (note.Length > 0 ? note + " " : "") + "ファイル名では一部の文字を置き換えます。";
            }
            rows.Add(new CaptionImportRow
            {
                Id = item.Id,
                Sequence = point?.Sequence,
                AppTime = point?.SelectedFrameTime,
                AiTimestamp = item.Timestamp,
                CurrentCaption = point?.Caption ?? "",
                NewCaption = caption,
                CurrentStepText = point?.StepText ?? "",
                NewStepText = string.IsNullOrWhiteSpace(item.StepText) ? null : CaptureText.Line(item.StepText),
                Status = status,
                Note = note,
                Include = status == CaptionMatchStatus.Ok,
            });
        }
        var missing = points.Where(p => !seen.Contains(p.Id)).OrderBy(p => p.Sequence).Select(p => p.Id).ToList();
        return new CaptionMatchResult(rows, missing);
    }
}

/// <summary>AI カット候補取り込みプレビューの 1 行。</summary>
public sealed class AiCutRow
{
    public double Start { get; set; }
    public double End { get; set; }
    public SegmentAction Action { get; set; }
    public double KeepSeconds { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool Include { get; set; }
    /// <summary>区間方式（v1.21）で元にした区間の番号（まとめた場合は複数。時刻方式では空）。</summary>
    public List<int> SegmentIds { get; } = [];
    /// <summary>区間方式（v1.21）の category（wait / mistake / hesitation / unrelated。時刻方式では空）。</summary>
    public string Category { get; set; } = "";
    /// <summary>エラーではないが取り込まない行（v1.21。カット済みの区間・重複した id。理由は Warnings）。</summary>
    public bool Excluded { get; set; }
    public bool IsValid => Errors.Count == 0 && !Excluded;
    public string Note => string.Join(" ", Errors.Concat(Warnings));
}

/// <summary>AI のカット候補を検証して CutCandidate へ変換する。自動確定はしない。</summary>
public static class AiCutImporter
{
    /// <summary>動画末尾をこの秒数まで超えた終了時刻は末尾に丸める（それ以上はエラー）。</summary>
    public const double EndTolerance = 1.0;

    /// <param name="segments">
    /// 履歴の区間表（v1.21。<see cref="AiHistoryEntry.Segments"/>）。あれば開始・終了を <see cref="SnapTolerance"/> 秒以内の最も近い区間の境目へ寄せる（注記）。
    /// null・空なら従来と同じ。
    /// </param>
    public static List<AiCutRow> Validate(IReadOnlyList<ParsedCut> cuts, double videoDuration, double defaultKeepSeconds,
        IReadOnlyList<AiSegment>? segments = null)
    {
        var rows = new List<AiCutRow>();
        foreach (var c in cuts)
        {
            var row = new AiCutRow { Reason = c.Reason.ReplaceLineEndings(" ").Trim() };
            if (!TimeFormat.TryParse(c.Start, out var start)) row.Errors.Add($"start を時刻として解釈できません（{c.Start ?? "なし"}）。");
            if (!TimeFormat.TryParse(c.End, out var end)) row.Errors.Add($"end を時刻として解釈できません（{c.End ?? "なし"}）。");
            if (row.Errors.Count == 0 && segments is { Count: > 0 })
            {
                var s2 = SnapToSegmentBoundary(start, segments);
                var e2 = SnapToSegmentBoundary(end, segments);
                if (e2 > s2 && (s2 != start || e2 != end))
                {
                    row.Warnings.Add($"開始・終了を区間の境目に寄せました（{TimeFormat.Format(start)}〜{TimeFormat.Format(end)} → {TimeFormat.Format(s2)}〜{TimeFormat.Format(e2)}）。");
                    start = s2;
                    end = e2;
                }
            }
            row.Start = start;
            row.End = end;

            switch (c.Action)
            {
                case "remove":
                    row.Action = SegmentAction.Remove;
                    break;
                case "shorten":
                    // 短縮は廃止（v1.17.0）。先頭の keep_seconds 秒を残し、それ以降だけを削除する区間にする。
                    row.Action = SegmentAction.Remove;
                    var keep = c.KeepSeconds is { } k && k > 0 ? Math.Round(k, 3) : defaultKeepSeconds;
                    if (row.Errors.Count == 0 && start + keep < end)
                    {
                        row.Start = Math.Round(start + keep, 3);
                        row.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"shorten を、先頭 {keep:0.0#} 秒を残して以降を削除する区間に変換しました。"));
                    }
                    else if (row.Errors.Count == 0) row.Errors.Add("keep_seconds が区間の長さ以上です。");
                    break;
                default:
                    row.Errors.Add($"action \"{c.Action ?? "なし"}\" は未対応です（remove）。");
                    break;
            }

            ApplyConfidence(row, c.Confidence);

            if (row.Errors.Count == 0)
            {
                if (row.End <= row.Start) row.Errors.Add("end が start 以前です。");
                if (videoDuration > 0 && row.Start >= videoDuration) row.Errors.Add("start が動画の長さを超えています。");
                else if (videoDuration > 0 && row.End > videoDuration)
                {
                    if (row.End - videoDuration <= EndTolerance)
                    {
                        row.End = videoDuration;
                        row.Warnings.Add("end を動画の末尾に合わせました。");
                    }
                    else row.Errors.Add("end が動画の長さを超えています。");
                }
            }
            row.Include = row.IsValid;
            rows.Add(row);
        }
        return rows;
    }

    private static void ApplyConfidence(AiCutRow row, double? confidence)
    {
        if (confidence is not { } conf) return;
        if (conf is >= 0 and <= 1) row.Confidence = conf;
        else if (conf is > 1 and <= 100)
        {
            row.Confidence = conf / 100;
            row.Warnings.Add("confidence を百分率として扱いました。");
        }
        else row.Warnings.Add("confidence が範囲外のため 0 にしました。");
    }

    // ---------------- 区間方式（v1.21。設計 4.4） ----------------

    /// <summary>時刻方式の開始・終了を区間の境目へ寄せる範囲（秒）。</summary>
    public const double SnapTolerance = 1.0;
    /// <summary>区間方式の start / end が区間の外へこの秒数を超えてはみ出したら、区間全体にする。</summary>
    public const double SegmentStickOutTolerance = 1.0;
    /// <summary>wait の開始をずらした後に、この秒数未満しか残らないならずらさない。</summary>
    public const double MinWaitCandidateSeconds = 0.5;

    /// <summary>category の表示（理由の頭に付ける）。未知の category は null。</summary>
    public static string? CategoryLabel(string? category) => category switch
    {
        "wait" => "待ち",
        "mistake" => "やり直し",
        "hesitation" => "迷い",
        "unrelated" => "無関係",
        _ => null,
    };

    /// <summary>
    /// 時刻を、<paramref name="tolerance"/> 秒以内で最も近い区間の境目（各区間の開始・終了）へ寄せる。範囲内に境目が無ければそのまま。
    /// </summary>
    public static double SnapToSegmentBoundary(double time, IReadOnlyList<AiSegment> segments, double tolerance = SnapTolerance)
    {
        double best = time, bestDist = double.MaxValue;
        foreach (var s in segments)
        {
            foreach (var b in (ReadOnlySpan<double>)[s.Start, s.End])
            {
                var d = Math.Abs(b - time);
                if (d <= tolerance && d < bestDist) { best = b; bestDist = d; }
            }
        }
        return best;
    }

    /// <summary>
    /// 区間方式の回答を検証して、取り込みプレビューの行にする（v1.21）。
    /// <list type="bullet">
    /// <item>id が区間表に無い → エラーの行。</item>
    /// <item>確定カットに 99% 以上含まれる区間（カット済み）・同じ id の 2 件目以降 → 警告つきで取り込まない行（<see cref="AiCutRow.Excluded"/>）。</item>
    /// <item>start / end があれば区間の中へ収める。区間の外へ 1 秒を超えてはみ出すなら警告して区間全体にする。</item>
    /// <item>番号が連続し、同じ category の区間は 1 つの行にまとめる（理由は「；」でつなぎ、信頼度は小さい方）。
    /// start / end で狭めた区間は、つなぎ目の側が区間の端と一致するときだけまとめる。</item>
    /// <item>wait は開始を <paramref name="defaultKeepSeconds"/> だけ後ろへずらす（0.5 秒未満しか残らなければずらさない。どちらも注記）。</item>
    /// <item>理由の頭に「［待ち］」などを付ける（未知の category は付けずに警告）。</item>
    /// </list>
    /// 行は、取り込める行（開始の順）→ 取り込まない行・エラーの行（回答の順）の順に返す。
    /// </summary>
    /// <param name="table">履歴の区間表（<see cref="AiHistoryEntry.Segments"/>）。</param>
    /// <param name="confirmedCuts">今の確定カット（カット済みの判定に使う）。</param>
    public static List<AiCutRow> ValidateSegments(IReadOnlyList<ParsedSegment> items, IReadOnlyList<AiSegment> table,
        IEnumerable<CutSegment> confirmedCuts, double videoDuration, double defaultKeepSeconds)
    {
        var byId = new Dictionary<int, AiSegment>();
        foreach (var s in table) byId.TryAdd(s.Id, s);
        var duration = videoDuration > 0 ? videoDuration : table.Count > 0 ? table.Max(s => s.End) : 0;
        var keep = duration > 0
            ? Editing.EditTimelineCalculator.ComputeKeepRanges(duration, confirmedCuts)
            : (IReadOnlyList<Editing.TimeRange>)[];

        var problems = new List<AiCutRow>();
        var parts = new List<(AiSegment Seg, ParsedSegment Item, AiCutRow Row)>();
        var seen = new HashSet<int>();
        foreach (var item in items)
        {
            var reason = item.Reason.ReplaceLineEndings(" ").Trim();
            if (!byId.TryGetValue(item.Id, out var seg))
            {
                var bad = new AiCutRow { Reason = reason, Category = item.Category };
                bad.SegmentIds.Add(item.Id);
                bad.Errors.Add($"区間 {item.Id} は区間の表にありません。");
                problems.Add(bad);
                continue;
            }
            var row = new AiCutRow { Start = seg.Start, End = seg.End, Action = SegmentAction.Remove, Reason = reason, Category = item.Category };
            row.SegmentIds.Add(item.Id);
            if (!seen.Add(item.Id))
            {
                row.Excluded = true;
                row.Warnings.Add($"区間 {item.Id} が重複しています（最初の 1 件のみ取り込みます）。");
                problems.Add(row);
                continue;
            }
            var segLen = seg.End - seg.Start;
            if (segLen > 0 && duration > 0
                &&segLen - SegmentTableBuilder.Overlap(keep, seg.Start, seg.End) >= segLen * SegmentTableBuilder.CutCoverRatio)
            {
                row.Excluded = true;
                row.Warnings.Add($"区間 {item.Id} はカット済みのため取り込みません。");
                problems.Add(row);
                continue;
            }

            ApplyRange(row, seg, item);
            ApplyConfidence(row, item.Confidence);
            parts.Add((seg, item, row));
        }

        // 番号が連続し、同じ category の区間をまとめる。
        var merged = new List<AiCutRow>();
        AiCutRow? current = null;
        int lastId = 0;
        AiSegment? lastSeg = null;
        foreach (var (seg, item, row) in parts.OrderBy(p => p.Seg.Id))
        {
            bool join = current is not null && seg.Id == lastId + 1 && current.Category == row.Category
                && current.End == lastSeg!.End && row.Start == seg.Start;
            if (join)
            {
                current!.End = row.End;
                current.SegmentIds.Add(seg.Id);
                current.Confidence = Math.Min(current.Confidence, row.Confidence);
                if (row.Reason.Length > 0 && !current.Reason.Split('；').Contains(row.Reason))
                    current.Reason = current.Reason.Length > 0 ? current.Reason + "；" + row.Reason : row.Reason;
                foreach (var w in row.Warnings) if (!current.Warnings.Contains(w)) current.Warnings.Add(w);
            }
            else
            {
                current = row;
                merged.Add(row);
            }
            lastId = seg.Id;
            lastSeg = seg;
        }

        foreach (var row in merged)
        {
            if (row.SegmentIds.Count > 1)
                row.Warnings.Insert(0, $"区間 {string.Join("・", row.SegmentIds)} を 1 つにまとめました。");
            if (row.Category == "wait" && defaultKeepSeconds > 0)
            {
                if (row.End - (row.Start + defaultKeepSeconds) >= MinWaitCandidateSeconds)
                {
                    row.Start = Math.Round(row.Start + defaultKeepSeconds, 3);
                    row.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"結果を確認できるよう、先頭 {defaultKeepSeconds:0.0#} 秒を残しました。"));
                }
                else row.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"区間が短いため、先頭 {defaultKeepSeconds:0.0#} 秒を残していません。"));
            }
            var label = CategoryLabel(row.Category);
            if (label is not null) row.Reason = $"［{label}］{row.Reason}";
            else row.Warnings.Add($"category \"{(row.Category.Length > 0 ? row.Category : "なし")}\" は未対応のため、種類を付けていません。");
            if (row.End <= row.Start) row.Errors.Add("end が start 以前です。");
            row.Include = row.IsValid;
        }
        foreach (var row in problems) row.Include = false;
        return [.. merged.OrderBy(r => r.Start), .. problems];
    }

    /// <summary>start / end があれば区間の中へ収める（区間の外へ大きくはみ出すなら区間全体）。</summary>
    private static void ApplyRange(AiCutRow row, AiSegment seg, ParsedSegment item)
    {
        bool hasStart = !string.IsNullOrWhiteSpace(item.Start);
        bool hasEnd = !string.IsNullOrWhiteSpace(item.End);
        if (!hasStart && !hasEnd) return;
        double s = seg.Start, e = seg.End;
        if (hasStart && !TimeFormat.TryParse(item.Start, out s))
        {
            row.Warnings.Add($"start を時刻として解釈できないため、区間全体にしました（{item.Start}）。");
            return;
        }
        if (hasEnd && !TimeFormat.TryParse(item.End, out e))
        {
            row.Warnings.Add($"end を時刻として解釈できないため、区間全体にしました（{item.End}）。");
            return;
        }
        if (!hasStart) s = seg.Start;
        if (!hasEnd) e = seg.End;
        if (s < seg.Start - SegmentStickOutTolerance || e > seg.End + SegmentStickOutTolerance || e <= s)
        {
            row.Warnings.Add($"start / end が区間 {seg.Id} の範囲外のため、区間全体にしました。");
            return;
        }
        // 秒単位の回答（"00:05:12"）は区間の端と少しずれるため、区間の中へ収める。
        row.Start = Math.Clamp(s, seg.Start, seg.End);
        row.End = Math.Clamp(e, seg.Start, seg.End);
        if (row.End <= row.Start)
        {
            row.Start = seg.Start;
            row.End = seg.End;
            row.Warnings.Add($"start / end が区間 {seg.Id} の範囲外のため、区間全体にしました。");
        }
    }

    public static List<CutCandidate> ToCandidates(IEnumerable<AiCutRow> rows, string? historyId)
        => rows.Where(r => r.Include && r.IsValid).Select(r => new CutCandidate
        {
            Start = Math.Round(r.Start, 3),
            End = Math.Round(r.End, 3),
            SuggestedAction = r.Action,
            KeepSeconds = r.Action == SegmentAction.Shorten ? r.KeepSeconds : 0,
            Confidence = r.Confidence,
            Reason = r.Reason,
            Source = SegmentSource.Ai,
            Kind = CandidateKind.Ai,
            AiHistoryId = historyId,
        }).ToList();
}
