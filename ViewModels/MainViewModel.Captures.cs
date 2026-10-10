using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Annotations;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Imaging;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>手順書画像（CapturePoint）の登録・編集。画像の位置は人間が決め、AI はキャプションだけを補助する。</summary>
public sealed partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanUseFullFeatures))]
    private async Task AddCaptureAsync()
    {
        if (Editor is null) return;
        PauseIfPlaying();
        var (result, point) = Editor.AddCapture(CurrentTime);
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        // 作業領域は切り替えない（カット・字幕の編集中に C キーで画像を追加しても、いまの画面のまま）。一覧は［画像作成］で見る（v1.13）。
        StatusMessage = Loc.Format("Status.CapturePoint", point!.Sequence, TimeFormat.Format(point.SelectedFrameTime));
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void DeleteCapture(CapturePointItem? item)
    {
        if (Editor is null || item is null) return;
        Editor.RemoveCapture(item.Id);
        StatusMessage = $"画像ポイント No.{item.Sequence} を削除しました（元に戻す: Ctrl+Z）。";
    }

    [RelayCommand]
    private void SeekToCapture(CapturePointItem? item)
    {
        if (item is null) return;
        PauseIfPlaying();
        SeekTo(item.SelectedFrameTime);
    }

    /// <summary>画像の時刻を現在の再生位置に変更する。</summary>
    [RelayCommand]
    private void SetCaptureToCurrent(CapturePointItem? item)
    {
        if (Editor is null || item is null) return;
        var r = Editor.SetSelectedFrameTime(item.Id, CurrentTime);
        StatusMessage = r.Success ? $"No.{item.Sequence} の画像時刻を {TimeFormat.Format(CurrentTime)} にしました。" : r.Message;
    }

    /// <summary>前後フレーム候補を比較して SelectedFrameTime を選ぶ。</summary>
    [RelayCommand]
    private void CompareFrames(CapturePointItem? item)
    {
        if (Editor is null || item is null || !EnsureFFmpeg()) return;
        var point = Editor.Project.CapturePoints.FirstOrDefault(p => p.Id == item.Id);
        if (point is null) return;
        PauseIfPlaying();
        var fc = Settings.FrameCandidates;
        var vm = new FrameCandidatesViewModel(_frames, FFmpeg.FfmpegPath!, VideoPath, Duration, point, fc.StepSeconds, fc.CountEachSide);
        var selected = _windows.ChooseFrame(vm);
        if (selected is not { } t) return;
        var r = Editor.SetSelectedFrameTime(item.Id, t);
        if (!r.Success) _dialogs.ShowWarning(r.Message);
        else
        {
            SeekTo(t);
            StatusMessage = $"No.{item.Sequence} の画像時刻を {TimeFormat.Format(t)} にしました。";
        }
    }

    /// <summary>
    /// 手順書画像の注釈編集（四角形・矢印・文字・モザイク）。元画像は元動画から切り出した注釈なしのフレームを使い、
    /// 保存時に注釈を焼き込んで書き出し済みの画像ファイルへ上書きする（ファイル名・形式は変えない）。
    /// </summary>
    [RelayCommand]
    private async Task EditAnnotationsAsync(CapturePointItem? item)
    {
        if (Editor is null || item is null) return;
        var point = Editor.Project.CapturePoints.FirstOrDefault(p => p.Id == item.Id);
        if (point is null) return;
        PauseIfPlaying();
        var es = Editor.Project.ExportSettings;
        var output = AnnotatedImageFiles.OutputPath(point, VideoPath, es, ProjectPath);
        if (output is not null && !File.Exists(output)) output = null;
        if (output is not null && AnnotatedImageFiles.FormatOf(output) is null)
        {
            _dialogs.ShowWarning($"PNG / JPEG 以外の画像は編集できません。{Environment.NewLine}{output}");
            return;
        }

        BitmapSource baseImage;
        string? loading = null;
        try
        {
            StatusMessage = $"No.{point.Sequence} の画像を準備しています…";
            if (FFmpeg.IsAvailable && FFmpeg.FfmpegPath is { } ffmpeg && File.Exists(VideoPath))
            {
                // 注釈なしの元画像は毎回元動画から切り出す（保存済みの画像には注釈が焼き込まれているため使わない）。
                var path = await _frames.GetFullFrameAsync(ffmpeg, VideoPath, point.SelectedFrameTime, CancellationToken.None);
                loading = path;
                baseImage = ImageFiles.Load(path);
            }
            else if (output is not null && point.Annotations.Count == 0)
            {
                // 動画・FFmpeg が無くても、まだ注釈の無い書き出し済み画像なら編集できる。
                loading = output;
                baseImage = ImageFiles.Load(output);
            }
            else
            {
                _dialogs.ShowWarning(point.Annotations.Count > 0
                    ? "注釈の編集には元動画と FFmpeg が必要です（保存済みの画像には注釈が焼き込まれているため、注釈なしの画像を元動画から作り直します）。"
                    : "画像がまだ書き出されていません。元動画と FFmpeg を使えるようにするか、先に［画像を書き出し］を行ってください。");
                StatusMessage = "";
                return;
            }
        }
        catch (FFmpegException ex)
        {
            _dialogs.ShowError(ex.Message, ex.ErrorDetail);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
        {
            _logger.LogError(ex, "Annotation base image failed: {Path}", loading);
            _dialogs.ShowError(loading is null ? "画像を読み込めませんでした。" : FileReadProblem.Message("画像を読み込めませんでした。", loading), ex.Message);
            return;
        }

        var format = output is not null ? AnnotatedImageFiles.FormatOf(output)!.Value : es.ImageFormat;
        var id = point.Id;
        var request = new ImageEditorRequest(JsonClone(point), baseImage, output, format, es.JpegQuality,
            (annotations, w, h) =>
            {
                var r = Editor.SetAnnotations(id, annotations, w, h);
                if (r.Success && output is not null) _annotatedImagesSinceSave = true;
                return r;
            });
        var vm = new ImageEditorViewModel(request, _dialogs, _windows, _logger);
        _windows.EditImage(vm);
        var saved = Editor.Project.CapturePoints.FirstOrDefault(p => p.Id == id);
        StatusMessage = saved is null ? "" : $"No.{saved.Sequence} の注釈: {saved.Annotations.Count} 件{(output is null ? "（画像の書き出し時に反映）" : "")}";
    }

    /// <summary>プロジェクトの保存後に、注釈を画像ファイルへ上書き保存したか（未保存で閉じるときの注意表示用）。</summary>
    private bool _annotatedImagesSinceSave;

    private static CapturePoint JsonClone(CapturePoint p) => Core.Persistence.JsonDefaults.Clone(p);

    /// <summary>キャプション入力欄の確定（フォーカス移動・Enter）。</summary>
    public void CommitCaption(CapturePointItem item, string text)
    {
        if (Editor is null) return;
        var r = Editor.SetCaption(item.Id, text);
        if (!r.Success) StatusMessage = r.Message;
    }

    /// <summary>手順書の文（v1.9）の確定。field は "step"（操作文）/ "note"（補足）/ "chapter"（章名）。</summary>
    public void CommitCaptureText(CapturePointItem item, string field, string text)
    {
        if (Editor is null) return;
        var r = field switch
        {
            "step" => Editor.SetStepText(item.Id, text),
            "note" => Editor.SetNote(item.Id, text),
            "chapter" => Editor.SetChapterTitle(item.Id, text),
            _ => EditResult.Fail($"不明な項目です: {field}"),
        };
        if (!r.Success) StatusMessage = r.Message;
    }

    private async Task LoadThumbnailAsync(CapturePointItem item)
    {
        var ffmpeg = FFmpeg.FfmpegPath;
        if (ffmpeg is null || string.IsNullOrEmpty(VideoPath)) return;
        var time = item.SelectedFrameTime;
        item.ThumbnailTime = time;
        try
        {
            var path = await _frames.GetThumbnailAsync(ffmpeg, VideoPath, time, Math.Clamp(Settings.FrameCandidates.ThumbnailWidth, 80, 640), CancellationToken.None);
            if (item.ThumbnailTime == time)
            {
                item.Thumbnail = ImageLoader.Load(path);
                item.ThumbnailFailed = false;
            }
        }
        catch (Exception ex)
        {
            item.ThumbnailFailed = true;
            _logger.LogWarning(ex, "Thumbnail failed at {Time}", time);
        }
    }
}
