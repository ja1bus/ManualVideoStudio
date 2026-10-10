using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Naming;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>キャプション取り込みプレビューの行（新しいキャプションは編集可能）。</summary>
public sealed partial class CaptionRowViewModel : ObservableObject
{
    private readonly ImageFormat _format;

    public CaptionRowViewModel(CaptionImportRow row, ImageFormat format)
    {
        Row = row;
        _format = format;
        _newCaption = row.NewCaption;
        _newStepText = row.NewStepText ?? "";
        _include = row.Include;
    }

    public CaptionImportRow Row { get; }
    public int Id => Row.Id;
    public string SequenceText => Row.Sequence?.ToString() ?? "-";
    public string AppTimeText => Row.AppTime is { } t ? TimeFormat.Format(t) : "-";
    public string AiTimestamp => Row.AiTimestamp ?? "";
    public string CurrentCaption => Row.CurrentCaption;
    public string CurrentStepText => Row.CurrentStepText;
    public bool CanImport => Row.CanImport;
    public string StatusText => Row.Status switch
    {
        CaptionMatchStatus.Ok => "OK",
        CaptionMatchStatus.UnknownId => "id 不一致",
        CaptionMatchStatus.DuplicateId => "重複",
        _ => "空",
    };
    public string Note => Row.Note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilenamePreview))]
    private string _newCaption;

    [ObservableProperty] private bool _include;
    /// <summary>取り込む操作文（v1.9。空なら操作文は変えない）。</summary>
    [ObservableProperty] private string _newStepText;

    public string FilenamePreview => Row.Sequence is { } s ? OutputFilenameBuilder.Build(s, NewCaption, _format) : "";

    partial void OnIncludeChanged(bool value)
    {
        if (value && !CanImport) Include = false;
    }
}

public sealed partial class CutRowViewModel(AiCutRow row) : ObservableObject
{
    public AiCutRow Row { get; } = row;
    public string StartText => Row.IsValid || Row.Start > 0 ? TimeFormat.Format(Row.Start) : "-";
    public string EndText => Row.IsValid || Row.End > 0 ? TimeFormat.Format(Row.End) : "-";
    public string ActionText => Row.Action == SegmentAction.Shorten ? $"短縮（{Row.KeepSeconds:0.0#} 秒残す）" : Row.Action == SegmentAction.Remove ? "削除" : "-";
    public string ConfidenceText => Row.Confidence.ToString("0%");
    public string Reason => Row.Reason;
    public string Note => Row.Note;
    public bool IsValid => Row.IsValid;

    [ObservableProperty] private bool _include = row.Include;

    partial void OnIncludeChanged(bool value)
    {
        if (value && !Row.IsValid) { Include = false; return; }
        Row.Include = value;
    }
}

public sealed record ReviewRow(string TargetId, string Target, string Verdict, string Comment, string Suggestion);

/// <summary>
/// Gemini Chat との手動連携。アプリはプロンプト生成・コピー・回答の解析・確認後の取り込みまでを行う。
/// Gemini API・ブラウザの自動操作は行わない。取り込み前に必ずプレビューを表示し、利用者が確認する。
/// </summary>
public sealed partial class AiAssistViewModel : ObservableObject
{
    public const string GeminiUrl = "https://gemini.google.com/app";

    public static IReadOnlyList<Choice<AiPurpose>> PurposeChoices { get; } =
    [
        new(AiPurpose.CaptureCaption, "手順書画像のキャプション生成（CaptureCaption）"),
        new(AiPurpose.CutAnalysis, "不要・短縮候補の解析（CutAnalysis）"),
        new(AiPurpose.CutReview, "現在の編集案のレビュー（CutReview）"),
    ];

    private readonly MainViewModel _main;
    private readonly IPromptTemplateService _templates;
    private readonly ISettingsService _settings;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<AiAssistViewModel> _logger;
    private AiHistoryEntry? _entry;
    private List<CutRowViewModel> _pendingCuts = [];

    public AiAssistViewModel(MainViewModel main, IPromptTemplateService templates, ISettingsService settings,
        IClipboardService clipboard, IDialogService dialogs, IShellService shell, ILogger<AiAssistViewModel> logger)
    {
        _main = main;
        _templates = templates;
        _settings = settings;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;
        GeneratePrompt();
        RefreshHistory();
    }

    public IReadOnlyList<Choice<AiPurpose>> Purposes => PurposeChoices;
    public string VideoPath => _main.VideoPath;
    public string VideoFileName => Path.GetFileName(_main.VideoPath);

    [ObservableProperty] private AiPurpose _purpose = AiPurpose.CaptureCaption;
    [ObservableProperty] private string _promptText = "";
    [ObservableProperty] private string _promptWarning = "";
    [ObservableProperty] private string _responseText = "";
    [ObservableProperty] private string _parseMessage = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _copyMessage = "";
    [ObservableProperty] private AiHistoryEntry? _selectedHistory;

    public ObservableCollection<CaptionRowViewModel> CaptionRows { get; } = [];
    public ObservableCollection<CutRowViewModel> CutRows { get; } = [];
    public ObservableCollection<ReviewRow> ReviewRows { get; } = [];
    public ObservableCollection<AiHistoryEntry> History { get; } = [];

    public bool IsCaptionMode => Purpose == AiPurpose.CaptureCaption;
    public bool IsCutMode => Purpose != AiPurpose.CaptureCaption;
    public bool IsReviewMode => Purpose == AiPurpose.CutReview;

    public string StepText => Purpose switch
    {
        AiPurpose.CaptureCaption => "手順書画像ごとの時刻（id）を渡し、画像の前後の操作も見てキャプションと手順書の操作文を作ってもらいます。取り込むのはキャプションと操作文だけで、画像の時刻は変わりません。",
        AiPurpose.CutAnalysis => "動画全体を見て、不要・短縮した方がよい区間を挙げてもらいます。取り込んだ結果は「AI 候補」になり、採用するまで編集には反映されません。",
        _ => "現在の確定編集をレビューしてもらいます。指摘は一覧に表示され、追加の提案区間は「AI 候補」として取り込めます。",
    };

    partial void OnPurposeChanged(AiPurpose value)
    {
        OnPropertyChanged(nameof(IsCaptionMode));
        OnPropertyChanged(nameof(IsCutMode));
        OnPropertyChanged(nameof(IsReviewMode));
        OnPropertyChanged(nameof(StepText));
        ClearResult();
        GeneratePrompt();
    }

    [RelayCommand]
    private void GeneratePrompt()
    {
        _entry = null;
        CopyMessage = "";
        var project = _main.Project;
        if (project is null)
        {
            PromptText = "";
            PromptWarning = "動画またはプロジェクトを開いてください。";
            return;
        }
        var template = _templates.Get(Purpose);
        var vars = PromptVariables.Build(project, _settings.Current.Language);
        var r = PromptRenderer.Render(template, vars);
        PromptText = r.Text;
        var warnings = new List<string>();
        if (r.HasWarnings) warnings.Add(r.WarningMessage);
        var missing = PromptVariables.MissingRequired(Purpose, template);
        if (missing.Count > 0) warnings.Add("テンプレートに必要な変数がありません: " + string.Join(", ", missing));
        if (Purpose == AiPurpose.CaptureCaption && project.CapturePoints.Count == 0) warnings.Add("手順書画像が登録されていません。先に画像ポイントを登録してください。");
        if (Purpose == AiPurpose.CutReview && project.CutSegments.Count == 0) warnings.Add("確定したカット区間がありません。");
        PromptWarning = string.Join(Environment.NewLine, warnings);
        OnPropertyChanged(nameof(VideoPath));
        OnPropertyChanged(nameof(VideoFileName));
    }

    [RelayCommand]
    private void CopyPrompt()
    {
        if (string.IsNullOrEmpty(PromptText)) return;
        if (!_clipboard.SetText(PromptText))
        {
            CopyMessage = "クリップボードにコピーできませんでした。もう一度お試しください。";
            return;
        }
        // 実際に Gemini へ渡したプロンプトを履歴に残す。
        _entry = new AiHistoryEntry
        {
            Purpose = Purpose,
            TemplateName = PromptTemplateService.TemplateName(Purpose) + (_templates.IsCustomized(Purpose) ? "（編集済み）" : ""),
            Prompt = PromptText,
            PromptCreatedAt = DateTimeOffset.Now,
        };
        _main.Editor?.UpsertAiHistory(_entry);
        RefreshHistory();
        CopyMessage = $"コピーしました（{DateTime.Now:HH:mm:ss}）。Gemini に動画をアップロードし、このプロンプトを貼り付けて送信してください。";
    }

    [RelayCommand] private void OpenGemini() => _shell.OpenUrl(GeminiUrl);
    [RelayCommand] private void RevealVideo() => _shell.RevealFile(VideoPath);

    [RelayCommand]
    private void CopyVideoPath()
    {
        if (_clipboard.SetText(VideoPath)) CopyMessage = "動画のパスをコピーしました。Gemini のファイル選択画面に貼り付けられます。";
    }

    [RelayCommand]
    private void ClearResponse()
    {
        ResponseText = "";
        ClearResult();
    }

    private void ClearResult()
    {
        CaptionRows.Clear();
        CutRows.Clear();
        ReviewRows.Clear();
        ReviewSummary = "";
        ParseMessage = "";
        ErrorMessage = "";
        HasResult = false;
        _pendingCuts = [];
    }

    [RelayCommand]
    private void Parse()
    {
        ClearResult();
        var project = _main.Project;
        if (project is null) { ErrorMessage = "プロジェクトが開かれていません。"; return; }
        if (string.IsNullOrWhiteSpace(ResponseText)) { ErrorMessage = "Gemini の回答を貼り付けてください。"; return; }

        _entry ??= new AiHistoryEntry
        {
            Purpose = Purpose,
            TemplateName = PromptTemplateService.TemplateName(Purpose),
            Prompt = PromptText,
            PromptCreatedAt = DateTimeOffset.Now,
        };
        _entry.Response = ResponseText;
        _entry.ParsedAt = DateTimeOffset.Now;

        string methodLabel = "";
        var warnings = new List<string>();
        switch (Purpose)
        {
            case AiPurpose.CaptureCaption:
            {
                var r = AiResponseParser.ParseCaptions(ResponseText);
                if (!r.Success) { Fail(r.Error); return; }
                methodLabel = r.Extraction?.MethodLabel ?? "";
                warnings.AddRange(r.Warnings);
                var match = CaptionImporter.Match(r.Items, project.CapturePoints);
                foreach (var row in match.Rows) CaptionRows.Add(new CaptionRowViewModel(row, project.ExportSettings.ImageFormat));
                ParseMessage = $"{r.Items.Count} 件のキャプションを認識しました（取り込み可能 {match.ImportableCount} 件"
                    + (match.MissingIds.Count > 0 ? $"、回答が無い画像 {match.MissingIds.Count} 件: id {string.Join(", ", match.MissingIds)}" : "") + "）。";
                break;
            }
            case AiPurpose.CutAnalysis:
            {
                var r = AiResponseParser.ParseCuts(ResponseText);
                if (!r.Success) { Fail(r.Error); return; }
                methodLabel = r.Extraction?.MethodLabel ?? "";
                warnings.AddRange(r.Warnings);
                AddCutRows(r.Items, project);
                ParseMessage = r.Items.Count == 0
                    ? "不要区間の候補はありませんでした。"
                    : $"{r.Items.Count} 件の区間候補を認識しました（取り込み可能 {CutRows.Count(c => c.IsValid)} 件）。";
                break;
            }
            default:
            {
                var r = AiResponseParser.ParseReview(ResponseText);
                if (!r.Success) { Fail(r.Error); return; }
                methodLabel = r.Extraction?.MethodLabel ?? "";
                warnings.AddRange(r.Warnings);
                ReviewSummary = r.Summary;
                foreach (var rv in r.Reviews)
                {
                    var seg = project.CutSegments.FirstOrDefault(s => s.Id == rv.TargetId);
                    var target = seg is null ? $"（不明な id: {rv.TargetId}）" : $"{TimeFormat.FormatShort(seg.Start)}～{TimeFormat.FormatShort(seg.End)}";
                    var verdict = rv.Verdict switch { "ok" => "問題なし", "adjust" => "要調整", "reject" => "削除すべきでない", _ => rv.Verdict };
                    var suggestion = rv.SuggestedStart is not null || rv.SuggestedEnd is not null ? $"{rv.SuggestedStart}～{rv.SuggestedEnd}" : "";
                    ReviewRows.Add(new ReviewRow(rv.TargetId, target, verdict, rv.Comment, suggestion));
                }
                AddCutRows(r.Cuts, project);
                ParseMessage = $"レビュー {r.Reviews.Count} 件、追加の区間候補 {r.Cuts.Count} 件を認識しました。指摘は確定編集へ自動反映しません。必要に応じて区間を編集してください。";
                break;
            }
        }
        if (methodLabel.Length > 0) ParseMessage += $"（{methodLabel}）";
        if (warnings.Count > 0) ParseMessage += Environment.NewLine + string.Join(Environment.NewLine, warnings);
        HasResult = true;
        _entry.ParseSucceeded = true;
        _entry.ParseMessage = ParseMessage;
        _main.Editor?.UpsertAiHistory(_entry);
        RefreshHistory();

        void Fail(string error)
        {
            ErrorMessage = error + Environment.NewLine + "回答欄の内容を確認・修正してから、もう一度［解析］を押してください。";
            _entry.ParseSucceeded = false;
            _entry.ParseMessage = error;
            _main.Editor?.UpsertAiHistory(_entry);
            _logger.LogWarning("AI response parse failed ({Purpose}): {Error}", Purpose, error);
            RefreshHistory();
        }
    }

    private void AddCutRows(IReadOnlyList<ParsedCut> cuts, ProjectDocument project)
    {
        var rows = AiCutImporter.Validate(cuts, project.VideoMetadata?.Duration ?? 0, _settings.Current.Ui.DefaultShortenKeepSeconds);
        _pendingCuts = rows.Select(r => new CutRowViewModel(r)).ToList();
        foreach (var r in _pendingCuts) CutRows.Add(r);
    }

    [RelayCommand]
    private void Import()
    {
        var editor = _main.Editor;
        if (editor is null || !HasResult || _entry is null) return;
        string summary;
        string json;
        if (Purpose == AiPurpose.CaptureCaption)
        {
            var selected = CaptionRows.Where(r => r.Include && r.CanImport).ToList();
            if (selected.Count == 0) { ErrorMessage = "取り込む行がありません。"; return; }
            var items = selected.Select(r => (r.Id, r.NewCaption.ReplaceLineEndings(" ").Trim(),
                string.IsNullOrWhiteSpace(r.NewStepText) ? null : CaptureText.Line(r.NewStepText))).ToList();
            var n = editor.ApplyCaptions(items, CaptionSource.Ai, "AI キャプションの取り込み");
            var steps = items.Count(i => i.Item3 is not null);
            summary = $"キャプション {n} 件を取り込みました。" + (steps > 0 ? $"（操作文 {steps} 件）" : "");
            json = JsonSerializer.Serialize(items.Select(i => new { id = i.Id, caption = i.Item2, step_text = i.Item3 }), JsonDefaults.Options);
        }
        else
        {
            var selected = _pendingCuts.Where(r => r.Include && r.IsValid).Select(r => r.Row).ToList();
            if (selected.Count == 0) { ErrorMessage = "取り込む候補がありません。"; return; }
            var candidates = AiCutImporter.ToCandidates(selected, _entry.Id);
            if (Purpose == AiPurpose.CutAnalysis)
                editor.ReplaceCandidates(SegmentSource.Ai, candidates, "AI 候補の取り込み");
            else
                editor.AddCandidates(candidates, "AI レビュー候補の取り込み");
            summary = $"AI 候補 {candidates.Count} 件を取り込みました（未判断。採用するまで編集に反映されません）。";
            json = JsonSerializer.Serialize(candidates, JsonDefaults.Options);
            _main.SelectedTabIndex = MainViewModel.TabCandidates;
        }
        _entry.ImportedAt = DateTimeOffset.Now;
        _entry.ImportSummary = summary;
        _entry.ImportedJson = json;
        editor.UpsertAiHistory(_entry);
        RefreshHistory();
        ParseMessage = summary + "（元に戻す: 主画面で Ctrl+Z）";
        _main.StatusMessage = summary;
        HasResult = false;
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var h in (_main.Project?.AiHistory ?? []).OrderByDescending(h => h.PromptCreatedAt ?? h.ParsedAt)) History.Add(h);
    }

    /// <summary>履歴の回答を回答欄へ戻す（再解析用）。</summary>
    [RelayCommand]
    private void RestoreHistory()
    {
        if (SelectedHistory is not { } h) return;
        if (Purpose != h.Purpose) Purpose = h.Purpose;
        PromptText = h.Prompt;
        ResponseText = h.Response;
        _entry = h;
        ClearResult();
    }
}
