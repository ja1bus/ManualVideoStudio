using System.IO;
using ManualVideoStudio.Core.Join;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 動画を追加して録画する（v1.18）。プロジェクト一覧で選んだ録画・編集の元の動画に続きを録画し、元の動画につなげて新しいプロジェクトにする。
/// 結合の対象は元の動画だけ（選んだ編集のカット・字幕・図形は引き継がない）。元のプロジェクト・元の動画・追加録画は変更・削除しない。
/// 条件が違えば録画の前（動画の確認）か連結の前に理由を示す。追加録画は、連結に失敗しても残す。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 追加録画から、連結して編集（または追加録画だけで編集）するまで。path は録画のフォルダーまたはプロジェクトファイル。
    /// 未保存の編集が開いていれば、画面を切り替える前に保存を確認する。
    /// </summary>
    public async Task AppendRecordingAsync(string path)
    {
        var nl = Environment.NewLine;
        if (_recordingJoin is null) return;
        if (_windows.IsRecorderOpen)
        {
            _windows.ActivateRecorder();
            return;
        }
        if (!ConfirmDiscardChanges()) return;
        if (!EnsureFFmpeg()) return;

        // 1. 元の動画を決める（編集は保存されたパスではなく、読み込み後に再解決された元の動画を使う）。
        string? video;
        if (Directory.Exists(path))
        {
            video = ProjectFolders.FindVideo(path);
            if (video is null)
            {
                _dialogs.ShowError($"録画のフォルダーに動画が見つかりません。{nl}{path}");
                return;
            }
        }
        else
        {
            ProjectLoadResult loaded;
            try
            {
                loaded = _projects.Load(path);
            }
            catch (Exception ex)
            {
                _dialogs.ShowError(ex.Message, ex.InnerException?.Message);
                return;
            }
            if (!loaded.VideoFound)
            {
                _dialogs.ShowWarning($"元の動画が見つからないため、追加録画できません。先に編集を開いて動画を指定し直してください。{nl}{path}");
                return;
            }
            video = loaded.Project.SourceVideo.Path;
        }
        var sourceName = SourceDisplayName(video);

        // 2. 元の動画を ffprobe で検査し直す（保存済みの動画情報・録画設定は判定に使わない）。録画できる条件を満たさなければ録画前に断る。
        StatusMessage = "元の動画を確認しています…";
        AppendRequirement? requirement;
        try
        {
            var info = await _recordingJoin.ProbeAsync(FFmpeg.FfprobePath!, video);
            requirement = JoinCompatibility.CheckAppendable(info, out var problems);
            if (requirement is null)
            {
                StatusMessage = "追加録画できない動画です。";
                _dialogs.ShowWarning($"この動画には、追加録画をつなげられません。{nl}{video}{nl}{nl}"
                    + string.Join(nl, problems.Select(p => "・" + p)));
                return;
            }
        }
        catch (JoinException ex)
        {
            StatusMessage = "元の動画を確認できませんでした。";
            _dialogs.ShowError(ex.Message, ex.Detail);
            return;
        }

        // 3. 追加録画（録画画面は元の動画と同じ条件に固定される）。
        PauseIfPlaying();
        var outcome = await _windows.ShowRecorderAsync(new AppendRecordingContext(sourceName, video, requirement));
        if (outcome is null)
        {
            StatusMessage = "追加録画を取り消しました。";
            return;
        }
        StatusMessage = $"追加録画を保存しました: {outcome.OutputPath}";
        TouchProjectIndex(outcome.OutputPath);

        // 4. 連結せず、追加録画だけで編集する場合は、通常の録画と同じ。
        if (!outcome.JoinWithSource)
        {
            if (!outcome.StartEditing) return;
            await OpenVideoFileAsync(outcome.OutputPath);
            if (Editor is not null && string.Equals(VideoPath, outcome.OutputPath, StringComparison.OrdinalIgnoreCase))
            {
                Editor.Project.RecordingSettings = outcome.Info;
                StatusMessage = $"追加録画だけの動画を開きました: {Path.GetFileName(outcome.OutputPath)}。";
            }
            return;
        }

        // 5. 元の動画に追加録画をつなげて、新しいプロジェクトにする。
        await JoinAndOpenAsync(video, sourceName, outcome);
    }

    private async Task JoinAndOpenAsync(string sourceVideo, string sourceName, RecordingOutcome outcome)
    {
        var nl = Environment.NewLine;
        var root = Settings.Recording.ResolveOutputDirectory();
        RecordingJoinService.CleanStaleWorkFolders(root, _logger);
        var addedName = ProjectFolders.DisplayName(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(outcome.OutputPath)) ?? ""));
        var request = new JoinRequest(FFmpeg.FfmpegPath!, FFmpeg.FfprobePath!, root,
            [new JoinInput(sourceVideo, sourceName), new JoinInput(outcome.OutputPath, string.IsNullOrEmpty(addedName) ? "追加録画" : addedName)],
            outcome.ProjectName, DateTime.Now);

        JoinResult? result = null;
        bool completed;
        try
        {
            completed = await _windows.RunWithProgressAsync("動画をつなげています", async (progress, ct) =>
            {
                progress.Report(new ProgressInfo(null, "元の動画と追加録画をつなげています（再エンコードはしません）…"));
                result = await _recordingJoin!.JoinAsync(request, ct);
            });
        }
        catch (JoinException ex)
        {
            _logger.LogError(ex, "Join failed");
            StatusMessage = "動画をつなげられませんでした。";
            _dialogs.ShowError($"{ex.Message}{nl}{string.Join(nl, ex.Problems.Select(p => "・" + p))}{nl}{nl}追加録画は残しています（一覧から開けます）。{nl}{outcome.OutputPath}", ex.Detail);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Join failed");
            StatusMessage = "動画をつなげられませんでした。";
            _dialogs.ShowError($"動画をつなげられませんでした。元の動画は変更していません。追加録画は残しています。{nl}{outcome.OutputPath}", ex.Message);
            return;
        }
        if (!completed || result is null)
        {
            StatusMessage = "動画をつなげるのを取り消しました。追加録画は残しています。";
            return;
        }

        // 6. プロジェクトを作って開く（結合の記録は動画と同じフォルダー）。録画の記録は単一の録画を表さないため持たない。
        var projectPath = Path.Combine(result.Folder, Path.GetFileName(result.Folder) + ProjectService.Extension);
        var project = NewProjectDocument(new FileInfo(result.VideoPath), result.Metadata);
        project.RecordingSettings = null;
        TouchProjectIndex(result.VideoPath);
        try
        {
            _projects.Save(project, projectPath);
        }
        catch (Exception ex)
        {
            // 完成した動画は消さない。場所を示す。
            _logger.LogError(ex, "Project save failed after join: {Path}", projectPath);
            _dialogs.ShowError($"つなげた動画は作れましたが、プロジェクトを保存できませんでした。動画は次の場所に残しています。一覧から、この動画で新しい編集を作れます。{nl}{result.VideoPath}", ex.Message);
            return;
        }
        _logger.LogInformation("Created joined project: {Path}", projectPath);
        AddRecent(projectPath);
        Settings.Ui.LastProjectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        SaveSettingsQuietly();
        SetProject(project, projectPath);
        TouchProjectIndex(projectPath);
        StatusMessage = result.SourcesSaved
            ? $"元の動画に追加録画をつなげて、新しいプロジェクトを作りました: {projectPath}"
            : $"つなげた動画のプロジェクトを作りました（結合元の記録 {JoinSourcesFile.FileName} は書けませんでしたが、動画と編集は使えます）: {projectPath}";
    }

    /// <summary>追加録画の画面などに出す元の動画の名前（保存先の録画のフォルダーなら日時を除いた名前、それ以外はファイル名）。</summary>
    private string SourceDisplayName(string video)
    {
        var root = Settings.Recording.ResolveOutputDirectory();
        if (ProjectFolders.IsInProjectFolder(video, root))
            return ProjectFolders.DisplayName(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(video))!));
        return Path.GetFileNameWithoutExtension(video);
    }
}
