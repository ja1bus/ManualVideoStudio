using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>一覧で選んだ後に主画面で行う操作（一覧の画面を閉じてから行う）。</summary>
public enum ProjectListActionKind
{
    /// <summary>編集（プロジェクトファイル）を開く。</summary>
    OpenProject,
    /// <summary>編集の無い録画の動画を開く。</summary>
    OpenVideo,
    /// <summary>録画から新しい編集を作る。Path は録画のフォルダー。</summary>
    NewEdit,
    /// <summary>編集を複製する。Path は元のプロジェクトファイル。</summary>
    Duplicate,
    /// <summary>カットだけ引き継いで複製する。Path は元のプロジェクトファイル。</summary>
    DuplicateFromCuts,
    StartRecording,
    OpenVideoDialog,
    OpenProjectDialog,
}

public sealed record ProjectListAction(ProjectListActionKind Kind, string? Path = null);

/// <summary>一覧の 1 行（録画、またはその下の編集）。</summary>
public sealed class ProjectListRow(IndexedRecording recording, IndexedEdit? edit, string fullPath)
{
    public IndexedRecording Recording { get; } = recording;
    public IndexedEdit? Edit { get; } = edit;
    /// <summary>録画の行ならフォルダー、編集の行ならプロジェクトファイルのフルパス。</summary>
    public string FullPath { get; } = fullPath;
    public bool IsRecording => Edit is null;
    public string Name => Edit?.Name ?? Recording.Name;
    public string DateText => Edit is { } e ? $"{e.UpdatedAt:yyyy/MM/dd HH:mm} 更新" : $"{Recording.Date:yyyy/MM/dd HH:mm}";
    public string Problem => Edit?.Problem ?? Recording.Problem;
    public bool HasProblem => Problem.Length > 0;
    public string ProblemMark => HasProblem ? "⚠ " : "";
    public string ProblemText => HasProblem ? $"（{Problem}）" : "";
    /// <summary>編集が無い録画の行の補足。</summary>
    public string Note => Edit is null && Recording.Edits.Count == 0 ? "（編集なし）" : "";
    /// <summary>読み上げ用の名前（UI Automation）。</summary>
    public string AutomationName => (IsRecording ? "録画 " : "編集 ") + Name + (HasProblem ? "（" + Problem + "）" : "");
}

/// <summary>
/// プロジェクト一覧（v1.7。起動時と［ファイル］→［プロジェクト一覧］で表示）。
/// 一覧は保存先ルートの管理ファイル（projects.json）から作り、［最新の情報に更新］でフォルダーを調べ直す。
/// 開く・新しい編集・複製は主画面で行うため、選んだ操作を返して閉じる。名前の変更・削除・エクスポート・インポートはこの画面で行う。
/// </summary>
public sealed partial class ProjectListViewModel(ISettingsService settings, IDialogService dialogs, IWindowService windows,
    IShellService shell, IRecycleBin recycleBin, ILogger<ProjectListViewModel> logger) : ObservableObject
{
    public const string ZipFilter = "zip ファイル (*.zip)|*.zip";

    private ProjectIndex _index = new();
    private List<ProjectListRow> _selection = [];

    public ObservableCollection<ProjectListRow> Rows { get; } = [];

    /// <summary>主画面で開いている編集のプロジェクトファイル（未保存なら null）。削除・名前の変更をさせない。</summary>
    public string? OpenProjectPath { get; set; }
    /// <summary>主画面で開いている動画。その録画のフォルダーは削除させない。</summary>
    public string? OpenVideoPath { get; set; }

    /// <summary>閉じる（操作を選んだ場合はその操作、閉じるだけなら null）。</summary>
    public event Action<ProjectListAction?>? CloseRequested;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "";

    public string Root => settings.Current.Recording.ResolveOutputDirectory();

    public bool ShowOnStartup
    {
        get => settings.Current.Ui.ShowProjectListOnStartup;
        set
        {
            if (settings.Current.Ui.ShowProjectListOnStartup == value) return;
            settings.Current.Ui.ShowProjectListOnStartup = value;
            OnPropertyChanged();
        }
    }

    public string RootText => $"保存先: {Root}";
    public bool HasRows => Rows.Count > 0;

    private ProjectIndexStore Store => new(Root, logger);

    /// <summary>管理ファイルを読んで一覧を作る（無い・読めない場合はフォルダーを調べて作る）。</summary>
    public void Load()
    {
        try
        {
            _index = Store.LoadOrRebuild(out var rebuilt);
            StatusText = rebuilt ? "保存先のフォルダーを調べて一覧を作りました。" : $"一覧の更新: {_index.UpdatedAt.LocalDateTime:yyyy/MM/dd HH:mm}（エクスプローラーで変更した場合は［最新の情報に更新］）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Project list load failed");
            _index = new ProjectIndex();
            StatusText = "一覧を作れませんでした。";
            dialogs.ShowError($"プロジェクトの一覧を作れませんでした。{Environment.NewLine}{Store.IndexPath}", ex.Message);
        }
        BuildRows();
    }

    partial void OnSearchTextChanged(string value) => BuildRows();

    private void BuildRows(string? selectPath = null)
    {
        Rows.Clear();
        var q = SearchText.Trim();
        foreach (var r in _index.Recordings)
        {
            var folder = Path.Combine(Root, r.Folder);
            var edits = r.Edits.Where(e => q.Length == 0 || Match(e.Name, q) || Match(r.Name, q)).ToList();
            if (q.Length > 0 && edits.Count == 0 && !Match(r.Name, q)) continue;
            Rows.Add(new ProjectListRow(r, null, folder));
            foreach (var e in edits) Rows.Add(new ProjectListRow(r, e, Path.GetFullPath(Path.Combine(folder, e.Path))));
        }
        OnPropertyChanged(nameof(HasRows));
        if (selectPath is not null) SelectPathRequested?.Invoke(Rows.FirstOrDefault(x => string.Equals(x.FullPath, selectPath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>行を選ぶ（View が選択を変える）。</summary>
    public event Action<ProjectListRow?>? SelectPathRequested;

    private static bool Match(string text, string q) => text.Contains(q, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>View から選択中の行を受け取る。</summary>
    public void SetSelection(IEnumerable<ProjectListRow> rows)
    {
        _selection = rows.ToList();
        NotifyCommands();
    }

    private ProjectListRow? Single => _selection.Count == 1 ? _selection[0] : null;
    private bool HasSingle() => Single is not null;
    private bool HasSingleEdit() => Single is { IsRecording: false };
    private bool HasAny() => _selection.Count > 0;

    private void NotifyCommands()
    {
        OpenCommand.NotifyCanExecuteChanged();
        NewEditCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DuplicateFromCutsCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ExportAndDeleteCommand.NotifyCanExecuteChanged();
    }

    // ---------------- 主画面で行う操作 ----------------

    [RelayCommand(CanExecute = nameof(HasSingle))]
    private void Open()
    {
        if (Single is not { } row) return;
        if (row.IsRecording)
        {
            // 編集があれば最後に更新した編集、無ければ動画を開く。
            var latest = row.Recording.Edits.OrderByDescending(e => e.UpdatedAt).FirstOrDefault();
            if (latest is not null)
            {
                OpenEdit(Path.GetFullPath(Path.Combine(row.FullPath, latest.Path)));
                return;
            }
            var video = ProjectFolders.FindVideo(row.FullPath);
            if (video is null)
            {
                dialogs.ShowWarning($"録画のフォルダーに動画が見つかりません。{Environment.NewLine}{row.FullPath}{Environment.NewLine}{Environment.NewLine}エクスプローラーで変更した場合は［最新の情報に更新］を押してください。");
                return;
            }
            Close(new ProjectListAction(ProjectListActionKind.OpenVideo, video));
            return;
        }
        OpenEdit(row.FullPath);
    }

    private void OpenEdit(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            dialogs.ShowWarning($"プロジェクトファイルが見つかりません。{Environment.NewLine}{projectPath}{Environment.NewLine}{Environment.NewLine}エクスプローラーで変更した場合は［最新の情報に更新］を押してください。");
            return;
        }
        var problem = ProjectIndexStore.CheckProject(projectPath);
        TryStore(s => s.SetEditProblem(projectPath, problem));
        if (problem == ProjectIndexStore.EditUnreadableProblem)
        {
            Load();
            dialogs.ShowWarning(FileReadProblem.Message("プロジェクトファイルを読めません。", projectPath));
            return;
        }
        // 元の動画が見つからない場合は、主画面で開くときに動画の再指定を案内する。
        Close(new ProjectListAction(ProjectListActionKind.OpenProject, projectPath));
    }

    [RelayCommand(CanExecute = nameof(HasSingle))]
    private void NewEdit()
    {
        if (Single is not { } row) return;
        var folder = Path.Combine(Root, row.Recording.Folder);
        if (ProjectFolders.FindVideo(folder) is null)
        {
            dialogs.ShowWarning($"録画のフォルダーに動画が見つからないため、新しい編集を作れません。{Environment.NewLine}{folder}");
            return;
        }
        Close(new ProjectListAction(ProjectListActionKind.NewEdit, folder));
    }

    [RelayCommand(CanExecute = nameof(HasSingleEdit))]
    private void Duplicate() => DuplicateCore(ProjectListActionKind.Duplicate);

    [RelayCommand(CanExecute = nameof(HasSingleEdit))]
    private void DuplicateFromCuts() => DuplicateCore(ProjectListActionKind.DuplicateFromCuts);

    private void DuplicateCore(ProjectListActionKind kind)
    {
        if (Single is not { IsRecording: false } row) return;
        if (!File.Exists(row.FullPath))
        {
            dialogs.ShowWarning($"プロジェクトファイルが見つかりません。{Environment.NewLine}{row.FullPath}");
            return;
        }
        Close(new ProjectListAction(kind, row.FullPath));
    }

    [RelayCommand]
    private void StartRecording() => Close(new ProjectListAction(ProjectListActionKind.StartRecording));

    [RelayCommand]
    private void OpenVideoDialog() => Close(new ProjectListAction(ProjectListActionKind.OpenVideoDialog));

    [RelayCommand]
    private void OpenProjectDialog() => Close(new ProjectListAction(ProjectListActionKind.OpenProjectDialog));

    [RelayCommand]
    private void CloseWindow() => Close(null);

    private void Close(ProjectListAction? action) => CloseRequested?.Invoke(action);

    // ---------------- この画面で行う操作 ----------------

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            _index = Store.Rebuild();
            StatusText = $"保存先のフォルダーを調べ直しました（録画 {_index.Recordings.Count} 件）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Project list rebuild failed");
            dialogs.ShowError($"一覧を作り直せませんでした。{Environment.NewLine}{Store.IndexPath}", ex.Message);
        }
        BuildRows();
    }

    [RelayCommand(CanExecute = nameof(HasSingle))]
    private void OpenFolder()
    {
        if (Single is not { } row) return;
        var folder = row.IsRecording ? row.FullPath : Path.GetDirectoryName(row.FullPath)!;
        if (!Directory.Exists(folder))
        {
            dialogs.ShowWarning($"フォルダーが見つかりません。{Environment.NewLine}{folder}");
            return;
        }
        shell.OpenFolder(folder);
    }

    [RelayCommand(CanExecute = nameof(HasSingleEdit))]
    private void Rename()
    {
        if (Single is not { IsRecording: false } row) return;
        if (IsOpenProject(row.FullPath))
        {
            dialogs.ShowWarning("主画面で開いている編集の名前は変えられません。別のプロジェクトを開いてから変えてください。");
            return;
        }
        var name = windows.PromptText("名前の変更", $"編集の新しい名前を入力してください（先頭の日時はそのまま残します）。{Environment.NewLine}{Environment.NewLine}{row.FullPath}", row.Name, singleLine: true);
        if (name is null) return;
        try
        {
            var renamed = ProjectCatalog.RenameEdit(row.FullPath, name);
            logger.LogInformation("Renamed edit: {Old} -> {New}", row.FullPath, renamed);
            TryStore(s => s.Touch(renamed));
            _index = Store.TryLoad() ?? _index;
            StatusText = $"名前を変えました: {renamed}";
            BuildRows(renamed);
        }
        catch (InvalidOperationException ex)
        {
            dialogs.ShowWarning(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Rename failed: {Path}", row.FullPath);
            dialogs.ShowError($"名前を変えられませんでした（ファイルを使用中の可能性があります）。{Environment.NewLine}{row.FullPath}", ex.Message);
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(HasAny))]
    private void Delete()
    {
        var targets = DeletionTargets();
        if (targets.Count == 0 || !CheckNotOpen(targets, "削除")) return;
        var nl = Environment.NewLine;
        var lines = targets.Select(t => "・" + Describe(t)).ToList();
        var message = $"次の {targets.Count} 件をごみ箱へ移します。{nl}{nl}{string.Join(nl, lines.Take(12))}"
            + (lines.Count > 12 ? $"{nl}…ほか {lines.Count - 12} 件" : "")
            + $"{nl}{nl}ごみ箱から元に戻せます。";
        if (!windows.ConfirmDanger("削除の確認", message, "ごみ箱へ移す", "キャンセル")) return;
        var failed = RecycleAll(targets.Select(t => t.Path));
        AfterChange(targets.Select(t => t.Path));
        StatusText = failed.Count == 0 ? $"{targets.Count} 件をごみ箱へ移しました。" : $"{targets.Count - failed.Count} 件をごみ箱へ移しました（{failed.Count} 件は移せませんでした）。";
        ReportFailures(failed);
    }

    /// <summary>ごみ箱へ移すもの。録画を選んだらそのフォルダー、編集だけを選んだらその編集（録画も選んでいれば録画にまとめる）。</summary>
    private List<DeletionTarget> DeletionTargets()
    {
        var recordings = _selection.Where(r => r.IsRecording).Select(r => r.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var list = recordings.Select(f => new DeletionTarget(f, true)).ToList();
        foreach (var e in _selection.Where(r => !r.IsRecording))
        {
            var folder = Path.Combine(Root, e.Recording.Folder);
            if (recordings.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
            var target = ProjectCatalog.EditDeletionTarget(e.FullPath);
            if (!list.Any(t => string.Equals(t.Path, target, StringComparison.OrdinalIgnoreCase))) list.Add(new DeletionTarget(target, false, e.FullPath));
        }
        return list;
    }

    private sealed record DeletionTarget(string Path, bool IsRecording, string? ProjectPath = null);

    private static string Describe(DeletionTarget t)
    {
        if (t.IsRecording)
        {
            int edits = ProjectFolders.ProjectFilesIn(t.Path).Count;
            return $"録画のフォルダー（動画と編集 {edits} 件をすべて含む）{Environment.NewLine}　{t.Path}";
        }
        return Directory.Exists(t.Path)
            ? $"編集のフォルダー（書き出したファイルを含む）{Environment.NewLine}　{t.Path}"
            : $"編集のプロジェクトファイル（元の動画と、録画のフォルダーにある書き出したファイルは残ります）{Environment.NewLine}　{t.Path}";
    }

    /// <summary>主画面で開いている編集・動画を含むものは操作させない。操作してよければ true。</summary>
    private bool CheckNotOpen(IEnumerable<DeletionTarget> targets, string verb)
    {
        foreach (var t in targets)
        {
            bool open = t.IsRecording
                ? IsInside(OpenProjectPath, t.Path) || IsInside(OpenVideoPath, t.Path)
                : IsOpenProject(t.ProjectPath) || IsInside(OpenProjectPath, t.Path);
            if (!open) continue;
            dialogs.ShowWarning($"主画面で開いている編集・動画が含まれているため、{verb}できません。主画面で別のプロジェクトを開いてから、もう一度操作してください。{Environment.NewLine}{Environment.NewLine}{t.Path}");
            return false;
        }
        return true;
    }

    private bool IsOpenProject(string? path)
        => OpenProjectPath is not null && path is not null && string.Equals(Path.GetFullPath(OpenProjectPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string? path, string folder)
        => !string.IsNullOrEmpty(path) && (ProjectFolders.IsUnder(path, folder)
            || string.Equals(Path.GetFullPath(path), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));

    /// <summary>ごみ箱へ移す。保存先の外・保存先そのものは移さない。移せなかったもの（パスと理由）を返す。</summary>
    private List<(string Path, string Reason)> RecycleAll(IEnumerable<string> paths)
    {
        var failed = new List<(string, string)>();
        foreach (var p in paths)
        {
            if (!ProjectCatalog.IsSafeToDelete(p, Root))
            {
                failed.Add((p, "保存先のフォルダーの外にあります。"));
                continue;
            }
            try
            {
                recycleBin.MoveToRecycleBin(p);
                logger.LogInformation("Moved to recycle bin: {Path}", p);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recycle failed: {Path}", p);
                failed.Add((p, ex.Message));
            }
        }
        return failed;
    }

    private void ReportFailures(List<(string Path, string Reason)> failed)
    {
        if (failed.Count == 0) return;
        var nl = Environment.NewLine;
        dialogs.ShowError($"ごみ箱へ移せなかったものがあります（ファイルを使用中の可能性があります）。{nl}{nl}"
            + string.Join(nl, failed.Select(f => $"{f.Path}{nl}　{f.Reason}")));
    }

    /// <summary>ファイルを変えた後に一覧を書き換える。</summary>
    private void AfterChange(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p) || File.Exists(p) || ProjectFolders.RecordingFolderOf(p, Root) is not null) TryStore(s => s.Touch(p));
            else TryStore(s => s.Remove(p));
        }
        _index = Store.TryLoad() ?? _index;
        BuildRows();
    }

    private void TryStore(Action<ProjectIndexStore> action)
    {
        try { action(Store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Project index update failed");
        }
    }

    // ---------------- エクスポート・インポート ----------------

    private string ArchiveDirectory
    {
        get
        {
            var d = settings.Current.Ui.LastArchiveDirectory;
            return !string.IsNullOrWhiteSpace(d) && Directory.Exists(d) ? d : KnownFolders.Downloads;
        }
    }

    [RelayCommand(CanExecute = nameof(HasAny))]
    private Task ExportAsync() => ExportCoreAsync(deleteAfter: false);

    [RelayCommand(CanExecute = nameof(HasAny))]
    private Task ExportAndDeleteAsync() => ExportCoreAsync(deleteAfter: true);

    private async Task ExportCoreAsync(bool deleteAfter)
    {
        var folders = _selection.Select(r => Path.Combine(Root, r.Recording.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) return;
        var nl = Environment.NewLine;
        if (deleteAfter)
        {
            if (!CheckNotOpen(folders.Select(f => new DeletionTarget(f, true)), "エクスポートして削除")) return;
            var list = string.Join(nl, folders.Select(f => "・" + f));
            if (!windows.ConfirmDanger("エクスポートして削除",
                    $"次の録画（元の動画とすべての編集）を zip にエクスポートし、zip を読み直して確認できたら、フォルダーをごみ箱へ移します。{nl}{nl}{list}",
                    "エクスポートして削除", "キャンセル"))
                return;
        }

        // 保存先: 1 件ならファイル名を選ぶ、複数なら保存するフォルダーを選ぶ。
        var zips = new List<(string Folder, string Zip)>();
        if (folders.Count == 1)
        {
            var zip = dialogs.SaveFile(ZipFilter, ProjectArchive.DefaultFileName(Path.GetFileName(folders[0])), ArchiveDirectory, "エクスポート（zip）の保存先");
            if (zip is null) return;
            zips.Add((folders[0], zip));
        }
        else
        {
            var dir = dialogs.PickFolder(ArchiveDirectory, $"エクスポート（zip {folders.Count} 件）の保存先");
            if (dir is null) return;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                var name = ProjectArchive.DefaultFileName(Path.GetFileName(f));
                var path = Path.Combine(dir, name);
                for (int i = 2; File.Exists(path) || used.Contains(path); i++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{i}.zip");
                used.Add(path);
                zips.Add((f, path));
            }
        }
        settings.Current.Ui.LastArchiveDirectory = Path.GetDirectoryName(zips[0].Zip) ?? "";
        SaveSettingsQuietly();

        var archive = new ProjectArchive(logger);
        var done = new List<(string Folder, string Zip)>();
        foreach (var (folder, zip) in zips)
        {
            if (string.Equals(Path.GetFullPath(zip), folder, StringComparison.OrdinalIgnoreCase) || ProjectFolders.IsUnder(zip, folder))
            {
                dialogs.ShowError($"録画のフォルダーの中には zip を保存できません。別の場所を選んでください。{nl}{zip}");
                break;
            }
            string? verifyError = null;
            try
            {
                var completed = await windows.RunWithProgressAsync("エクスポート", async (progress, ct) =>
                {
                    var p = new Progress<double>(r => progress.Report(new ProgressInfo(r, $"{Path.GetFileName(folder)} を zip にしています（{r:0%}）")));
                    await archive.ExportAsync(folder, zip, ProjectService.AppVersion, p, ct);
                    progress.Report(new ProgressInfo(null, "zip を読み直して確認しています…"));
                    verifyError = await Task.Run(() => ProjectArchive.Verify(zip, folder), ct);
                });
                if (!completed)
                {
                    StatusText = "エクスポートをキャンセルしました（途中のファイルは削除しました）。";
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Export failed: {Folder}", folder);
                dialogs.ShowError($"エクスポートできませんでした。{nl}{folder}{nl}→ {zip}", ex.Message);
                break;
            }
            if (verifyError is not null)
            {
                logger.LogWarning("Export verification failed: {Zip}: {Reason}", zip, verifyError);
                dialogs.ShowError($"エクスポートした zip を確認できませんでした{(deleteAfter ? "。フォルダーは削除していません" : "")}。{nl}{zip}{nl}{nl}{verifyError}");
                break;
            }
            done.Add((folder, zip));
        }
        if (done.Count == 0) return;

        var failed = new List<(string Path, string Reason)>();
        if (deleteAfter)
        {
            failed = RecycleAll(done.Select(d => d.Folder));
            AfterChange(done.Select(d => d.Folder));
        }
        StatusText = $"{done.Count} 件をエクスポートしました{(deleteAfter ? "（フォルダーはごみ箱へ移しました）" : "")}: {Path.GetDirectoryName(done[0].Zip)}";
        ReportFailures(failed);
        if (dialogs.Confirm($"{done.Count} 件をエクスポートしました{(deleteAfter && failed.Count == 0 ? "。フォルダーはごみ箱へ移しました" : "")}。{nl}{string.Join(nl, done.Select(d => d.Zip))}{nl}{nl}保存したフォルダーを開きますか？", "完了"))
            shell.RevealFile(done[0].Zip);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var zip = dialogs.OpenFile(ZipFilter, ArchiveDirectory, "インポートする zip");
        if (zip is null) return;
        settings.Current.Ui.LastArchiveDirectory = Path.GetDirectoryName(zip) ?? "";
        SaveSettingsQuietly();
        string? imported = null;
        var nl = Environment.NewLine;
        try
        {
            var completed = await windows.RunWithProgressAsync("インポート", async (progress, ct) =>
            {
                var p = new Progress<double>(r => progress.Report(new ProgressInfo(r, $"{Path.GetFileName(zip)} を取り込んでいます（{r:0%}）")));
                imported = await new ProjectArchive(logger).ImportAsync(zip, Root, p, ct);
            });
            if (!completed)
            {
                StatusText = "インポートをキャンセルしました（途中のファイルは削除しました）。";
                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            dialogs.ShowWarning(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import failed: {Zip}", zip);
            dialogs.ShowError(FileReadProblem.Message("zip をインポートできませんでした。", zip), ex.Message);
            return;
        }
        TryStore(s => s.Touch(imported!));
        _index = Store.TryLoad() ?? _index;
        SearchText = "";
        BuildRows(imported);
        StatusText = $"インポートしました: {imported}";
        var renamed = !string.Equals(Path.GetFileName(imported), ReadFolderName(zip), StringComparison.OrdinalIgnoreCase);
        dialogs.ShowInfo($"インポートしました。{nl}{imported}"
            + (renamed ? $"{nl}{nl}同じ名前のフォルダーがあったため、末尾に番号を付けました。" : ""), "完了");
    }

    private static string ReadFolderName(string zip)
    {
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            return ProjectArchive.ReadManifest(z, zip).Folder;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { return ""; }
    }

    private void SaveSettingsQuietly()
    {
        try { settings.Save(); }
        catch (Exception ex) { logger.LogWarning(ex, "Settings save failed"); }
    }
}
