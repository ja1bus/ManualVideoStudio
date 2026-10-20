using System.IO;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// プロジェクト一覧（v1.7）と、同じ録画から作る 2 つ目以降の編集（新しい編集・複製）。
/// 編集は録画のフォルダーの直下（1 つ目）または edits\「日時_名前」（2 つ目以降）に置き、作った時点で保存する。
/// 一覧の管理ファイル（projects.json）は、保存・録画・新しい編集などのたびに書き換える。
/// </summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private async Task ShowProjectListAsync()
    {
        PauseIfPlaying();
        var action = _windows.ShowProjectList(ProjectPath, HasProject ? VideoPath : null);
        SaveSettingsQuietly();
        await ExecuteProjectListActionAsync(action);
    }

    /// <summary>一覧で選んだ操作を行う。</summary>
    public async Task ExecuteProjectListActionAsync(ProjectListAction? action)
    {
        if (action is null) return;
        switch (action.Kind)
        {
            case ProjectListActionKind.OpenProject: await OpenProjectFileAsync(action.Path!); break;
            case ProjectListActionKind.OpenVideo: await OpenVideoFileAsync(action.Path!); break;
            case ProjectListActionKind.NewEdit: await CreateNewEditAsync(action.Path!); break;
            case ProjectListActionKind.Duplicate: await DuplicateEditAsync(action.Path!, restartFromCuts: false); break;
            case ProjectListActionKind.DuplicateFromCuts: await DuplicateEditAsync(action.Path!, restartFromCuts: true); break;
            case ProjectListActionKind.StartRecording: await StartRecordingAsync(); break;
            case ProjectListActionKind.AppendRecording: await AppendRecordingAsync(action.Path!); break;
            case ProjectListActionKind.OpenVideoDialog: await OpenVideoAsync(); break;
            case ProjectListActionKind.OpenProjectDialog: await OpenProjectAsync(); break;
        }
    }

    /// <summary>開いている動画の録画のフォルダー（保存先の中にある場合）。</summary>
    private string? CurrentRecordingFolder
        => HasProject && ProjectFolders.IsInProjectFolder(VideoPath, Settings.Recording.ResolveOutputDirectory())
            ? Path.GetDirectoryName(Path.GetFullPath(VideoPath))
            : null;

    private bool CanCreateEditForCurrent() => CurrentRecordingFolder is not null;

    /// <summary>メニュー［ファイル］→［この録画で新しい編集を作る］。</summary>
    [RelayCommand(CanExecute = nameof(CanCreateEditForCurrent))]
    private async Task NewEditForCurrentAsync()
    {
        if (CurrentRecordingFolder is { } folder) await CreateNewEditAsync(folder);
    }

    /// <summary>
    /// 録画から新しい編集を作って開く。編集が 1 つも無ければ録画のフォルダーの直下（フォルダー名.mvsproj.json）、
    /// あれば名前を尋ねて edits\「日時_名前」に作る。作った時点で保存する（一覧に出すため）。
    /// </summary>
    public async Task CreateNewEditAsync(string recordingFolder, bool changesConfirmed = false)
    {
        var nl = Environment.NewLine;
        if (!changesConfirmed && !ConfirmDiscardChanges()) return;
        if (!EnsureFFmpeg()) return;
        var video = ProjectFolders.FindVideo(recordingFolder);
        if (video is null)
        {
            _dialogs.ShowError($"録画のフォルダーに動画が見つかりません。{nl}{recordingFolder}");
            return;
        }
        string projectPath;
        string? createdFolder = null;
        if (ProjectFolders.ProjectFilesIn(recordingFolder).Count == 0)
        {
            projectPath = Path.Combine(recordingFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(recordingFolder)) + ProjectService.Extension);
        }
        else
        {
            var name = _windows.PromptText("新しい編集",
                $"同じ録画から新しい編集を作ります（元の動画はコピーしません）。編集の名前を入力してください（空欄なら日時だけの名前）。{nl}{nl}"
                + $"録画: {recordingFolder}{nl}作成先: {Path.Combine(recordingFolder, ProjectFolders.EditsFolderName, "日時_名前")}",
                "", singleLine: true);
            if (name is null) return;
            try
            {
                createdFolder = ProjectFolders.CreateEditFolder(recordingFolder, DateTime.Now, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError($"編集のフォルダーを作れませんでした。{nl}{Path.Combine(recordingFolder, ProjectFolders.EditsFolderName)}", ex.Message);
                return;
            }
            projectPath = Path.Combine(createdFolder, Path.GetFileName(createdFolder) + ProjectService.Extension);
        }

        StatusMessage = "動画情報を取得しています…";
        VideoMetadata meta;
        try
        {
            meta = await _metadata.ProbeAsync(FFmpeg.FfprobePath!, video);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Probe failed: {Path}", video);
            RemoveIfEmpty(createdFolder);
            StatusMessage = "動画を開けませんでした。";
            _dialogs.ShowError(FileReadProblem.Message("動画を開けませんでした。", video), ex.Message);
            return;
        }
        var project = NewProjectDocument(new FileInfo(video), meta);
        project.RecordingSettings = RecordingSettingsOf(recordingFolder);
        try
        {
            _projects.Save(project, projectPath);
        }
        catch (Exception ex)
        {
            RemoveIfEmpty(createdFolder);
            _dialogs.ShowError(ex.Message + nl + projectPath, ex.InnerException?.Message);
            return;
        }
        _logger.LogInformation("Created new edit: {Path}", projectPath);
        AddRecent(projectPath);
        Settings.Ui.LastProjectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        SaveSettingsQuietly();
        SetProject(project, projectPath);
        TouchProjectIndex(projectPath);
        StatusMessage = $"新しい編集を作りました: {projectPath}";
    }

    /// <summary>
    /// 編集を複製して開く（edits\「日時_名前」に作る）。restartFromCuts ならカットだけ引き継ぎ、カット編集の段階から始める。
    /// 元の編集が主画面で開いていて変更がある場合は、先に保存するか確認する。
    /// </summary>
    public async Task DuplicateEditAsync(string sourcePath, bool restartFromCuts)
    {
        var nl = Environment.NewLine;
        if (!ConfirmDiscardChanges()) return;
        var root = Settings.Recording.ResolveOutputDirectory();
        var recordingFolder = ProjectFolders.RecordingFolderOf(sourcePath, root);
        if (recordingFolder is null)
        {
            _dialogs.ShowWarning($"保存先のフォルダーの外にあるプロジェクトは複製できません。{nl}{sourcePath}{nl}{nl}保存先: {root}");
            return;
        }
        ProjectLoadResult loaded;
        try
        {
            loaded = _projects.Load(sourcePath);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message, ex.InnerException?.Message);
            return;
        }
        if (!loaded.VideoFound)
        {
            _dialogs.ShowWarning($"元の動画が見つからないため複製できません。先に編集を開いて動画を指定し直してください。{nl}{sourcePath}");
            return;
        }
        var sourceName = ProjectFolders.DisplayName(ProjectFolders.IsEditFolderProject(loaded.Project.SourceVideo.Path, sourcePath)
            ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!)
            : ProjectFolders.ProjectFileBaseName(sourcePath));
        var what = restartFromCuts
            ? "カットだけ引き継ぎ、カット編集の段階から始める編集を作ります（字幕・図形は引き継ぎません。元の編集はそのまま残ります）。"
            : "カット・字幕・図形・手順書画像をすべて引き継いだ編集を作ります。";
        var name = _windows.PromptText(restartFromCuts ? "カットからやり直す複製" : "編集の複製",
            $"{what}新しい編集の名前を入力してください。{nl}{nl}元の編集: {sourcePath}{nl}作成先: {Path.Combine(recordingFolder, ProjectFolders.EditsFolderName, "日時_名前")}",
            sourceName + (restartFromCuts ? "_カットから" : "_コピー"), singleLine: true);
        if (name is null) return;

        string folder;
        try
        {
            folder = ProjectFolders.CreateEditFolder(recordingFolder, DateTime.Now, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"編集のフォルダーを作れませんでした。{nl}{Path.Combine(recordingFolder, ProjectFolders.EditsFolderName)}", ex.Message);
            return;
        }
        var path = Path.Combine(folder, Path.GetFileName(folder) + ProjectService.Extension);
        var copy = ProjectCatalog.PrepareCopy(loaded.Project, restartFromCuts, Settings.DefaultExport);
        try
        {
            _projects.Save(copy, path);
        }
        catch (Exception ex)
        {
            RemoveIfEmpty(folder);
            _dialogs.ShowError(ex.Message + nl + path, ex.InnerException?.Message);
            return;
        }
        _logger.LogInformation("Duplicated edit: {Source} -> {Path} (restart from cuts: {Restart})", sourcePath, path, restartFromCuts);
        TouchProjectIndex(path);
        await OpenProjectFileAsync(path, changesConfirmed: true);
        StatusMessage = $"編集を複製しました: {path}";
    }

    /// <summary>新しいプロジェクト（動画を開いたとき・新しい編集）。</summary>
    private ProjectDocument NewProjectDocument(FileInfo video, VideoMetadata meta) => new()
    {
        AppVersion = ProjectService.AppVersion,
        SourceVideo = new SourceVideo { Path = video.FullName, FileSize = video.Length, LastWriteTime = video.LastWriteTime },
        VideoMetadata = meta,
        ExportSettings = JsonDefaults.Clone(Settings.DefaultExport),
        AppSettingsReference = new AppSettingsReference
        {
            FfmpegPath = FFmpeg.FfmpegPath ?? "",
            FfmpegVersion = FFmpeg.Capabilities?.FfmpegVersion ?? "",
            AnalysisPreset = Settings.Analysis.Preset.ToString(),
        },
    };

    /// <summary>同じ録画のほかの編集にある録画の記録（参考情報）を引き継ぐ。無ければ null。</summary>
    private RecordingInfo? RecordingSettingsOf(string recordingFolder)
    {
        foreach (var p in ProjectFolders.ProjectFilesIn(recordingFolder))
        {
            try
            {
                if (_projects.Load(p).Project.RecordingSettings is { } info) return info;
            }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    /// <summary>
    /// 保存先の中の録画のフォルダーにある動画を開くとき、そのフォルダーに編集があれば、新しい編集を作るか最後の編集を開くかを尋ねる。
    /// 続けて動画を新しいプロジェクト（未保存）として開くなら true。
    /// </summary>
    private async Task<bool> AskExistingEditsAsync(string videoPath)
    {
        var root = Settings.Recording.ResolveOutputDirectory();
        if (!ProjectFolders.IsInProjectFolder(videoPath, root)) return true;
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var edits = ProjectFolders.ProjectFilesIn(folder);
        if (edits.Count == 0) return true;
        var latest = edits.OrderByDescending(File.GetLastWriteTime).First();
        var nl = Environment.NewLine;
        var r = _dialogs.ConfirmYesNoCancel(
            $"この動画には編集が {edits.Count} 件あります。{nl}{folder}{nl}{nl}"
            + $"［はい］新しい編集を作る（{ProjectFolders.EditsFolderName} フォルダーに作ります）{nl}"
            + $"［いいえ］最後に更新した編集を開く（{Path.GetFileName(latest)}）{nl}"
            + "［キャンセル］開かない",
            "編集があります");
        switch (r)
        {
            case true: await CreateNewEditAsync(folder, changesConfirmed: true); break;
            case false: await OpenProjectFileAsync(latest, changesConfirmed: true); break;
            default: StatusMessage = "動画を開くのを取り消しました。"; break;
        }
        return false;
    }

    /// <summary>一覧の管理ファイルに、path を含む録画のフォルダーの今の状態を反映する（保存先の外なら何もしない）。</summary>
    private void TouchProjectIndex(string path)
    {
        var root = Settings.Recording.ResolveOutputDirectory();
        if (!ProjectFolders.IsUnder(path, root)) return;
        try
        {
            new ProjectIndexStore(root, _logger).Touch(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Project index update failed: {Path}", path);
        }
    }

    private void RemoveIfEmpty(string? folder)
    {
        if (folder is null) return;
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Failed to remove {Folder}", folder); }
    }

    /// <summary>ウィンドウのタイトルに出す編集の名前（edits\ の編集は「録画名 / 編集名」）。</summary>
    private string ProjectTitleName
    {
        get
        {
            if (ProjectPath is null) return Path.GetFileName(VideoPath) + "（未保存）";
            if (!ProjectFolders.IsEditFolderProject(VideoPath, ProjectPath)) return Path.GetFileName(ProjectPath);
            var recording = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(VideoPath))!);
            var edit = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!);
            return $"{ProjectFolders.DisplayName(recording)} / {ProjectFolders.DisplayName(edit)}";
        }
    }
}
