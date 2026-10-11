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
    public bool IsValid => Errors.Count == 0;
    public string Note => string.Join(" ", Errors.Concat(Warnings));
}

/// <summary>AI のカット候補を検証して CutCandidate へ変換する。自動確定はしない。</summary>
public static class AiCutImporter
{
    /// <summary>動画末尾をこの秒数まで超えた終了時刻は末尾に丸める（それ以上はエラー）。</summary>
    public const double EndTolerance = 1.0;

    public static List<AiCutRow> Validate(IReadOnlyList<ParsedCut> cuts, double videoDuration, double defaultKeepSeconds)
    {
        var rows = new List<AiCutRow>();
        foreach (var c in cuts)
        {
            var row = new AiCutRow { Reason = c.Reason.ReplaceLineEndings(" ").Trim() };
            if (!TimeFormat.TryParse(c.Start, out var start)) row.Errors.Add($"start を時刻として解釈できません（{c.Start ?? "なし"}）。");
            if (!TimeFormat.TryParse(c.End, out var end)) row.Errors.Add($"end を時刻として解釈できません（{c.End ?? "なし"}）。");
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

            if (c.Confidence is { } conf)
            {
                if (conf is >= 0 and <= 1) row.Confidence = conf;
                else if (conf is > 1 and <= 100)
                {
                    row.Confidence = conf / 100;
                    row.Warnings.Add("confidence を百分率として扱いました。");
                }
                else row.Warnings.Add("confidence が範囲外のため 0 にしました。");
            }

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
