using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Export;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Imaging;
using Microsoft.Extensions.Logging;

namespace ManualVideoStudio.ViewModels;

/// <summary>タイムラインの図形トラック表示用（出力時刻のまとまりを元動画の時刻へ写した区間。v1.6）。</summary>
public sealed record ShapeTimelineItem(string Id, string Label, bool HasMosaic, IReadOnlyList<TimeRange> SourceRanges);

/// <summary>
/// 動画に重ねる図形（v1.6）。四角形・矢印・直線・文字・モザイクを、表示時間を持つ「まとまり」で入れる。
/// 時刻は字幕と同じく出力動画（カット適用後）基準で、字幕編集中（カット確定後）だけ編集できる。
/// 編集は手順書画像の注釈編集画面を使い、背景は元動画から切り出した 1 コマ（注釈なし）。
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ShapeGroupItem> ShapeItems { get; } = [];

    /// <summary>タイムラインの図形トラック。</summary>
    public IReadOnlyList<ShapeTimelineItem> TimelineShapes
    {
        get
        {
            if (Project is null) return [];
            var keep = KeepRanges;
            return Project.ShapeGroups
                .Select(g => new ShapeTimelineItem(g.Id, g.Summary, g.HasMosaic, EditTimelineCalculator.MapOutputRangeToSource(g.Start, g.End, keep)))
                .ToList();
        }
    }

    /// <summary>プレビューに重ねる図形（現在位置で表示中のまとまり）。</summary>
    [ObservableProperty] private IReadOnlyList<ShapeGroup> _currentShapeGroups = [];

    private string _currentShapeKey = "";

    private void RefreshShapes()
    {
        CollectionSync.Sync(ShapeItems, Project?.ShapeGroups ?? [], i => i.Id, m => m.Id,
            m => new ShapeGroupItem(m), (item, m) => item.Update(m));
        OnPropertyChanged(nameof(TimelineShapes));
        LoadShapeDraft(force: false);
        AddShapesCommand.NotifyCanExecuteChanged();
        // 図形の中身が変わっても同じまとまりのため、プレビューを描き直させる。
        UpdateCurrentShapes(force: true);
    }

    /// <summary>再生位置の図形をプレビューに出す（削除される位置では出さない）。</summary>
    private void UpdateCurrentShapes(bool force = false)
    {
        var p = Project;
        IReadOnlyList<ShapeGroup> now = [];
        if (p is not null && p.ShapeGroups.Count > 0 && !EditTimelineCalculator.IsRemoved(CurrentTime, KeepRanges))
        {
            var t = CurrentOutputTime;
            now = p.ShapeGroups.Where(g => t >= g.Start && t < g.End).ToList();
        }
        var key = string.Join(",", now.Select(g => g.Id));
        if (!force && key == _currentShapeKey) return;
        _currentShapeKey = key;
        CurrentShapeGroups = now;
    }

    // ---------------- 追加・編集・削除 ----------------

    [RelayCommand(CanExecute = nameof(CanEditSubtitles))]
    private async Task AddShapesAsync()
    {
        if (Editor is null) return;
        PauseIfPlaying();
        var keep = KeepRanges;
        var output = Editor.OutputDuration;
        double start, end;
        if (MarkIn is not null || MarkOut is not null)
        {
            // I / O で指定した区間（字幕と同じ）。
            start = EditTimelineCalculator.MapToOutputClamped(MarkIn ?? CurrentTime, keep);
            end = MarkOut is { } o ? EditTimelineCalculator.MapToOutputClamped(o, keep) : Math.Min(start + 3, output);
        }
        else if (CurrentSubtitle is { } sub)
        {
            // 再生位置に字幕が出ていれば、その字幕と同じ時間（説明と図形を一緒に出すことが多いため）。
            start = sub.Start;
            end = sub.End;
        }
        else
        {
            start = CurrentOutputTime;
            end = Math.Min(start + 3, output);
        }
        if (end <= start) end = Math.Min(start + 3, output);
        if (end <= start)
        {
            // 動画の最後で追加した場合は、最後の 3 秒にする。
            start = Math.Max(0, output - 3);
            end = output;
        }
        var frameOutput = Math.Clamp(CurrentOutputTime, start, Math.Max(start, end - 0.001));
        if (await OpenShapeEditorAsync(null, start, end, frameOutput))
        {
            MarkIn = null;
            MarkOut = null;
            SelectedTabIndex = TabShapes;
            StatusMessage = "図形を追加しました（元に戻す: Ctrl+Z）。タイムラインの赤紫の段に表示時間が出ます。";
        }
    }

    [RelayCommand]
    private async Task EditShapesAsync(ShapeGroupItem? item)
    {
        if (Editor is null || item is null || !IsSubtitlePhase) return;
        PauseIfPlaying();
        var g = Editor.Project.ShapeGroups.FirstOrDefault(x => x.Id == item.Id);
        if (g is null) return;
        // 再生位置が表示時間内ならその画面、外なら表示時間の最初のあたりの画面を背景にする。
        var t = CurrentOutputTime;
        var frameOutput = t >= g.Start && t < g.End ? t : g.Start + Math.Min(g.Duration / 2, 1);
        if (await OpenShapeEditorAsync(g, g.Start, g.End, frameOutput))
            StatusMessage = Editor.Project.ShapeGroups.Any(x => x.Id == g.Id)
                ? "図形を変更しました（元に戻す: Ctrl+Z）。"
                : "図形がすべて無くなったため、まとまりを削除しました（元に戻す: Ctrl+Z）。";
    }

    [RelayCommand]
    private void DeleteShapes(IList? items)
    {
        if (Editor is null || items is null || items.Count == 0 || !IsSubtitlePhase) return;
        var ids = items.OfType<ShapeGroupItem>().Select(i => i.Id).ToList();
        var r = Editor.RemoveShapeGroups(ids);
        StatusMessage = r.Success ? $"図形を {ids.Count} 件削除しました（元に戻す: Ctrl+Z）。" : r.Message;
    }

    [RelayCommand]
    private void SeekToShapes(ShapeGroupItem? item)
    {
        if (item is null) return;
        PauseIfPlaying();
        SeekTo(EditTimelineCalculator.MapToSource(item.Model.Start, KeepRanges));
    }

    /// <summary>
    /// 図形の編集画面を開く。背景は編集後の動画の frameOutput 秒の画面（元動画から切り出す）。反映したら true。
    /// </summary>
    private async Task<bool> OpenShapeEditorAsync(ShapeGroup? existing, double start, double end, double frameOutput)
    {
        if (Editor is null) return false;
        if (!FFmpeg.IsAvailable || FFmpeg.FfmpegPath is not { } ffmpeg || !File.Exists(VideoPath))
        {
            _dialogs.ShowWarning("図形の編集には元動画と FFmpeg が必要です（背景にする画面を元動画から切り出すため）。");
            return false;
        }
        BitmapSource baseImage;
        string? loading = null;
        try
        {
            StatusMessage = "図形を描く画面を準備しています…";
            var source = EditTimelineCalculator.MapToSource(frameOutput, KeepRanges);
            loading = await _frames.GetFullFrameAsync(ffmpeg, VideoPath, source, CancellationToken.None);
            baseImage = ImageFiles.Load(loading);
        }
        catch (FFmpegException ex)
        {
            StatusMessage = "";
            _dialogs.ShowError(ex.Message, ex.ErrorDetail);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
        {
            StatusMessage = "";
            _logger.LogError(ex, "Shape base image failed: {Path}", loading);
            _dialogs.ShowError(loading is null ? "画面を読み込めませんでした。" : FileReadProblem.Message("画面を読み込めませんでした。", loading), ex.Message);
            return false;
        }
        StatusMessage = "";

        var id = existing?.Id;
        var committed = false;
        var request = new VideoShapeEditRequest(
            existing is null ? "動画の図形 - 追加" : $"動画の図形 - 編集（{existing.Summary}）",
            baseImage,
            existing?.Shapes ?? [], existing?.ImageWidth ?? 0, existing?.ImageHeight ?? 0,
            start, end, Editor.OutputDuration,
            $"背景: 編集後 {TimeFormat.FormatShort(frameOutput)} の画面。図形は画面の動きについて行きません（その位置に表示し続けます）。",
            (shapes, w, h, s, e) =>
            {
                var r = id is null ? Editor.AddShapeGroup(s, e, shapes, w, h).Result : Editor.UpdateShapeGroup(id, s, e, shapes, w, h);
                committed |= r.Success;
                return r;
            });
        var vm = new ImageEditorViewModel(request, _dialogs, _windows, _logger);
        _windows.EditImage(vm);
        return committed;
    }

    public const int TabShapes = 3;
}
