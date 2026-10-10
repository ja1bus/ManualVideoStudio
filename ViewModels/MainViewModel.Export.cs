using System.IO;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.FFmpeg;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>動画・画像の書き出し。ffmpeg の実行は Core のサービスで行い、ここでは確認・進捗・結果表示のみ。</summary>
public sealed partial class MainViewModel
{
    public const string VideoOutputFilter = "MP4 動画 (*.mp4)|*.mp4|Matroska 動画 (*.mkv)|*.mkv";
    public const string GifOutputFilter = "Animated GIF (*.gif)|*.gif";

    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task ExportVideoAsync()
    {
        if (Editor is null || Metadata is null || !EnsureFFmpeg()) return;
        PauseIfPlaying();
        if (!FlushSubtitleDraft()) return;
        var project = Editor.Project;
        if (project.CutSegments.Count == 0 && project.SubtitleSegments.Count == 0 && project.ShapeGroups.Count == 0
            && !project.Title.HasTitle && !_dialogs.Confirm("確定したカット区間・字幕・図形がありません。元動画全体を再エンコードして書き出しますか？"))
            return;
        var pendingCount = PendingCandidateCount;
        if (pendingCount > 0
            && !_dialogs.Confirm($"未判断のカット候補が {pendingCount} 件あります。候補は書き出しに反映されません（確定区間のみ反映）。続行しますか？"))
            return;

        var defaultPath = VideoExportService.DefaultOutputPath(VideoPath, project.ExportSettings, ProjectPath);
        var output = _dialogs.SaveFile(VideoOutputFilter, Path.GetFileName(defaultPath), Path.GetDirectoryName(defaultPath), "編集済み動画の書き出し");
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(VideoPath), StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowError("元動画と同じファイルには書き出せません。別の名前を指定してください。");
            return;
        }

        var es = project.ExportSettings;
        if (!CheckSubtitleSupport(project) || !await ConfirmSubtitleFontAsync(project)) return;
        if (!TryBuildShapeLayers(project, out var shapes)) return;
        // 映像は偶数の大きさへ切り下げて書き出すため、タイトルの画像も同じ大きさで作る。
        if (!TryBuildTitleImage(project, Metadata.Width & ~1, Metadata.Height & ~1, out var titleImage)) return;
        var request = new VideoExportRequest(FFmpeg.FfmpegPath!, FFmpeg.MajorVersion, VideoPath, Metadata,
            project.CutSegments.ToList(), output,
            new VideoEncodeOptions { Crf = es.VideoCrf, Preset = es.VideoPreset, AudioBitrateKbps = es.AudioBitrateKbps },
            project.SubtitleSegments.ToList(), Settings.Subtitle.Clone(), es.TransitionSecondsForExport, WpfSubtitleTextMeasurer.Instance, shapes, titleImage);
        VideoExportResult? result = null;
        try
        {
            var completed = await _windows.RunWithProgressAsync("動画の書き出し", async (progress, ct) =>
            {
                var p = new Progress<VideoExportProgress>(x => progress.Report(new ProgressInfo(x.Ratio,
                    $"書き出し中 {TimeFormat.Format(x.ProcessedSeconds)} / {TimeFormat.Format(x.TotalSeconds)}（{x.Ratio:0%}）")));
                result = await _videoExport.ExportAsync(request, p, ct);
            });
            if (!completed)
            {
                StatusMessage = "動画の書き出しをキャンセルしました（途中のファイルは削除しました）。";
                return;
            }
        }
        catch (FFmpegException ex)
        {
            _dialogs.ShowError(ex.Message, ex.ErrorDetail);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            _dialogs.ShowError("動画の書き出しに失敗しました。", ex.Message);
            return;
        }

        es.VideoOutputDirectory = Path.GetDirectoryName(output) ?? "";
        es.VideoOutputFileName = Path.GetFileName(output);
        Editor.MarkDirty();
        StatusMessage = $"書き出しました: {output}（{TimeFormat.Format(result!.OutputDuration)}、{result.Elapsed.TotalSeconds:0} 秒{BurnedInText(project)}）";
        if (_dialogs.Confirm($"書き出しが完了しました。{Environment.NewLine}{output}{Environment.NewLine}{Environment.NewLine}フォルダーを開きますか？", "完了"))
            _shell.RevealFile(output);
    }

    /// <summary>完了表示の「、字幕 n 件・図形 m 件を焼き込み」。</summary>
    private static string BurnedInText(ProjectDocument project)
    {
        var parts = new List<string>();
        if (project.Title.HasTitle) parts.Add("タイトル");
        if (project.SubtitleSegments.Count > 0) parts.Add($"字幕 {project.SubtitleSegments.Count} 件");
        if (project.ShapeGroups.Count > 0) parts.Add($"図形 {project.ShapeGroups.Count} 件");
        return parts.Count == 0 ? "" : $"、{string.Join("・", parts)}を焼き込み";
    }

    /// <summary>
    /// 図形（v1.6）の書き出し用の画像を作る（WPF の描画のため UI スレッドで行う）。失敗したらエラーを出して false。
    /// </summary>
    private bool TryBuildShapeLayers(ProjectDocument project, out VideoShapeLayers? layers)
    {
        layers = null;
        if (project.ShapeGroups.Count == 0 || Metadata is null) return true;
        try
        {
            layers = ShapeOverlayRenderer.Build(project.ShapeGroups, Metadata.Width, Metadata.Height);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            _logger.LogError(ex, "Shape overlay render failed");
            _dialogs.ShowError("図形の画像を作れませんでした。", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 黒背景タイトル（v1.15）の書き出し用の画像を作る（タイトルなしなら null で成功）。文章が 2 行に収まらないときはエラーを出して false
    /// （黙って切り捨てない）。プレビューと同じ描画処理（TitleRenderer）で作るため、WPF の描画として UI スレッドで行う。
    /// </summary>
    private bool TryBuildTitleImage(ProjectDocument project, int width, int height, out Core.Export.VideoTitleImage? image)
    {
        image = null;
        if (!project.Title.HasTitle) return true;
        try
        {
            var mainDuration = EditTimelineCalculator.OutputDuration(EditTimelineCalculator.ComputeKeepRanges(Duration, project.CutSegments));
            var (built, error) = TitleRenderer.BuildExportImage(project.Title, width, height, mainDuration);
            if (error is not null)
            {
                _dialogs.ShowError(error + Environment.NewLine + "［タイトル...］で文章を直してください。");
                return false;
            }
            image = built;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            _logger.LogError(ex, "Title image render failed");
            _dialogs.ShowError("タイトルの画像を作れませんでした。", ex.Message);
            return false;
        }
    }

    /// <summary>字幕を焼き込むには ffmpeg の subtitles フィルタ（libass）が必要。</summary>
    private bool CheckSubtitleSupport(ProjectDocument project)
    {
        if (project.SubtitleSegments.Count == 0 || FFmpeg.Capabilities is null || FFmpeg.Capabilities.HasSubtitles) return true;
        _dialogs.ShowError("この ffmpeg には字幕の焼き込み（subtitles フィルタ / libass）がありません。libass を含む ffmpeg（gyan.dev の full / essentials 版など）を指定してください。");
        return false;
    }

    /// <summary>
    /// 字幕のフォントがこの PC で使えるか（libass が指定と違うフォントで描かないか）を書き出し前に確かめる（v1.5）。
    /// 違う場合は続けるか尋ねる。確かめられない場合はそのまま続ける。
    /// </summary>
    private async Task<bool> ConfirmSubtitleFontAsync(ProjectDocument project)
    {
        if (project.SubtitleSegments.Count == 0 || _subtitleCheck is null || !_subtitleCheck.IsAvailable) return true;
        var warning = await _subtitleCheck.CheckFontAsync(Settings.Subtitle, CancellationToken.None);
        if (warning is null) return true;
        _logger.LogWarning("Subtitle font mismatch: {Warning}", warning);
        return _dialogs.Confirm(warning + Environment.NewLine + Environment.NewLine + "このまま書き出しますか？（フォントは［字幕の設定...］で変えられます）", "字幕のフォント");
    }

    /// <summary>
    /// Animated GIF の書き出し。MP4 と同じカット・字幕を使い、GIF 用に FPS・大きさを下げてパレット処理する。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private async Task ExportGifAsync()
    {
        if (Editor is null || Metadata is null || !EnsureFFmpeg()) return;
        PauseIfPlaying();
        if (!FlushSubtitleDraft()) return;
        var project = Editor.Project;
        if (FFmpeg.Capabilities is { HasGifPalette: false })
        {
            _dialogs.ShowError("この ffmpeg には GIF の作成に必要なフィルタ（palettegen / paletteuse）がありません。別の ffmpeg を指定してください。");
            return;
        }
        if (!CheckSubtitleSupport(project) || !await ConfirmSubtitleFontAsync(project)) return;
        var settings = project.ExportGifSettings;
        var choose = new GifExportViewModel(settings.Quality, Editor.OutputDuration, project.SubtitleSegments.Count,
            Metadata.Width, Metadata.Height, Metadata.HasAudio, project.ShapeGroups.Count,
            project.Title.HasTitle ? VideoTitle.NormalizeSeconds(project.Title.Seconds) : 0);
        if (!_windows.ChooseGifQuality(choose)) return;
        if (IsCutPhase && project.CutSegments.Count > 0 && PendingCandidateCount > 0
            && !_dialogs.Confirm($"未判断のカット候補が {PendingCandidateCount} 件あります。候補は書き出しに反映されません（確定区間のみ反映）。続行しますか？"))
            return;

        var defaultPath = GifExportService.DefaultOutputPath(VideoPath, settings, ProjectPath);
        var output = _dialogs.SaveFile(GifOutputFilter, Path.GetFileName(defaultPath), Path.GetDirectoryName(defaultPath), "Animated GIF の書き出し");
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(VideoPath), StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowError("元動画と同じファイルには書き出せません。別の名前を指定してください。");
            return;
        }

        if (!TryBuildShapeLayers(project, out var shapes)) return;
        if (!TryBuildTitleImage(project, Metadata.Width, Metadata.Height, out var titleImage)) return;
        var request = new GifExportRequest(FFmpeg.FfmpegPath!, FFmpeg.MajorVersion, VideoPath, Metadata,
            project.CutSegments.ToList(), project.SubtitleSegments.ToList(), Settings.Subtitle.Clone(), choose.Quality, output,
            project.ExportSettings.TransitionSecondsForExport, WpfSubtitleTextMeasurer.Instance, shapes, titleImage);
        GifExportResult? result = null;
        try
        {
            var completed = await _windows.RunWithProgressAsync("Animated GIF の書き出し", async (progress, ct) =>
            {
                var p = new Progress<VideoExportProgress>(x => progress.Report(new ProgressInfo(x.Ratio,
                    $"{(x.Ratio < 0.4 ? "色を分析しています（1/2）" : "GIF を作成しています（2/2）")}… {x.Ratio:0%}")));
                result = await _gifExport.ExportAsync(request, p, ct);
            });
            if (!completed)
            {
                StatusMessage = "GIF の書き出しをキャンセルしました（途中のファイルは削除しました）。";
                return;
            }
        }
        catch (FFmpegException ex)
        {
            _dialogs.ShowError(ex.Message, ex.ErrorDetail);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GIF export failed");
            _dialogs.ShowError("GIF の書き出しに失敗しました。", ex.Message);
            return;
        }

        settings.Quality = choose.Quality;
        settings.OutputDirectory = Path.GetDirectoryName(output) ?? "";
        settings.OutputFileName = Path.GetFileName(output);
        Editor.MarkDirty();
        var mb = result!.FileSize / 1024.0 / 1024.0;
        StatusMessage = $"GIF を書き出しました: {output}（{TimeFormat.Format(result.OutputDuration)}、{mb:0.0} MB）";
        if (_dialogs.Confirm($"GIF の書き出しが完了しました（{mb:0.0} MB）。{Environment.NewLine}{output}{Environment.NewLine}{Environment.NewLine}フォルダーを開きますか？", "完了"))
            _shell.RevealFile(output);
    }

    [RelayCommand(CanExecute = nameof(CanUseFullFeatures))]
    private Task ExportImagesAsync() => ExportCaptureImagesAsync(tejunsho: false);

    /// <summary>手順書用に書き出す（v1.8）: 出力先の images に画像、出力先に capture-metadata.json と meta.json の下書き。</summary>
    [RelayCommand(CanExecute = nameof(CanExportExternalDocument))]
    private Task ExportTejunshoAsync() => ExportCaptureImagesAsync(tejunsho: true);

    private async Task ExportCaptureImagesAsync(bool tejunsho)
    {
        // 文書作成用の経路は、コマンドを直接呼ばれても、設定で無効（または簡易モード）なら何も始めない（v1.14）。
        if (tejunsho && !CanExportExternalDocument) return;
        if (Editor is null || !EnsureFFmpeg()) return;
        PauseIfPlaying();
        var project = Editor.Project;
        if (project.CapturePoints.Count == 0)
        {
            _dialogs.ShowInfo("手順書画像が登録されていません。動画を見ながら［この位置を画像に追加］で登録してください。");
            return;
        }

        var es = project.ExportSettings;
        var title = tejunsho ? Loc.Get("Doc.ExportTitle") : "画像の書き出し";
        var dir = tejunsho ? TejunshoExport.DefaultDirectory(VideoPath, es, ProjectPath) : ImageExportService.DefaultDirectory(VideoPath, es, ProjectPath);
        var what = tejunsho
            ? Loc.Get("Doc.ExportWhat")
            : "次のフォルダーに画像を書き出します。";
        var choice = _dialogs.ConfirmYesNoCancel($"{what}{Environment.NewLine}{dir}{Environment.NewLine}{Environment.NewLine}［はい］このフォルダー　［いいえ］別のフォルダーを選ぶ", title);
        if (choice is null) return;
        if (choice == false)
        {
            var parent = Directory.Exists(dir) ? dir : Path.GetDirectoryName(dir);
            var picked = _dialogs.PickFolder(parent, tejunsho ? Loc.Get("Doc.PickFolder") : "画像の出力先フォルダー");
            if (picked is null) return;
            dir = picked;
        }

        var imagesDir = tejunsho ? TejunshoExport.ImagesDirectory(dir) : dir;
        var plan = ImageExportService.Plan(project.CapturePoints, imagesDir, es.ImageFormat);
        var empty = plan.Items.Count(i => string.IsNullOrWhiteSpace(i.Point.Caption));
        if (empty > 0 && !_dialogs.Confirm($"キャプションが空の画像が {empty} 件あります（ファイル名は連番のみになります）。続行しますか？"))
            return;

        bool overwrite = true;
        if (plan.ExistingCount > 0)
        {
            var r = _dialogs.ConfirmYesNoCancel($"同じ名前のファイルが {plan.ExistingCount} 件あります。上書きしますか？{Environment.NewLine}［はい］上書き　［いいえ］既存のファイルはそのまま（スキップ）", "上書きの確認");
            if (r is null) return;
            overwrite = r.Value;
        }
        if (plan.StaleFiles.Count > 0)
        {
            var list = string.Join(Environment.NewLine, plan.StaleFiles.Take(10)) + (plan.StaleFiles.Count > 10 ? $"{Environment.NewLine}…ほか {plan.StaleFiles.Count - 10} 件" : "");
            var r = _dialogs.ConfirmYesNoCancel($"出力先に、今回の出力に含まれない連番画像が {plan.StaleFiles.Count} 件あります（以前のキャプション・連番で出力したもの等）。手順書の生成時に混ざるおそれがあります。{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}［はい］ごみ箱へ移動する　［いいえ］残す", "古い画像");
            if (r is null) return;
            if (r == true) MoveToRecycleBin(imagesDir, plan.StaleFiles);
        }

        ImageExportResult? result = null;
        var keep = EditTimelineCalculator.ComputeKeepRanges(Duration, project.CutSegments);
        var projectName = TejunshoExport.ProjectName(VideoPath, ProjectPath);
        var metadata = new ImageMetadataOptions(tejunsho ? ImageMetadataLayout.Tejunsho : ImageMetadataLayout.Images, projectName,
            tejunsho ? dir : null, project.SubtitleSegments);
        try
        {
            var completed = await _windows.RunWithProgressAsync(title, async (progress, ct) =>
            {
                var p = new Progress<(int Done, int Total)>(x => progress.Report(new ProgressInfo((double)x.Done / x.Total, $"画像を作成しています… {x.Done} / {x.Total}")));
                result = await _imageExport.ExportAsync(FFmpeg.FfmpegPath!, VideoPath, plan, overwrite, es.JpegQuality,
                    tejunsho || es.WriteMetadataJson, keep, p, ct, metadata);
            });
            if (!completed)
            {
                StatusMessage = $"{title}をキャンセルしました。";
                return;
            }
        }
        catch (FFmpegException ex)
        {
            _dialogs.ShowError(ex.Message, ex.ErrorDetail);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image export failed (tejunsho={Tejunsho})", tejunsho);
            _dialogs.ShowError($"{title}に失敗しました。", ex.Message);
            return;
        }

        if (!tejunsho)
        {
            es.ImageOutputDirectory = dir;
            Editor.SetOutputFilenames(result!.FileNames);
            StatusMessage = $"画像を書き出しました: {result.Written} 件（スキップ {result.Skipped} 件） → {dir}";
            if (_dialogs.Confirm($"{result.Written} 件の画像を書き出しました。{(result.Skipped > 0 ? $"（スキップ {result.Skipped} 件）" : "")}{Environment.NewLine}{dir}{Environment.NewLine}{Environment.NewLine}フォルダーを開きますか？", "完了"))
                _shell.OpenFolder(dir);
            return;
        }

        // 手順書用: 注釈の上書き保存の対象（outputFilename・imageOutputDirectory）は通常の画像の書き出しのまま変えない。
        bool metaWritten;
        try
        {
            metaWritten = TejunshoExport.WriteMetaIfMissing(dir, projectName, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var metaPath = Path.Combine(dir, TejunshoExport.MetaFileName);
            _logger.LogError(ex, "meta.json write failed: {Path}", metaPath);
            _dialogs.ShowError($"画像は書き出しましたが、meta.json を書けませんでした。{Environment.NewLine}{metaPath}", ex.Message);
            metaWritten = false;
        }
        if (!string.Equals(es.TejunshoOutputDirectory, dir, StringComparison.OrdinalIgnoreCase))
        {
            es.TejunshoOutputDirectory = dir;
            Editor.MarkDirty();
        }
        StatusMessage = $"手順書用に書き出しました: {result!.Written} 件（スキップ {result.Skipped} 件） → {dir}";
        _windows.ShowTejunshoExportDone(new TejunshoExportDone(dir, result.Written, result.Skipped, metaWritten, projectName));
    }

    private void MoveToRecycleBin(string dir, IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Path.Combine(dir, n),
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recycle failed: {File}", n);
            }
        }
    }
}
