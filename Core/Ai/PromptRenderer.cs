using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;

namespace ManualVideoStudio.Core.Ai;

public sealed record PromptRenderResult(string Text, IReadOnlyList<string> UnknownVariables, IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => UnknownVariables.Count > 0 || Warnings.Count > 0;

    public string WarningMessage => string.Join(Environment.NewLine,
        UnknownVariables.Select(v => $"未定義の変数 {{{{{v}}}}} はそのまま残しました。").Concat(Warnings));
}

/// <summary>
/// テンプレート中の {{NAME}}（英大文字・数字・_）を展開する。
/// 未知の変数は置き換えずに残して警告を返す（プロンプト生成自体は止めない）。
/// </summary>
public static partial class PromptRenderer
{
    public static PromptRenderResult Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        var unknown = new List<string>();
        var warnings = new List<string>();
        var text = VariablePattern().Replace(template ?? "", m =>
        {
            var name = m.Groups[1].Value.ToUpperInvariant();
            if (variables.TryGetValue(name, out var value)) return value;
            if (!unknown.Contains(name)) unknown.Add(name);
            return m.Value;
        });

        // 展開後に残った "{{" は閉じ忘れや不正な変数名（小文字・記号入り）。
        foreach (Match m in BrokenPattern().Matches(text))
        {
            var fragment = m.Value.Length > 30 ? m.Value[..30] + "…" : m.Value;
            if (VariablePattern().IsMatch(m.Value)) continue; // 未知の変数（報告済み）
            warnings.Add($"変数として解釈できない記述があります: {fragment.ReplaceLineEndings(" ")}");
        }
        return new PromptRenderResult(text, unknown, warnings);
    }

    /// <summary>テンプレートに含まれる変数名（重複なし・出現順）。</summary>
    public static IReadOnlyList<string> FindVariables(string template)
        => VariablePattern().Matches(template ?? "").Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct().ToList();

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"\{\{[^\r\n]{0,40}")]
    private static partial Regex BrokenPattern();
}

/// <summary>プロンプト変数の一覧と、プロジェクトからの値の生成。</summary>
public static class PromptVariables
{
    public const string VideoFilename = "VIDEO_FILENAME";
    public const string VideoDuration = "VIDEO_DURATION";
    public const string VideoResolution = "VIDEO_RESOLUTION";
    public const string CapturePoints = "CAPTURE_POINTS";
    public const string CaptureCount = "CAPTURE_COUNT";
    public const string CutSegments = "CUT_SEGMENTS";
    public const string CutCount = "CUT_COUNT";
    public const string EditedDuration = "EDITED_DURATION";
    public const string Language = "LANGUAGE";
    public const string Date = "DATE";
    // 手順書作成の依頼文（v1.9）の変数。
    public const string OutputDir = "OUTPUT_DIR";
    public const string OutputDirHome = "OUTPUT_DIR_HOME";
    public const string ProjectName = "PROJECT_NAME";
    public const string ImageCount = "IMAGE_COUNT";

    /// <summary>手順書作成の依頼文で使える変数。</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Tejunsho =
    [
        (OutputDirHome, "手順書用の書き出しの出力先（ユーザーフォルダーの中なら ~/Videos/… の形。別の PC でも同じ場所に写せば通じる。外ならフルパス）"),
        (OutputDir, "手順書用の書き出しの出力先（フルパス）"),
        (ProjectName, "プロジェクト名（meta.json の題名の下書きと同じ）"),
        (ImageCount, "書き出した画像の件数"),
        (Date, "今日の日付（yyyy-MM-dd）"),
    ];

    /// <summary>字幕依頼の文（v1.12）で使える変数。動画はカット後の本編の動画（完成動画の本編部分と同じ時間軸。タイトルは含まない）。</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> SubtitleRequest =
    [
        (EditedDuration, "字幕依頼用動画（カット後）の長さ（HH:MM:SS.mmm）"),
        (VideoResolution, "解像度（例: 1920x1080）"),
        (Language, "回答言語（設定値。既定は「日本語」）"),
        (Date, "今日の日付（yyyy-MM-dd）"),
    ];

    /// <summary>用途ごとに使える変数。</summary>
    public static IReadOnlyList<(string Name, string Description)> For(AiPurpose purpose)
        => purpose switch { AiPurpose.TejunshoRequest => Tejunsho, AiPurpose.SubtitleRequest => SubtitleRequest, _ => All };

    /// <summary>設定画面に表示する変数の説明。</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (VideoFilename, "元動画のファイル名（例: manual.mp4）"),
        (VideoDuration, "元動画の長さ（HH:MM:SS.mmm）"),
        (VideoResolution, "解像度（例: 1920x1080）"),
        (CapturePoints, "画像ポイント一覧の JSON（id・no（連番）・timestamp・current_caption・current_step_text）"),
        (CaptureCount, "画像ポイントの件数"),
        (CutSegments, "確定済み編集区間の JSON（id・start・end・action・keep_seconds・reason）"),
        (CutCount, "確定済み編集区間の件数"),
        (EditedDuration, "編集後の動画の長さ（HH:MM:SS.mmm）"),
        (Language, "回答言語（設定値。既定は「日本語」）"),
        (Date, "今日の日付（yyyy-MM-dd）"),
    ];

    /// <summary>
    /// 用途ごとに必須の変数（テンプレートに無い場合に警告する）。1 つの項目に複数の名前があれば、どれか 1 つがあればよい
    /// （依頼文の出力先は {{OUTPUT_DIR_HOME}} か {{OUTPUT_DIR}}。v1.10）。
    /// </summary>
    public static IReadOnlyList<string[]> Required(AiPurpose purpose) => purpose switch
    {
        AiPurpose.CaptureCaption => [[CapturePoints]],
        AiPurpose.CutReview => [[CutSegments]],
        AiPurpose.TejunshoRequest => [[OutputDirHome, OutputDir]],
        _ => [],
    };

    /// <summary>テンプレートに無い必須の変数（表示用。「{{A}} または {{B}}」）。</summary>
    public static IReadOnlyList<string> MissingRequired(AiPurpose purpose, string template)
    {
        var vars = PromptRenderer.FindVariables(template);
        return Required(purpose).Where(g => !g.Any(vars.Contains))
            .Select(g => string.Join(" または ", g.Select(v => "{{" + v + "}}"))).ToList();
    }

    public static Dictionary<string, string> Build(ProjectDocument project, string language)
    {
        var duration = project.VideoMetadata?.Duration ?? 0;
        var keep = EditTimelineCalculator.ComputeKeepRanges(duration, project.CutSegments);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VideoFilename] = Path.GetFileName(project.SourceVideo.Path),
            [VideoDuration] = TimeFormat.Format(duration),
            [VideoResolution] = project.VideoMetadata?.Resolution ?? "",
            [CapturePoints] = CapturePointsJson(project.CapturePoints),
            [CaptureCount] = project.CapturePoints.Count.ToString(CultureInfo.InvariantCulture),
            [CutSegments] = CutSegmentsJson(project.CutSegments),
            [CutCount] = project.CutSegments.Count.ToString(CultureInfo.InvariantCulture),
            [EditedDuration] = TimeFormat.Format(EditTimelineCalculator.OutputDuration(keep)),
            [Language] = string.IsNullOrWhiteSpace(language) ? "日本語" : language,
            [Date] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public static string CapturePointsJson(IEnumerable<CapturePoint> points)
        => JsonSerializer.Serialize(points.OrderBy(p => p.Sequence).Select(p => new Dictionary<string, object>
        {
            ["id"] = p.Id,
            ["no"] = p.Sequence,
            ["timestamp"] = TimeFormat.Format(p.SelectedFrameTime),
            ["current_caption"] = p.Caption,
            ["current_step_text"] = p.StepText,
        }), JsonDefaults.Options);

    public static string CutSegmentsJson(IEnumerable<CutSegment> cuts)
        => JsonSerializer.Serialize(cuts.OrderBy(c => c.Start).Select(c => new Dictionary<string, object>
        {
            ["id"] = c.Id,
            ["start"] = TimeFormat.Format(c.Start),
            ["end"] = TimeFormat.Format(c.End),
            ["action"] = c.Action == SegmentAction.Shorten ? "shorten" : "remove",
            ["keep_seconds"] = c.Action == SegmentAction.Shorten ? c.KeepSeconds : 0,
            ["reason"] = c.Reason,
        }), JsonDefaults.Options);
}
