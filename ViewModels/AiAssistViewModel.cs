using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Ai;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Guidance;
using ManualVideoStudio.Core.Logging;
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
    public string ActionText => Row.Action == SegmentAction.Remove ? "カット（削除）" : "-";
    public string ConfidenceText => Row.Confidence.ToString("0%");
    public string Reason => Row.Reason;
    public string Note => Row.Note;
    public bool IsValid => Row.IsValid;
    /// <summary>区間方式（v1.21）の種類（待ち・やり直しなど。時刻方式・未知の category は空 / そのまま）。</summary>
    public string CategoryText => Row.Category.Length == 0 ? "" : AiCutImporter.CategoryLabel(Row.Category) ?? Row.Category;
    /// <summary>区間方式（v1.21）で元にした区間の番号（「12〜14」「12, 15」。時刻方式では空）。</summary>
    public string SegmentIdsText => SegmentIds(Row.SegmentIds);
    /// <summary>取り込まない行（カット済み・重複）。一覧では灰色にする。</summary>
    public bool IsExcluded => Row.Excluded;

    public static string SegmentIds(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return "";
        if (ids.Count == 1) return ids[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool consecutive = ids.Zip(ids.Skip(1)).All(p => p.Second == p.First + 1);
        return consecutive ? $"{ids[0]}〜{ids[^1]}" : string.Join(", ", ids);
    }

    [ObservableProperty] private bool _include = row.Include;

    partial void OnIncludeChanged(bool value)
    {
        if (value && !Row.IsValid) { Include = false; return; }
        Row.Include = value;
    }
}

public sealed record ReviewRow(string TargetId, string Target, string Verdict, string Comment, string Suggestion);

/// <summary>
/// 不要区間の解析の依頼用動画（時刻と区間番号入り。v1.21）の状態。アプリに 1 つ（AI 連携の画面を閉じても残る。アプリの終了で失われる）。
/// 字幕依頼用動画（<see cref="CaptionRequestSession"/>）と同じく、使えなくなった動画は確認なしに消さず <see cref="Orphans"/> に残す。
/// </summary>
public sealed class CutRequestSession
{
    /// <summary>準備済みの依頼用動画（無ければ null）。</summary>
    public CaptionRequestVideo? Video { get; set; }

    /// <summary>使えなくなったが、まだ削除していない依頼用動画（このアプリが作ったもの）。</summary>
    public List<CaptionRequestVideo> Orphans { get; } = [];

    /// <summary>準備済みの動画を使わなくする（作り直すとき）。ファイルが残っていれば削除の確認用に残す。</summary>
    public void Invalidate()
    {
        if (Video is { } v && File.Exists(v.Path)) Orphans.Add(v);
        Video = null;
    }
}

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
        new(AiPurpose.CutAnalysis, "不要区間（カットする区間）の解析（CutAnalysis）"),
        new(AiPurpose.CutReview, "現在の編集案のレビュー（CutReview）"),
    ];

    private readonly MainViewModel _main;
    private readonly IPromptTemplateService _templates;
    private readonly ISettingsService _settings;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<AiAssistViewModel> _logger;
    private readonly AiRequestVideoService? _requestVideos;
    private readonly CaptionRequestStore? _store;
    private readonly IWindowService? _windows;
    private readonly CutRequestSession _requestSession;
    private AiHistoryEntry? _entry;
    private List<CutRowViewModel> _pendingCuts = [];
    /// <summary>表示中のプロンプトに使った区間表（不要区間の解析のときだけ）。</summary>
    private SegmentTable? _table;
    /// <summary>この画面で簡易解析の自動実行を試したか（キャンセル・失敗でも繰り返さない）。</summary>
    private bool _analysisAttempted;

    /// <param name="requestVideos">依頼用動画（時刻と区間番号入り）の作成。null なら作成のボタンは使えない。</param>
    /// <param name="requestSession">依頼用動画の状態（アプリに 1 つ）。null なら画面ごとに持つ。</param>
    public AiAssistViewModel(MainViewModel main, IPromptTemplateService templates, ISettingsService settings,
        IClipboardService clipboard, IDialogService dialogs, IShellService shell, ILogger<AiAssistViewModel> logger,
        AiRequestVideoService? requestVideos = null, CaptionRequestStore? store = null, IWindowService? windows = null,
        CutRequestSession? requestSession = null)
    {
        _main = main;
        _templates = templates;
        _settings = settings;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;
        _requestVideos = requestVideos;
        _store = store;
        _windows = windows;
        _requestSession = requestSession ?? new CutRequestSession();
        RunLocalAnalysis = async () =>
        {
            if (_main.RunAnalysisCommand.CanExecute(null)) await _main.RunAnalysisCommand.ExecuteAsync(null);
        };
        // 解析候補の［AI に聞く］からは「不要区間の解析」で開く。簡易モードも同じ。
        var start = main.TakePendingAiPurpose() ?? (main.IsSimpleMode ? AiPurpose.CutAnalysis : (AiPurpose?)null);
        if (start is { } p) _purpose = p;
        RenderPrompt();
        RefreshHistory();
    }

    /// <summary>
    /// 簡易解析を実行する処理（既定は主画面の［簡易解析］と同じコマンド。キャンセル・失敗は主画面側で案内する）。
    /// 試験で差し替えられるよう公開している。
    /// </summary>
    public Func<Task> RunLocalAnalysis { get; set; }

    /// <summary>選べる用途。簡易モードでは「不要区間の解析」だけ（v1.17.0）。</summary>
    public IReadOnlyList<Choice<AiPurpose>> Purposes => _main.IsSimpleMode ? PurposeChoices.Where(c => c.Value == AiPurpose.CutAnalysis).ToList() : PurposeChoices;
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
    /// <summary>［クリップボードから貼り付け］の結果（成功した文字数、または失敗の理由）。</summary>
    [ObservableProperty] private string _pasteMessage = "";

    /// <summary>［閉じる］［取り込んで閉じる］で画面を閉じる要求（閉じるだけでは、未取り込みの回答は反映しない）。</summary>
    public event Action? CloseRequested;

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
        AiPurpose.CutAnalysis => "動画全体を見て、不要な（カットする）区間を挙げてもらいます。取り込んだ結果は「AI 候補」になり、採用するまで編集には反映されません。",
        _ => "現在の確定編集をレビューしてもらいます。指摘は一覧に表示され、追加の提案区間は「AI 候補」として取り込めます。",
    };

    partial void OnPurposeChanged(AiPurpose value)
    {
        OnPropertyChanged(nameof(IsCaptionMode));
        OnPropertyChanged(nameof(IsCutMode));
        OnPropertyChanged(nameof(IsReviewMode));
        OnPropertyChanged(nameof(StepText));
        ClearResult();
        RenderPrompt();
    }

    // ---------------- 区間表（不要区間の解析。v1.21） ----------------

    /// <summary>表示中のプロンプトの区間表（不要区間の解析のときだけ。それ以外は null）。</summary>
    public SegmentTable? SegmentTable => _table;

    /// <summary>区間表の区間の数と、回答の受け取り方の説明（不要区間の解析のときだけ）。</summary>
    [ObservableProperty] private string _segmentInfo = "";

    public bool HasSegmentInfo => SegmentInfo.Length > 0;
    partial void OnSegmentInfoChanged(string value) => OnPropertyChanged(nameof(HasSegmentInfo));

    /// <summary>不要区間の解析のテンプレートが区間表（{{SEGMENT_TABLE}}）を使うか。</summary>
    private bool TemplateUsesSegments => Purpose == AiPurpose.CutAnalysis
        && _templates.Get(AiPurpose.CutAnalysis).Contains("{{" + PromptVariables.SegmentTable + "}}", StringComparison.Ordinal);

    /// <summary>
    /// 区間表を作る前に簡易解析を実行するか（不要区間の解析で区間表を使い、画面変化が無く、簡易解析を一度も実行していない。
    /// この画面で一度試したら、キャンセル・失敗でも繰り返さない）。
    /// </summary>
    public bool NeedsLocalAnalysis
    {
        get
        {
            if (_analysisAttempted || !TemplateUsesSegments || _main.Project is not { } project) return false;
            if (project.SceneChanges.Count > 0) return false;
            var local = project.CutCandidates.Count(c => c.Source == SegmentSource.LocalAnalysis);
            return WorkflowGuide.InferAnalysisState(project.SceneChanges.Count, local) == AnalysisRunState.NotRun;
        }
    }

    /// <summary>必要なら簡易解析を先に実行し、プロンプト（区間表）を作り直す。実行したかどうかを状態の文字で知らせる。</summary>
    private async Task EnsureSegmentTableAsync()
    {
        if (!NeedsLocalAnalysis) return;
        _analysisAttempted = true;
        CopyMessage = Loc.Get("Ai.Seg.Analysis.Running");
        try
        {
            await RunLocalAnalysis();
        }
        catch (Exception ex)
        {
            // 解析できなくても、画面変化なしの区間表で続ける。
            _logger.LogWarning(ex, "Local analysis before the segment table failed");
        }
        RenderPrompt();
        var project = _main.Project;
        var ran = project is not null && (project.SceneChanges.Count > 0 || project.CutCandidates.Any(c => c.Source == SegmentSource.LocalAnalysis));
        CopyMessage = ran
            ? Loc.Format("Ai.Seg.Analysis.Done", project!.SceneChanges.Count)
            : Loc.Get("Ai.Seg.Analysis.Skipped");
    }

    /// <summary>［再生成］: 必要なら簡易解析を実行してから、最新の内容でプロンプトを作り直す。</summary>
    [RelayCommand]
    private async Task GeneratePromptAsync()
    {
        RenderPrompt();
        await EnsureSegmentTableAsync();
    }

    private void RenderPrompt()
    {
        _entry = null;
        CopyMessage = "";
        _table = null;
        SegmentInfo = "";
        var project = _main.Project;
        if (project is null)
        {
            PromptText = "";
            PromptWarning = "動画またはプロジェクトを開いてください。";
            OnPropertyChanged(nameof(SegmentTable));
            RefreshRequestVideo();
            return;
        }
        var template = _templates.Get(Purpose);
        if (Purpose == AiPurpose.CutAnalysis)
        {
            _table = SegmentTableBuilder.FromProject(project,
                _main.InputIdleRanges(_settings.Current.Analysis.FreezeMinSeconds), _main.RecordingMarkers);
            SegmentInfo = TemplateUsesSegments
                ? Loc.Format("Ai.Seg.Count", _table.Count)
                : Loc.Get("Ai.Seg.NoTableInTemplate");
        }
        var vars = PromptVariables.Build(project, _settings.Current.Language, _table);
        var r = PromptRenderer.Render(template, vars);
        PromptText = r.Text;
        var warnings = new List<string>();
        if (r.HasWarnings) warnings.Add(r.WarningMessage);
        var missing = PromptVariables.MissingRequired(Purpose, template);
        if (missing.Count > 0) warnings.Add("テンプレートに必要な変数がありません: " + string.Join(", ", missing));
        if (Purpose == AiPurpose.CaptureCaption && project.CapturePoints.Count == 0) warnings.Add("手順書画像が登録されていません。先に画像ポイントを登録してください。");
        if (Purpose == AiPurpose.CutReview && project.CutSegments.Count == 0) warnings.Add("確定したカット区間がありません。");
        if (NeedsLocalAnalysis) warnings.Add(Loc.Get("Ai.Seg.Analysis.Pending"));
        PromptWarning = string.Join(Environment.NewLine, warnings);
        OnPropertyChanged(nameof(SegmentTable));
        OnPropertyChanged(nameof(VideoPath));
        OnPropertyChanged(nameof(VideoFileName));
        RefreshRequestVideo();
    }

    [RelayCommand]
    private async Task CopyPromptAsync()
    {
        // 画面変化が未解析なら、区間表を作る前に簡易解析を実行する（設計 4.1）。
        await EnsureSegmentTableAsync();
        var before = CopyMessage;
        if (string.IsNullOrEmpty(PromptText)) return;
        if (!_clipboard.SetText(PromptText))
        {
            CopyMessage = "クリップボードにコピーできませんでした。もう一度お試しください。";
            return;
        }
        // 実際に Gemini へ渡したプロンプトを履歴に残す。区間表もこの時点のものを残し、取り込みで番号を時刻へ戻すのに使う。
        _entry = new AiHistoryEntry
        {
            Purpose = Purpose,
            TemplateName = PromptTemplateService.TemplateName(Purpose) + (_templates.IsCustomized(Purpose) ? "（編集済み）" : ""),
            Prompt = PromptText,
            PromptCreatedAt = DateTimeOffset.Now,
            Segments = CurrentSegments(),
        };
        _main.Editor?.UpsertAiHistory(_entry);
        RefreshHistory();
        var copied = $"コピーしました（{DateTime.Now:HH:mm:ss}）。Gemini に動画をアップロードし、このプロンプトを貼り付けて送信してください。";
        CopyMessage = before.Length > 0 ? before + Environment.NewLine + copied : copied;
    }

    /// <summary>履歴に残す区間表（不要区間の解析のときだけ。それ以外は空）。</summary>
    private List<AiSegment> CurrentSegments() => Purpose == AiPurpose.CutAnalysis && _table is { } t ? t.ToHistory() : [];

    // ---------------- 依頼用動画（時刻と区間番号入り。v1.21） ----------------

    [ObservableProperty] private string _requestVideoStatus = "";
    [ObservableProperty] private string _requestVideoMessage = "";
    [ObservableProperty] private string _requestOrphanText = "";

    public bool HasRequestVideo => _requestSession.Video is not null;
    public bool HasRequestOrphans => _requestSession.Orphans.Count > 0;
    public string RequestVideoPath => _requestSession.Video?.Path ?? "";
    public bool CanMakeRequestVideo => _requestVideos is not null && _store is not null && _windows is not null;
    public bool IsCutAnalysisMode => Purpose == AiPurpose.CutAnalysis;

    /// <summary>依頼用動画の状態の表示を更新する。</summary>
    private void RefreshRequestVideo()
    {
        var project = _main.Project;
        if (_requestSession.Video is { } v && !File.Exists(v.Path)) _requestSession.Video = null;
        _requestSession.Orphans.RemoveAll(o => !File.Exists(o.Path));
        if (_requestSession.Video is { } video)
        {
            var size = new FileInfo(video.Path).Length / 1024.0 / 1024.0;
            RequestVideoStatus = project is not null && _table is { } t && AiRequestVideoService.IsCurrent(video, project, t.ToHistory())
                ? Loc.Format("Ai.Seg.Video.Ready", Path.GetFileName(video.Path), size)
                : Loc.Get("Ai.Seg.Video.Stale");
        }
        else RequestVideoStatus = Loc.Get("Ai.Seg.Video.None");
        RequestOrphanText = _requestSession.Orphans.Count == 0 ? "" : Loc.Format("Ai.Seg.Video.Orphans", _requestSession.Orphans.Count);
        OnPropertyChanged(nameof(HasRequestVideo));
        OnPropertyChanged(nameof(HasRequestOrphans));
        OnPropertyChanged(nameof(RequestVideoPath));
        OnPropertyChanged(nameof(IsCutAnalysisMode));
    }

    /// <summary>
    /// ［依頼用動画を作る（時刻と区間番号入り）］: 元動画の下に区間の番号と時刻の帯を足した動画を作る（カットは適用しない）。
    /// 今の区間表で作った動画があれば、作り直さずに使う。動画はプロジェクトフォルダーに作るため、未保存なら先に保存する。
    /// </summary>
    [RelayCommand]
    private async Task PrepareRequestVideoAsync()
    {
        if (_requestVideos is null || _store is null || _windows is null || Purpose != AiPurpose.CutAnalysis) return;
        await EnsureSegmentTableAsync();
        var project = _main.Project;
        if (project is null || _table is null) return;
        var segments = _table.ToHistory();
        if (AiRequestVideoService.IsCurrent(_requestSession.Video, project, segments))
        {
            RequestVideoMessage = Loc.Get("Ai.Seg.Video.Reused");
            RefreshRequestVideo();
            return;
        }
        if (_main.FFmpeg.FfmpegPath is not { Length: > 0 } ffmpeg)
        {
            _dialogs.ShowError(Loc.Get("Ai.Seg.Video.NoFFmpeg"));
            return;
        }
        if (_main.ProjectPath is null)
        {
            _main.SaveProjectCommand.Execute(null);
            if (_main.ProjectPath is null)
            {
                RequestVideoMessage = Loc.Get("Ai.Seg.Video.NeedSave");
                return;
            }
        }
        // 作り直す場合、前の動画は使わなくなる（確認して削除できるよう残す）。
        if (_requestSession.Video is not null) _requestSession.Invalidate();

        var name = ProjectFolders.DefaultOutputBaseName(_main.VideoPath, _main.ProjectPath);
        var location = CaptionRequestStore.ProjectLocation(_main.ProjectPath);
        CaptionRequestVideo? video = null;
        try
        {
            var completed = await _windows.RunWithProgressAsync(Loc.Get("Ai.Seg.Video.ProgressTitle"), async (progress, ct) =>
            {
                var p = new Progress<VideoExportProgress>(x => progress.Report(new ProgressInfo(x.Ratio,
                    Loc.Format("Ai.Seg.Video.Progress", TimeFormat.Format(x.ProcessedSeconds), TimeFormat.Format(x.TotalSeconds), x.Ratio))));
                video = await _requestVideos.PrepareAsync(ffmpeg, project, segments, name, location, p, ct);
            });
            if (!completed || video is null)
            {
                RequestVideoMessage = Loc.Get("Ai.Seg.Video.Cancelled");
                RefreshRequestVideo();
                return;
            }
        }
        catch (PathStageException ex)
        {
            _dialogs.ShowError(Loc.Get("Ai.Seg.Video.Failed") + Environment.NewLine + ex.Message, LogHint());
            RequestVideoMessage = Loc.Format("Ai.Seg.Video.FailedStage", ex.Stage);
            RefreshRequestVideo();
            return;
        }
        catch (FFmpegException ex)
        {
            _dialogs.ShowError(ex.Message, ex.ErrorDetail + Environment.NewLine + LogHint());
            RequestVideoMessage = Loc.Format("Ai.Seg.Video.FailedStage", "FFmpeg");
            RefreshRequestVideo();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cut request video failed");
            _dialogs.ShowError(Loc.Get("Ai.Seg.Video.Failed"), ex.Message + Environment.NewLine + LogHint());
            RefreshRequestVideo();
            return;
        }
        _requestSession.Video = video;
        RequestVideoMessage = Loc.Get("Ai.Seg.Video.Done") + (video.Notice.Length > 0 ? Environment.NewLine + video.Notice : "");
        RefreshRequestVideo();
    }

    /// <summary>エラー表示に添える、ログの保存先の案内。</summary>
    private static string LogHint()
    {
        var dir = AppPaths.Current.LogDirectory;
        return FileLoggerProvider.LastWriteError is { } err
            ? $"ログを保存できていません（{dir}: {err}）。"
            : $"詳しい経過はログに記録しています: {dir}";
    }

    /// <summary>［フォルダーを開く］: 依頼用動画をエクスプローラーで選んだ状態で開く。</summary>
    [RelayCommand]
    private void OpenRequestVideoFolder()
    {
        if (_requestSession.Video is { } v && File.Exists(v.Path)) _shell.RevealFile(v.Path);
    }

    [RelayCommand]
    private void CopyRequestVideoPath()
    {
        if (_requestSession.Video is { } v && _clipboard.SetText(v.Path)) RequestVideoMessage = Loc.Get("Ai.Seg.Video.PathCopied");
    }

    /// <summary>使えなくなった古い依頼用動画を、1 件ずつ確認して削除する。</summary>
    [RelayCommand]
    private void DeleteRequestOrphans()
    {
        if (_windows is null || _store is null) return;
        foreach (var o in _requestSession.Orphans.ToList())
        {
            if (!File.Exists(o.Path)) { _requestSession.Orphans.Remove(o); continue; }
            if (_windows.ConfirmDeleteRequestVideo(Loc.Get("Ai.Seg.Video.OrphanAsk"), o.Path) != true) continue;
            var r = _store.Delete(o.Path);
            if (r.Success) _requestSession.Orphans.Remove(o);
            else _dialogs.ShowError(Loc.Get("Ai.Seg.Video.DeleteFailed") + Environment.NewLine + o.Path, r.Message);
        }
        RefreshRequestVideo();
    }

    /// <summary>
    /// AI 候補の取り込みの後に、依頼用動画を削除するか確認する（字幕依頼用動画と同じ。既定は［削除する］、
    /// ［削除しない］・閉じるだけでは削除しない）。失敗しても取り込んだ候補はそのまま。結果の文を返す（何もしなければ空）。
    /// </summary>
    private string AskDeleteRequestVideo()
    {
        if (_windows is null || _store is null || _requestSession.Video is not { } v) return "";
        if (!File.Exists(v.Path)) { _requestSession.Video = null; return ""; }
        var answer = _windows.ConfirmDeleteRequestVideo(Loc.Get("Ai.Seg.Video.DeleteAsk"), v.Path);
        if (answer != true) return Loc.Get("Ai.Seg.Video.Kept");
        var d = _store.Delete(v.Path);
        if (d.Success)
        {
            _requestSession.Video = null;
            return Loc.Get("Ai.Seg.Video.Deleted");
        }
        _logger.LogWarning("Cut request video delete failed: {Path}: {Reason}", v.Path, d.Message);
        _dialogs.ShowError(Loc.Get("Ai.Seg.Video.DeleteFailed") + Environment.NewLine + v.Path, d.Message);
        return Loc.Get("Ai.Seg.Video.DeleteFailedShort");
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
        PasteMessage = "";
        ClearResult();
    }

    /// <summary>
    /// ［クリップボードから貼り付け］: 回答欄の内容をクリップボードの文字列に<b>置き換える</b>（追記しない）。
    /// クリップボードが空・読み取れないときは、回答欄を変えず理由を示す。貼り付けただけでは解析も取り込みもしない。
    /// </summary>
    [RelayCommand]
    private void PasteFromClipboard()
    {
        if (!_clipboard.TryGetText(out var text) || string.IsNullOrWhiteSpace(text))
        {
            PasteMessage = "クリップボードに文字列がありません（空、または他のアプリが使用中です）。Gemini の回答をコピーしてから押してください。回答欄はそのままです。";
            return;
        }
        ResponseText = text;
        PasteMessage = $"クリップボードの内容で回答欄を置き換えました（{text.Length:N0} 文字）。続けて［解析］を押してください。";
    }

    /// <summary>
    /// 回答欄が書き換えられたら、前の解析結果は取り込めなくする（貼り付け → 解析 → 確認 → 取り込み の順を保つ）。
    /// </summary>
    partial void OnResponseTextChanged(string value)
    {
        if (!HasResult && CaptionRows.Count == 0 && CutRows.Count == 0 && ReviewRows.Count == 0) return;
        ClearResult();
        ParseMessage = "回答欄が書き換えられたため、前の解析結果は取り込めなくなりました。もう一度［解析］を押してください。";
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
            // コピーせずに解析した場合は、表示中のプロンプトの区間表を使う。
            Segments = CurrentSegments(),
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
                var r = AiResponseParser.ParseCutAnalysis(ResponseText);
                if (!r.Success) { Fail(r.Error); return; }
                methodLabel = r.Extraction?.MethodLabel ?? "";
                warnings.AddRange(r.Warnings);
                if (r.IsSegmentMode)
                {
                    // 区間方式: 番号はプロンプトをコピーした時点の区間表（履歴）で時刻へ戻す。
                    if (_entry.Segments.Count == 0) { Fail(Loc.Get("Ai.Seg.MissingTable")); return; }
                    var rows = AiCutImporter.ValidateSegments(r.Segments, _entry.Segments, project.CutSegments,
                        project.VideoMetadata?.Duration ?? 0, _settings.Current.Ui.DefaultShortenKeepSeconds);
                    SetCutRows(rows);
                    ParseMessage = r.Segments.Count == 0
                        ? "不要区間の候補はありませんでした。"
                        : Loc.Format("Ai.Seg.Parsed", r.Segments.Count, CutRows.Count(c => c.IsValid), CutRows.Count(c => c.IsExcluded));
                }
                else
                {
                    // 時刻方式（古い回答・区間表の無いテンプレート）: 区間表があれば境目へ寄せる。
                    AddCutRows(r.Cuts, project, _entry.Segments);
                    ParseMessage = r.Cuts.Count == 0
                        ? "不要区間の候補はありませんでした。"
                        : $"{r.Cuts.Count} 件の区間候補を認識しました（取り込み可能 {CutRows.Count(c => c.IsValid)} 件）。";
                }
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

    private void AddCutRows(IReadOnlyList<ParsedCut> cuts, ProjectDocument project, IReadOnlyList<AiSegment>? segments = null)
        => SetCutRows(AiCutImporter.Validate(cuts, project.VideoMetadata?.Duration ?? 0, _settings.Current.Ui.DefaultShortenKeepSeconds,
            segments is { Count: > 0 } ? segments : null));

    private void SetCutRows(IReadOnlyList<AiCutRow> rows)
    {
        _pendingCuts = rows.Select(r => new CutRowViewModel(r)).ToList();
        foreach (var r in _pendingCuts) CutRows.Add(r);
    }

    /// <summary>［取り込む］: 取り込んで、この画面は開いたままにする（履歴などを続けて見られる）。</summary>
    [RelayCommand]
    private void Import() => ImportCore();

    /// <summary>［取り込んで閉じる］: 取り込みに成功したときだけ閉じる。対象が 0 件・エラーなら画面を残す。</summary>
    [RelayCommand]
    private void ImportAndClose()
    {
        if (ImportCore()) CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private bool ImportCore()
    {
        var editor = _main.Editor;
        if (editor is null || !HasResult || _entry is null) return false;
        string summary;
        string json;
        if (Purpose == AiPurpose.CaptureCaption)
        {
            var selected = CaptionRows.Where(r => r.Include && r.CanImport).ToList();
            if (selected.Count == 0) { ErrorMessage = "取り込む行がありません。"; return false; }
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
            if (selected.Count == 0) { ErrorMessage = "取り込む候補がありません。"; return false; }
            var candidates = AiCutImporter.ToCandidates(selected, _entry.Id);
            var replacedBefore = editor.Project.CutCandidates.Count(c => c.Source == SegmentSource.Ai && c.Status != CandidateStatus.Accepted);
            if (Purpose == AiPurpose.CutAnalysis)
                editor.ReplaceCandidates(SegmentSource.Ai, candidates, "AI 候補の取り込み");
            else
                editor.AddCandidates(candidates, "AI レビュー候補の取り込み");
            var replaced = Purpose == AiPurpose.CutAnalysis ? replacedBefore : 0;
            summary = $"AI 候補 {candidates.Count} 件を取り込みました（未判断。採用するまで編集に反映されません）。"
                + (replaced > 0 ? $"未採用の AI 候補 {replaced} 件は新しい結果に置き換わりました（採用済みの記録と確定カットは残っています）。" : "");
            json = JsonSerializer.Serialize(candidates, JsonDefaults.Options);
            _main.SelectedTabIndex = MainViewModel.TabCandidates;
        }
        _entry.ImportedAt = DateTimeOffset.Now;
        _entry.ImportSummary = summary;
        _entry.ImportedJson = json;
        editor.UpsertAiHistory(_entry);
        RefreshHistory();
        ParseMessage = summary;
        _main.StatusMessage = summary;
        HasResult = false;
        // 依頼用動画（時刻と区間番号入り）を作っていれば、削除するか確認する（字幕依頼用動画と同じ）。
        if (Purpose == AiPurpose.CutAnalysis)
        {
            var deleted = AskDeleteRequestVideo();
            if (deleted.Length > 0) ParseMessage += Environment.NewLine + deleted;
            RefreshRequestVideo();
        }
        return true;
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
