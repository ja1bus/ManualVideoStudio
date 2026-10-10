using System.IO;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// Gemini での字幕作成（v1.12）と、「現在の編集を残して、カットからやり直す」。
/// 字幕依頼の時間軸はカット後の動画で、Gemini Chat 連携（AI 連携。元動画の時間軸）とは別の画面・別の仕組み。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>字幕依頼用動画の状態（画面を閉じても残る。編集の切り替え・カットのやり直しで無効になる）。</summary>
    public CaptionRequestSession CaptionRequest { get; } = new();

    private bool CanOpenGeminiCaption() => HasProject && IsSubtitlePhase && _geminiCaption is not null;

    /// <summary>字幕編集の［Geminiで字幕を作る…］。簡易モードでも使える。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenGeminiCaption))]
    private void OpenGeminiCaption()
    {
        if (Editor is null || _geminiCaption is null || !IsSubtitlePhase) return;
        PauseIfPlaying();
        var name = ProjectFolders.DefaultOutputBaseName(VideoPath, ProjectPath);
        var vm = new GeminiCaptionViewModel(Editor, CaptionRequest, _geminiCaption, name);
        vm.Imported += message =>
        {
            SelectedTabIndex = TabSubtitles;
            StatusMessage = message;
            NotifySubtitlesImported(); // 取り込んだままの間は、確認して調整する案内を出す（v1.13）
        };
        _windows.ShowGeminiCaption(vm);
        RefreshAll();
    }

    private bool CanRestartFromCuts() => HasProject && IsSubtitlePhase;

    /// <summary>
    /// ［現在の編集を残して、カットからやり直す］: 現在の最新状態をプロジェクトファイルへ保存して残し、同じ元動画を参照する
    /// 別の編集（カットは引き継ぎ、字幕・図形は除く）を作って開き、カット編集から始める。
    /// 保存・作成に失敗したら、この編集（字幕・図形を含む）に留まる。「保存しないで続行」はできない。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartFromCuts))]
    private async Task RestartFromCutsKeepingCurrentAsync()
    {
        if (Editor is null || !IsSubtitlePhase) return;
        PauseIfPlaying();
        var nl = Environment.NewLine;
        var root = Settings.Recording.ResolveOutputDirectory();
        // 別の編集は保存先の中の録画のフォルダー（edits\）に作る。外の動画では作れない（この編集はそのまま）。
        if (CurrentRecordingFolder is null)
        {
            _dialogs.ShowWarning($"保存先のフォルダーの外にある動画のため、別の編集を作れません。この編集はそのままです。{nl}{VideoPath}{nl}{nl}保存先: {root}{nl}"
                + "（字幕・図形を破棄してカットを変更する場合は［カット編集へ戻る］を使ってください）");
            return;
        }

        var sourceName = ProjectFolders.DisplayName(ProjectPath is not null && ProjectFolders.IsEditFolderProject(VideoPath, ProjectPath)
            ? Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!)
            : ProjectPath is not null ? ProjectFolders.ProjectFileBaseName(ProjectPath) : Path.GetFileNameWithoutExtension(VideoPath));
        var current = $"字幕 {Editor.Project.SubtitleSegments.Count} 件・図形 {Editor.Project.ShapeGroups.Count} 件";
        var name = _windows.PromptText("現在の編集を残して、カットからやり直す",
            $"現在の編集（{current}。未保存の修正を含む）をプロジェクトファイルに保存して残し、カットだけ引き継いだ別の編集を作って、カット編集から始めます。{nl}"
            + $"元の編集はプロジェクト一覧に残り、後から開いて以前のカット・字幕・図形に戻せます。元動画はコピーせず、同じものを使います（復元には元動画が必要です）。{nl}{nl}"
            + $"新しい編集の名前を入力してください。{nl}作成先: {Path.Combine(CurrentRecordingFolder, ProjectFolders.EditsFolderName, "日時_名前")}",
            sourceName + "_カットから", singleLine: true);
        if (name is null)
        {
            StatusMessage = "カットからやり直すのを取りやめました（この編集はそのままです）。";
            return;
        }

        // 1. 現在の最新状態を保存する。失敗・キャンセルならこの編集に留まる（字幕・図形は破棄しない）。
        var firstSave = ProjectPath is null ? ProjectFolders.FirstSavePath(VideoPath, root) : null;
        if (!Save(saveAs: false, firstSave))
        {
            StatusMessage = "現在の編集を保存できなかったため、カットからやり直すのを中止しました（字幕・図形はそのままです）。";
            return;
        }
        var sourcePath = ProjectPath!;
        var recordingFolder = ProjectFolders.RecordingFolderOf(sourcePath, root);
        if (recordingFolder is null)
        {
            _dialogs.ShowWarning($"このプロジェクトは保存先のフォルダーの外にあるため、別の編集を作れません。現在の編集は保存しました（そのままです）。{nl}{sourcePath}{nl}{nl}保存先: {root}");
            return;
        }

        // 2. 別の編集を作って保存する（元の編集のファイルには触れない）。
        string folder;
        try
        {
            folder = ProjectFolders.CreateEditFolder(recordingFolder, DateTime.Now, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"編集のフォルダーを作れませんでした。現在の編集は保存済みで、そのままです。{nl}{Path.Combine(recordingFolder, ProjectFolders.EditsFolderName)}", ex.Message);
            return;
        }
        var path = Path.Combine(folder, Path.GetFileName(folder) + ProjectService.Extension);
        var copy = ProjectCatalog.PrepareCopy(Editor.Project, restartFromCuts: true, Settings.DefaultExport);
        // 書き出し先が元の編集と同じ共有フォルダーを指していると、以前の完成物・画像を上書きするおそれがある。新しい編集の既定（自分のフォルダー）にする。
        var original = Editor.Project.ExportSettings;
        if (original.ImageOutputDirectory.Length > 0
            && string.Equals(copy.ExportSettings.ImageOutputDirectory, original.ImageOutputDirectory, StringComparison.OrdinalIgnoreCase))
            copy.ExportSettings.ImageOutputDirectory = "";
        try
        {
            _projects.Save(copy, path);
        }
        catch (Exception ex)
        {
            RemoveIfEmpty(folder);
            _dialogs.ShowError($"別の編集を保存できませんでした。現在の編集は保存済みで、そのままです（字幕・図形も残っています）。{nl}{path}", ex.Message);
            return;
        }
        _logger.LogInformation("Restart from cuts keeping current edit: {Source} -> {Path}", sourcePath, path);
        TouchProjectIndex(path);

        // 3. 新しい編集を開く（保存済みのため、保存の確認は不要）。開けなければ元の編集を開いたままにする。
        await OpenProjectFileAsync(path, changesConfirmed: true);
        if (!string.Equals(ProjectPath, path, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"別の編集は作りましたが、開けませんでした。この編集（元の編集）を開いたままです。新しい編集: {path}";
            return;
        }
        SelectedTabIndex = TabCuts;
        StatusMessage = $"現在の編集を残して、カットからやり直す編集を作りました: {path}（元の編集: {sourcePath}。プロジェクト一覧から開けます）";
    }
}
