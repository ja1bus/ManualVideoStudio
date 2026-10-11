using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 「選択中の図形」の欄（v1.17.0）。図形の表示する時間（編集後の時刻）を、字幕の欄と同じ方法で直す:
/// 再生位置を動かして［現在位置］で入力欄に入れ、［変更を反映］で確定する（1 回の元に戻す操作で戻せる）。図形の描き直しは［図形を編集…］。
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _shapeDraftStart = "";
    [ObservableProperty] private string _shapeDraftEnd = "";
    [ObservableProperty] private string _shapeDraftError = "";

    private string _shapeBaseStart = "", _shapeBaseEnd = "";
    private string? _shapeDraftId;

    /// <summary>欄の対象の図形（一覧・タイムラインで 1 つだけ選んでいるとき）。</summary>
    private ShapeGroup? SelectedShapeGroup => IsSubtitlePhase && SelectedShapeIds.Count == 1
        ? Project?.ShapeGroups.FirstOrDefault(g => g.Id == SelectedShapeIds[0]) : null;

    public bool HasSelectedShape => SelectedShapeGroup is not null;
    public bool HasShapeDraft => HasSelectedShape && (ShapeDraftStart != _shapeBaseStart || ShapeDraftEnd != _shapeBaseEnd);

    partial void OnShapeDraftStartChanged(string value) => OnPropertyChanged(nameof(HasShapeDraft));
    partial void OnShapeDraftEndChanged(string value) => OnPropertyChanged(nameof(HasShapeDraft));
    partial void OnSelectedShapeIdsChanged(IReadOnlyList<string> value) => LoadShapeDraft(force: true);

    /// <summary>選択中の図形の時刻を入力欄へ読み込む。force でなければ、編集中の入力は上書きしない。</summary>
    private void LoadShapeDraft(bool force)
    {
        var g = SelectedShapeGroup;
        if (!force && g is not null && g.Id == _shapeDraftId && HasShapeDraft) { OnPropertyChanged(nameof(HasSelectedShape)); return; }
        _shapeDraftId = g?.Id;
        _shapeBaseStart = g is null ? "" : TimeFormat.Format(g.Start);
        _shapeBaseEnd = g is null ? "" : TimeFormat.Format(g.End);
        ShapeDraftStart = _shapeBaseStart;
        ShapeDraftEnd = _shapeBaseEnd;
        ShapeDraftError = "";
        OnPropertyChanged(nameof(HasSelectedShape));
        OnPropertyChanged(nameof(HasShapeDraft));
    }

    [RelayCommand] private void ShapeDraftStartFromCurrent() => ShapeDraftStart = TimeFormat.Format(CurrentOutputTime);
    [RelayCommand] private void ShapeDraftEndFromCurrent() => ShapeDraftEnd = TimeFormat.Format(CurrentOutputTime);

    [RelayCommand]
    private void RevertShapeDraft() => LoadShapeDraft(force: true);

    /// <summary>入力中の図形の時間を反映する。反映するものが無ければ true。反映できなければ false（理由は error）。</summary>
    private bool TryApplyShapeDraft(out string error)
    {
        error = "";
        if (Editor is null || SelectedShapeGroup is not { } g || !HasShapeDraft) return true;
        if (!TimeFormat.TryParse(ShapeDraftStart, out var start)) { error = "開始位置を HH:MM:SS.mmm 形式で入力してください。"; return false; }
        if (!TimeFormat.TryParse(ShapeDraftEnd, out var end)) { error = "終了位置を HH:MM:SS.mmm 形式で入力してください。"; return false; }
        var r = Editor.UpdateShapeGroup(g.Id, start, end, g.Shapes, g.ImageWidth, g.ImageHeight);
        if (!r.Success) { error = r.Message; return false; }
        LoadShapeDraft(force: true);
        StatusMessage = Loc.Get("ShapeEdit.Applied");
        return true;
    }

    /// <summary>保存・書き出し・終了の前に、入力中の図形の時間を反映する（反映できなければエラーを出して false）。</summary>
    private bool FlushShapeDraft()
    {
        if (!HasShapeDraft) return true;
        if (TryApplyShapeDraft(out var error)) return true;
        ShapeDraftError = Loc.Format("SubEdit.CannotApply", error);
        StatusMessage = ShapeDraftError;
        return false;
    }

    [RelayCommand]
    private void ApplyShapeDraft()
    {
        if (!TryApplyShapeDraft(out var error)) ShapeDraftError = Loc.Format("SubEdit.CannotApply", error);
        else ShapeDraftError = "";
    }

    /// <summary>
    /// 別の図形を選ぶ前に、入力中の時間を反映する。反映できないときは、破棄して移るか、いまの図形に留まるかを選ばせる
    /// （留まるときは、一覧の選択をいまの図形へ戻す）。
    /// </summary>
    public void SetSelectedShapes(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (_shapeDraftId is { } old && HasShapeDraft && !(list.Count == 1 && list[0] == old))
        {
            if (!TryApplyShapeDraft(out var error)
                && !_dialogs.Confirm(Loc.Format("ShapeEdit.DiscardConfirm", error), Loc.Get("ShapeEdit.DiscardTitle")))
            {
                ListSelectionRequested?.Invoke(ListKind.Shapes, [old]);
                return;
            }
        }
        SelectedShapeIds = list;
    }
}
