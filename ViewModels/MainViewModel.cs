using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Analysis;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

public sealed record VideoInfoRow(string Name, string Value);

/// <summary>
/// 主画面の ViewModel。領域ごとに partial ファイルへ分割している:
/// MainViewModel.cs（プロジェクト・ファイル操作・Undo）/ .Player.cs（再生・シーク）/ .Cuts.cs（手動カット・候補・解析）/
/// .Subtitles.cs（編集段階・手動字幕）/ .Shapes.cs（動画に重ねる図形。v1.6）/ .Captures.cs（手順書画像）/ .Export.cs（動画・GIF・画像の書き出し）/ .Recording.cs（画面録画）。
/// 編集はすべて ProjectEditor（Core）を通し、ffmpeg コマンドは Core のサービスが生成する。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IProjectService _projects;
    private readonly IMediaMetadataService _metadata;
    private readonly IFrameExtractionService _frames;
    private readonly IVideoExportService _videoExport;
    private readonly IGifExportService _gifExport;
    private readonly ImageExportService _imageExport;
    private readonly ILocalAnalysisService _analysis;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly IShellService _shell;
    private readonly ILogger<MainViewModel> _logger;
    /// <summary>書き出し前の字幕フォントの確認（v1.5。テストでは null）。</summary>
    private readonly SubtitleCheckService? _subtitleCheck;
    /// <summary>字幕依頼（Gemini）の部品（v1.12。テストでは null で、その場合は［Geminiで字幕を作る］を使えない）。</summary>
    private readonly GeminiCaptionServices? _geminiCaption;

    public MainViewModel(ISettingsService settings, IProjectService projects, IMediaMetadataService metadata,
        IFrameExtractionService frames, IVideoExportService videoExport, IGifExportService gifExport, ImageExportService imageExport,
        ILocalAnalysisService analysis, IDialogService dialogs, IWindowService windows, IShellService shell,
        FFmpegStatus ffmpeg, ILogger<MainViewModel> logger, SubtitleCheckService? subtitleCheck = null,
        CaptionRequestService? captionRequests = null, CaptionRequestStore? captionStore = null,
        Core.Ai.IPromptTemplateService? promptTemplates = null, IClipboardService? clipboard = null)
    {
        if (captionRequests is not null && captionStore is not null && promptTemplates is not null && clipboard is not null)
            _geminiCaption = new GeminiCaptionServices(captionRequests, captionStore, promptTemplates, clipboard, shell, dialogs, windows, ffmpeg, settings, logger);
        _subtitleCheck = subtitleCheck;
        _settings = settings;
        _projects = projects;
        _metadata = metadata;
        _frames = frames;
        _videoExport = videoExport;
        _gifExport = gifExport;
        _imageExport = imageExport;
        _analysis = analysis;
        _dialogs = dialogs;
        _windows = windows;
        _shell = shell;
        _logger = logger;
        FFmpeg = ffmpeg;
        _shortenKeepText = settings.Current.Ui.DefaultShortenKeepSeconds.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
        // 最初は動画編集（カット区間の一覧）。画像作成（画像の一覧）は全機能モードで［画像作成］を押したときだけ開く（v1.13）。
        _selectedTabIndex = TabCuts;
    }

    public FFmpegStatus FFmpeg { get; }
    public ProjectEditor? Editor { get; private set; }
    public AppSettings Settings => _settings.Current;

    public ObservableCollection<CapturePointItem> Captures { get; } = [];
    public ObservableCollection<CutSegmentItem> CutItems { get; } = [];
    public ObservableCollection<CandidateItem> CandidateItems { get; } = [];
    public ObservableCollection<VideoInfoRow> VideoInfo { get; } = [];

    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private string _statusMessage = "動画ファイル（MP4 など）を開くか、この画面へドラッグ＆ドロップしてください。";
    /// <summary>タイムライン再描画のきっかけ（編集のたびに増える）。</summary>
    [ObservableProperty] private int _timelineRevision;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _showDismissedCandidates;

    public bool HasProject => Editor is not null;

    /// <summary>簡易モード（v1.11）: 手順書画像・手順書用の書き出し・AI 連携を隠し、コマンドも止める。</summary>
    public bool IsSimpleMode => Settings.IsSimpleMode;
    public bool IsFullMode => !IsSimpleMode;
    /// <summary>全機能モードでだけ使えるコマンド（プロジェクトが必要なもの）の CanExecute。</summary>
    public bool CanUseFullFeatures => HasProject && IsFullMode;
    /// <summary>外部ツール向けの文書作成用書き出しを設定で有効にしているか（v1.14。簡易モードでも値は保持する）。</summary>
    public bool IsExternalDocumentEnabled => Settings.EnableExternalDocumentExport;
    /// <summary>文書作成用の入口（メニュー・章 / 操作文 / 補足の入力欄など）を出す条件: 全機能モード かつ 設定で有効。</summary>
    public bool ShowExternalDocument => IsFullMode && IsExternalDocumentEnabled;
    /// <summary>文書作成用の書き出しを実行できる条件（既存の条件 AND 設定で有効）。</summary>
    public bool CanExportExternalDocument => HasProject && ShowExternalDocument;
    /// <summary>ステータスバーに出すモード名（全機能モードでは出さない）。</summary>
    public string UiModeText => IsSimpleMode ? "簡易モード" : "";
    public ProjectDocument? Project => Editor?.Project;
    public VideoMetadata? Metadata => Editor?.Project.VideoMetadata;
    public string VideoPath => Editor?.Project.SourceVideo.Path ?? "";
    public double Duration => Metadata?.Duration ?? 0;
    public bool IsDirty => Editor?.IsDirty ?? false;

    public string WindowTitle
    {
        get
        {
            const string app = "Manual Video Studio";
            if (Editor is null) return app;
            return $"{(IsDirty ? "* " : "")}{ProjectTitleName} - {app}";
        }
    }

    // タイムライン用（Undo でリストが差し替わるため毎回 Project から取る）
    public IReadOnlyList<CutSegment> TimelineSegments => Project?.CutSegments ?? [];
    public IReadOnlyList<CutCandidate> TimelineCandidates => Project?.CutCandidates ?? [];
    /// <summary>簡易モードでは画像ポイントの印を出さない（データはプロジェクトに残る）。</summary>
    public IReadOnlyList<CapturePoint> TimelineCaptures => IsSimpleMode ? [] : Project?.CapturePoints ?? [];
    public IReadOnlyList<double> TimelineScenes => Project?.SceneChanges ?? [];

    public string OutputDurationText
    {
        get
        {
            if (Project is null) return "";
            var keep = EditTimelineCalculator.ComputeKeepRanges(Duration, Project.CutSegments);
            var output = EditTimelineCalculator.OutputDuration(keep);
            // タイトルがあるときは、本編の長さとタイトル付きの完成動画の長さを分けて示す（編集の時間軸は本編だけ）。
            if (Project.Title.HasTitle)
            {
                var total = Project.Title.TotalDuration(output);
                return $"本編 {TimeFormat.Format(output)}（元 {TimeFormat.Format(Duration)}、{Duration - output:0.0} 秒短縮）／完成動画 {TimeFormat.Format(total)}（タイトル {VideoTitle.NormalizeSeconds(Project.Title.Seconds)} 秒を含む）";
            }
            return $"出力予定 {TimeFormat.Format(output)}（元 {TimeFormat.Format(Duration)}、{Duration - output:0.0} 秒短縮）";
        }
    }

    public string UndoText => Editor?.History.UndoLabel is { } l ? $"元に戻す: {l}" : "元に戻す";
    public string RedoText => Editor?.History.RedoLabel is { } l ? $"やり直し: {l}" : "やり直し";
    public string ImageFormatText => Project?.ExportSettings.ImageFormat == ImageFormat.Jpeg ? "jpg" : "png";
    public int PendingCandidateCount => Project?.CutCandidates.Count(c => c.Status == CandidateStatus.Pending) ?? 0;

    // ---------------- 起動・ファイル ----------------

    public async Task InitializeAsync(string? startupPath)
    {
        var refresh = FFmpeg.RefreshAsync();
        // 起動時に読めなかった設定ファイル（暗号化・破損）を知らせる（v1.4.2）。
        foreach (var message in FileLoadProblems.TakeAll()) _dialogs.ShowWarning(message);
        // 起動引数が無ければプロジェクト一覧を出す（v1.7）。FFmpeg の確認はその間も裏で進め、選んだ操作は確認の後で行う。
        ProjectListAction? action = null;
        if (string.IsNullOrEmpty(startupPath) && Settings.Ui.ShowProjectListOnStartup)
        {
            action = _windows.ShowProjectList(null, null);
            SaveSettingsQuietly();
        }
        await refresh;
        if (!FFmpeg.IsAvailable)
            StatusMessage = $"{FFmpeg.StatusText}。メニュー［ツール］→［設定］で ffmpeg.exe を指定してください。";
        if (!string.IsNullOrEmpty(startupPath)) await OpenPathAsync(startupPath);
        else await ExecuteProjectListActionAsync(action);
        if (!HasProject) RefreshRecentProjects();
    }

    /// <summary>ドラッグ＆ドロップ・コマンドライン引数。拡張子でプロジェクトか動画かを判断する。</summary>
    public async Task OpenPathAsync(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) await OpenProjectFileAsync(path);
        else await OpenVideoFileAsync(path);
    }

    public const string VideoFilter = "動画ファイル (*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv)|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv|すべてのファイル (*.*)|*.*";

    [RelayCommand]
    private async Task OpenVideoAsync()
    {
        var path = _dialogs.OpenFile(VideoFilter, Settings.Ui.LastVideoDirectory, "動画を開く");
        if (path is not null) await OpenVideoFileAsync(path);
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var path = _dialogs.OpenFile(ProjectService.FileFilter, Settings.Ui.LastProjectDirectory, "プロジェクトを開く");
        if (path is not null) await OpenProjectFileAsync(path);
    }

    public async Task OpenVideoFileAsync(string path)
    {
        if (!File.Exists(path)) { _dialogs.ShowError($"ファイルが見つかりません。{Environment.NewLine}{path}"); return; }
        if (!ConfirmDiscardChanges()) return;
        if (!EnsureFFmpeg()) return;
        var moved = await MoveIntoProjectFolderAsync(path);
        if (moved is null) return;
        // 編集がある録画の動画なら、新しい編集を作るか最後の編集を開くかを選ばせる（v1.7）。
        if (string.Equals(moved, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && !await AskExistingEditsAsync(moved)) return;
        path = moved;
        StatusMessage = "動画情報を取得しています…";
        VideoMetadata meta;
        try
        {
            meta = await _metadata.ProbeAsync(FFmpeg.FfprobePath!, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Probe failed: {Path}", path);
            StatusMessage = "動画を開けませんでした。";
            _dialogs.ShowError(FileReadProblem.Message("動画を開けませんでした。", path), ex.Message);
            return;
        }
        var fi = new FileInfo(path);
        var project = NewProjectDocument(fi, meta);
        Settings.Ui.LastVideoDirectory = fi.DirectoryName ?? "";
        SaveSettingsQuietly();
        SetProject(project, null);
        StatusMessage = $"動画を開きました: {fi.Name}（{meta.Resolution} / {TimeFormat.Format(meta.Duration)}）";
    }

    /// <summary>
    /// 動画がプロジェクトフォルダー（保存先ルートの直下のフォルダー）に無ければ、プロジェクト名を尋ねて
    /// 「日時_名前」のフォルダーを作り、動画をそこへ移動する（v1.4）。移動後のパス、取り消し・失敗なら null。
    /// </summary>
    private async Task<string?> MoveIntoProjectFolderAsync(string path)
    {
        var root = Settings.Recording.ResolveOutputDirectory();
        if (ProjectFolders.IsInProjectFolder(path, root)) return path;
        var nl = Environment.NewLine;
        var name = _windows.PromptText("プロジェクトの作成",
            $"動画をプロジェクトフォルダーへ移動してから開きます。プロジェクト名を入力してください（空欄なら日時だけのフォルダー名）。{nl}{nl}"
            + $"動画: {path}{nl}移動先: {Path.Combine(root, "日時_プロジェクト名")}",
            Path.GetFileNameWithoutExtension(path), singleLine: true);
        if (name is null)
        {
            StatusMessage = "動画を開くのを取り消しました。";
            return null;
        }
        // 開いている動画そのものを移動する場合は、再生を止めてファイルを離す。
        if (string.Equals(Path.GetFullPath(path), VideoPath, StringComparison.OrdinalIgnoreCase)) _player?.Close();
        StatusMessage = "動画をプロジェクトフォルダーへ移動しています…";
        try
        {
            var dest = await Task.Run(() => ProjectFolders.MoveIntoNewFolder(path, root, DateTime.Now, name));
            _logger.LogInformation("Moved video into project folder: {Source} -> {Dest}", path, dest);
            TouchProjectIndex(dest);
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to move video: {Path}", path);
            StatusMessage = "動画を移動できませんでした。";
            _dialogs.ShowError($"動画をプロジェクトフォルダーへ移動できませんでした。{nl}{path}", ex.Message);
            return null;
        }
    }

    /// <summary>プロジェクトファイルを開く。changesConfirmed なら、開いているプロジェクトの保存の確認を済ませている（v1.7）。</summary>
    public async Task OpenProjectFileAsync(string path, bool changesConfirmed = false)
    {
        if (!changesConfirmed && !ConfirmDiscardChanges()) return;
        ProjectLoadResult result;
        try
        {
            result = _projects.Load(path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, ex.InnerException?.Message);
            return;
        }
        var project = result.Project;
        bool relinked = false;
        if (!result.VideoFound)
        {
            if (!_dialogs.Confirm($"元動画が見つかりません。{Environment.NewLine}{project.SourceVideo.Path}{Environment.NewLine}{Environment.NewLine}動画ファイルを指定し直しますか？", "動画の再指定"))
                return;
            var newPath = _dialogs.OpenFile(VideoFilter, Path.GetDirectoryName(path), "動画を再指定");
            if (newPath is null) return;
            var warning = ProjectService.Relink(project, newPath);
            if (warning is not null) _dialogs.ShowWarning(warning);
            relinked = true;
        }
        else if (result.Warning is not null)
        {
            _dialogs.ShowWarning(result.Warning);
        }

        if ((project.VideoMetadata is null || relinked) && FFmpeg.IsAvailable)
            await RefreshMetadataAsync(project);

        Settings.Ui.LastProjectDirectory = Path.GetDirectoryName(path) ?? "";
        AddRecent(path);
        SetProject(project, path);
        if (relinked) Editor!.MarkDirty();
        StatusMessage = $"プロジェクトを開きました: {Path.GetFileName(path)}";
    }

    private async Task RefreshMetadataAsync(ProjectDocument project)
    {
        try
        {
            var meta = await _metadata.ProbeAsync(FFmpeg.FfprobePath!, project.SourceVideo.Path);
            if (project.VideoMetadata is { } old && Math.Abs(old.Duration - meta.Duration) > 0.5)
                _dialogs.ShowWarning($"動画の長さが保存時と異なります（保存時 {TimeFormat.Format(old.Duration)} / 現在 {TimeFormat.Format(meta.Duration)}）。登録済みの時刻を確認してください。");
            project.VideoMetadata = meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadata refresh failed");
            _dialogs.ShowWarning(FileReadProblem.Message("動画の情報を読み込めませんでした（再生・書き出しができない可能性があります）。", project.SourceVideo.Path)
                + Environment.NewLine + Environment.NewLine + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task RelinkVideoAsync()
    {
        var newPath = _dialogs.OpenFile(VideoFilter, Path.GetDirectoryName(VideoPath), "動画を再指定");
        if (newPath is null || Project is null) return;
        var warning = ProjectService.Relink(Project, newPath);
        if (warning is not null) _dialogs.ShowWarning(warning);
        if (FFmpeg.IsAvailable) await RefreshMetadataAsync(Project);
        _player?.Open(Project.SourceVideo.Path);
        Editor!.MarkDirty();
        RefreshAll();
    }

    private void SetProject(ProjectDocument project, string? path)
    {
        // 開いていた編集の字幕依頼用動画と、回答の取り込み待ちは無効にする（ファイルは確認なしには消さない。v1.12）。
        CaptionRequest.Invalidate();
        _importedSubtitleSignature = null;
        IsShortenDetailOpen = false;
        if (Editor is not null)
        {
            Editor.Changed -= OnEditorChanged;
            Editor.History.Changed -= OnHistoryChanged;
        }
        Editor = new ProjectEditor(project) { SubtitleDefaults = Settings.Subtitle.Clone() };
        _annotatedImagesSinceSave = false;
        Editor.Changed += OnEditorChanged;
        Editor.History.Changed += OnHistoryChanged;
        ProjectPath = path;
        Editor.MarkSaved();
        Captures.Clear();
        MarkIn = null;
        MarkOut = null;
        CurrentTime = 0;
        PlayerError = "";
        FallbackFrame = null;
        _analysisState = Core.Guidance.WorkflowGuide.InferAnalysisState(project.SceneChanges.Count,
            project.CutCandidates.Count(c => c.Source == SegmentSource.LocalAnalysis));
        RefreshAll();
        if (File.Exists(project.SourceVideo.Path)) _player?.Open(project.SourceVideo.Path);
        OnPropertyChanged(nameof(HasProject));
        NotifyCommands();
        NormalizePaneForPhase(force: true);
        NotifyGuidance();
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveProject() => Save(saveAs: false);

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void SaveProjectAs() => Save(saveAs: true);

    /// <summary>保存する。targetPath を指定すると（未保存の編集を尋ねずに保存するとき）保存先を尋ねない。</summary>
    private bool Save(bool saveAs, string? targetPath = null)
    {
        if (Editor is null) return false;
        if (!FlushSubtitleDraft()) return false; // 入力中の字幕を先に反映する（v1.13）
        var path = targetPath ?? ProjectPath;
        if (targetPath is null && (saveAs || path is null))
        {
            var dir = path is not null ? Path.GetDirectoryName(path) : Path.GetDirectoryName(VideoPath);
            // プロジェクトフォルダーにある動画なら、フォルダー名（日時_名前）をプロジェクトファイル名にする。
            var root = Settings.Recording.ResolveOutputDirectory();
            var baseName = ProjectFolders.IsInProjectFolder(VideoPath, root)
                ? Path.GetFileName(Path.GetDirectoryName(VideoPath)!)
                : Path.GetFileNameWithoutExtension(VideoPath);
            var name = baseName + ProjectService.Extension;
            path = _dialogs.SaveFile(ProjectService.FileFilter, name, dir, "プロジェクトを保存");
            if (path is null) return false;
            path = ProjectService.NormalizePath(path);
        }
        if (path is null) return false;
        try
        {
            Editor.Project.AppSettingsReference ??= new AppSettingsReference();
            Editor.Project.AppSettingsReference.FfmpegPath = FFmpeg.FfmpegPath ?? "";
            Editor.Project.AppSettingsReference.FfmpegVersion = FFmpeg.Capabilities?.FfmpegVersion ?? "";
            _projects.Save(Editor.Project, path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, ex.InnerException?.Message);
            return false;
        }
        ProjectPath = path;
        Editor.MarkSaved();
        _annotatedImagesSinceSave = false;
        AddRecent(path);
        Settings.Ui.LastProjectDirectory = Path.GetDirectoryName(path) ?? "";
        SaveSettingsQuietly();
        TouchProjectIndex(path);
        StatusMessage = $"保存しました: {path}";
        OnPropertyChanged(nameof(WindowTitle));
        return true;
    }

    /// <summary>未保存の変更がある場合に保存を確認する。続行してよければ true。</summary>
    public bool ConfirmDiscardChanges()
    {
        // 入力中の字幕は先に反映する。反映できない入力は、破棄してよいか確かめる（黙って捨てない。v1.13）。
        if (HasSubtitleDraft && !FlushSubtitleDraft()
            && !_dialogs.Confirm(Loc.Format("SubEdit.DiscardConfirm", SubtitleDraftError), Loc.Get("SubEdit.DiscardTitle")))
            return false;
        if (Editor is null || !Editor.IsDirty) return true;
        var message = "変更が保存されていません。保存しますか？";
        if (_annotatedImagesSinceSave)
            message += Environment.NewLine + Environment.NewLine + "注釈を上書き保存した画像があります。プロジェクトを保存しないと、次に開いたときの注釈はプロジェクトに記録済みの以前の内容になり、画像ファイルと食い違います（［画像を書き出し］で以前の注釈の画像に作り直されます）。";
        var r = _dialogs.ConfirmYesNoCancel(message, "Manual Video Studio");
        return r switch
        {
            true => Save(saveAs: false),
            false => true,
            null => false,
        };
    }

    private void AddRecent(string path)
    {
        var list = Settings.Ui.RecentProjects;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
    }

    public void SaveSettingsQuietly()
    {
        try { _settings.Save(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Settings save failed"); }
    }

    // ---------------- Undo / Redo ----------------

    private bool CanUndo() => Editor?.History.CanUndo ?? false;
    private bool CanRedo() => Editor?.History.CanRedo ?? false;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var label = Editor?.History.UndoLabel;
        Editor?.Undo();
        StatusMessage = $"元に戻しました: {label}";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var label = Editor?.History.RedoLabel;
        Editor?.Redo();
        StatusMessage = $"やり直しました: {label}";
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        // カットのやり直しなどで、準備済みの字幕依頼用動画が今の編集と合わなくなったら無効にする（v1.12）。
        CaptionRequest.InvalidateIfStale(Project, IsSubtitlePhase);
        RefreshAll();
    }

    /// <summary>プロジェクトの内容を画面の一覧・タイムラインへ反映する。</summary>
    private void RefreshAll()
    {
        var p = Project;
        var format = p?.ExportSettings.ImageFormat ?? ImageFormat.Png;
        CollectionSync.Sync(Captures, p?.CapturePoints ?? [], i => i.Id, m => m.Id,
            m =>
            {
                var item = new CapturePointItem(m, format);
                _ = LoadThumbnailAsync(item);
                return item;
            },
            (item, m) =>
            {
                item.Update(m, format);
                if (item.ThumbnailTime is not { } t || Math.Abs(t - m.SelectedFrameTime) >= 0.001) _ = LoadThumbnailAsync(item);
            });

        CutItems.Clear();
        foreach (var c in p?.CutSegments ?? []) CutItems.Add(new CutSegmentItem(c));
        RefreshSubtitles();
        RefreshShapes();
        CandidateItems.Clear();
        foreach (var c in (p?.CutCandidates ?? []).Where(c => ShowDismissedCandidates || c.Status == CandidateStatus.Pending))
            CandidateItems.Add(new CandidateItem(c));

        VideoInfo.Clear();
        if (p?.VideoMetadata is { } m)
        {
            VideoInfo.Add(new("ファイル", p.SourceVideo.Path));
            VideoInfo.Add(new("コンテナ", m.Container));
            VideoInfo.Add(new("映像コーデック", m.VideoCodec));
            VideoInfo.Add(new("音声コーデック", m.HasAudio ? m.AudioCodec : "（音声なし）"));
            VideoInfo.Add(new("解像度", m.Resolution));
            VideoInfo.Add(new("FPS", m.Fps.ToString("0.###")));
            VideoInfo.Add(new("長さ", TimeFormat.Format(m.Duration)));
            VideoInfo.Add(new("ピクセル形式", m.PixelFormat));
            VideoInfo.Add(new("映像ビットレート", Kbps(m.VideoBitrate)));
            VideoInfo.Add(new("音声ビットレート", m.HasAudio ? Kbps(m.AudioBitrate) : "-"));
            VideoInfo.Add(new("ファイルサイズ", $"{m.FileSize / 1024.0 / 1024.0:0.0} MB（{m.FileSize:N0} バイト）"));
        }

        TimelineRevision++;
        OnPropertyChanged(nameof(TimelineSegments));
        OnPropertyChanged(nameof(TimelineCandidates));
        OnPropertyChanged(nameof(TimelineCaptures));
        OnPropertyChanged(nameof(TimelineScenes));
        OnPropertyChanged(nameof(TimelineSubtitles));
        OnPropertyChanged(nameof(OutputDurationText));
        NotifyTitle();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ImageFormatText));
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(VideoPath));
        OnPropertyChanged(nameof(PendingCandidateCount));
        OnHistoryChanged(null, EventArgs.Empty);
        NotifyGuidance();
    }

    private static string Kbps(long bps) => bps > 0 ? $"{bps / 1000.0:0} kbps" : "不明";

    partial void OnShowDismissedCandidatesChanged(bool value) => RefreshAll();

    /// <summary>簡易モードでは隠した「画像」タブを選ばせない（v1.11。隠した TabItem が選ばれると中身が見えてしまう）。</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == TabCaptures && IsSimpleMode) SelectedTabIndex = TabCuts;
        NotifyGuidance();
    }

    /// <summary>設定で画面のモードを変えたあとに、表示とコマンドを切り替える（v1.11）。</summary>
    private void ApplyUiMode()
    {
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsFullMode));
        OnPropertyChanged(nameof(IsExternalDocumentEnabled));
        OnPropertyChanged(nameof(ShowExternalDocument));
        OnPropertyChanged(nameof(CanExportExternalDocument));
        OnPropertyChanged(nameof(UiModeText));
        OnPropertyChanged(nameof(TimelineCaptures));
        TimelineRevision++;
        NotifyCommands();
        NotifyGuidance();
        if (!IsSimpleMode) return;
        if (SelectedTabIndex == TabCaptures) SelectedTabIndex = TabCuts;
        _windows.CloseAiAssist();
    }
    partial void OnProjectPathChanged(string? value) => OnPropertyChanged(nameof(WindowTitle));

    private void NotifyCommands()
    {
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
        RelinkVideoCommand.NotifyCanExecuteChanged();
        AddCaptureCommand.NotifyCanExecuteChanged();
        ExportVideoCommand.NotifyCanExecuteChanged();
        EditTitleCommand.NotifyCanExecuteChanged();
        ExportImagesCommand.NotifyCanExecuteChanged();
        ExportTejunshoCommand.NotifyCanExecuteChanged();
        RunAnalysisCommand.NotifyCanExecuteChanged();
        ShowAiAssistCommand.NotifyCanExecuteChanged();
        ShowPromptSettingsCommand.NotifyCanExecuteChanged();
        SetMarkInCommand.NotifyCanExecuteChanged();
        SetMarkOutCommand.NotifyCanExecuteChanged();
        AddRemoveCutCommand.NotifyCanExecuteChanged();
        AddShortenCutCommand.NotifyCanExecuteChanged();
        ExportGifCommand.NotifyCanExecuteChanged();
        ConfirmCutsCommand.NotifyCanExecuteChanged();
        ReturnToCutEditingCommand.NotifyCanExecuteChanged();
        AddSubtitleCommand.NotifyCanExecuteChanged();
        NewEditForCurrentCommand.NotifyCanExecuteChanged();
        OpenGeminiCaptionCommand.NotifyCanExecuteChanged();
        RestartFromCutsKeepingCurrentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCutPhase));
        OnPropertyChanged(nameof(IsSubtitlePhase));
    }

    /// <summary>ffmpeg / ffprobe が使えない場合に案内する。</summary>
    private bool EnsureFFmpeg()
    {
        if (FFmpeg.IsAvailable && FFmpeg.FfmpegPath is not null && FFmpeg.FfprobePath is not null) return true;
        if (FFmpeg.IsDetecting)
        {
            // 起動直後の確認中（v1.6.1）。見つからないと案内しない。
            _dialogs.ShowInfo("FFmpeg を確認しています。数秒待ってから、もう一度操作してください。", "FFmpeg");
            return false;
        }
        if (_dialogs.Confirm($"{FFmpeg.StatusText}。{Environment.NewLine}{FFmpeg.DetailText}{Environment.NewLine}{Environment.NewLine}設定画面を開きますか？", "FFmpeg が必要です"))
            _ = OpenSettingsAsync(SettingsTab.FFmpeg);
        return false;
    }

    // ---------------- 画面 ----------------

    [RelayCommand]
    private Task ShowSettingsAsync() => OpenSettingsAsync(SettingsTab.FFmpeg);

    [RelayCommand]
    private Task ShowAnalysisSettingsAsync() => OpenSettingsAsync(SettingsTab.Analysis);

    [RelayCommand(CanExecute = nameof(IsFullMode))]
    private Task ShowPromptSettingsAsync() => OpenSettingsAsync(SettingsTab.Prompts);

    private async Task OpenSettingsAsync(SettingsTab tab)
    {
        PauseIfPlaying();
        var mode = Settings.UiMode;
        var external = Settings.EnableExternalDocumentExport;
        var r = await _windows.ShowSettingsAsync(tab, SubtitleSampleForSettings());
        if (!r.Saved) return;
        // 画面のモードだけでなく、外部文書連携の有効・無効だけが変わった場合も、表示と実行条件を再評価する（v1.14）。
        if (Settings.UiMode != mode || Settings.EnableExternalDocumentExport != external) ApplyUiMode();
        if (Editor is not null) Editor.SubtitleDefaults = Settings.Subtitle.Clone();
        ShortenKeepText = Settings.Ui.DefaultShortenKeepSeconds.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
        if (r.ApplyOutputToProject && Editor is not null)
        {
            // 出力先フォルダーはプロジェクトごとの記録を優先し、形式・品質だけを反映する。
            var es = Editor.Project.ExportSettings;
            var d = Settings.DefaultExport;
            es.ImageFormat = d.ImageFormat;
            es.JpegQuality = d.JpegQuality;
            es.WriteMetadataJson = d.WriteMetadataJson;
            es.VideoCrf = d.VideoCrf;
            es.VideoPreset = d.VideoPreset;
            es.AudioBitrateKbps = d.AudioBitrateKbps;
            es.CutTransition = d.CutTransition;
            if (string.IsNullOrEmpty(es.VideoOutputDirectory)) es.VideoOutputDirectory = d.VideoOutputDirectory;
            if (string.IsNullOrEmpty(es.ImageOutputDirectory)) es.ImageOutputDirectory = d.ImageOutputDirectory;
            Editor.MarkDirty();
        }
        foreach (var c in Captures) c.ThumbnailTime = null;
        RefreshAll();
    }

    [RelayCommand(CanExecute = nameof(CanUseFullFeatures))]
    private void ShowAiAssist() => _windows.ShowAiAssist();

    /// <summary>起動中のアプリのバージョン（主画面の右下に表示）。</summary>
    public string AppVersionText => "v" + ProjectService.AppVersion;

    /// <summary>取扱説明書（HTML）を既定のブラウザーで開く。</summary>
    [RelayCommand]
    private void OpenManual()
    {
        try
        {
            _shell.OpenUrl(HelpManual.Extract(new AppPaths().LocalDirectory));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open manual");
            _dialogs.ShowError("取扱説明書を開けませんでした。", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(new AppPaths().LogDirectory);
}
