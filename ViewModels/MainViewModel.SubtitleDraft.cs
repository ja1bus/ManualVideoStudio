using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 選択中の字幕の文章・開始/終了時刻の編集（v1.13。右側の編集欄）。入力は「下書き」として持ち、
/// ［変更を反映］・入力欄を離れたとき・Enter・保存や書き出し・別の字幕の選択の直前に、既存の字幕の検証（ProjectEditor.UpdateSubtitle）を通して反映する。
/// 反映の 1 回が元に戻す 1 回。反映できない入力は黙って捨てず、エラーを出して入力を残すか、利用者が破棄を選んだときだけ捨てる。
/// 位置・サイズ・上下の調整は従来どおりその場で反映する（SetSubtitleLayout）。
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string _subtitleDraftText = "";
    [ObservableProperty] private string _subtitleDraftStart = "";
    [ObservableProperty] private string _subtitleDraftEnd = "";
    [ObservableProperty] private string _subtitleDraftError = "";

    // 下書きを読み込んだ時点の値（下書きが編集されたかの判定用）と、その字幕の ID。
    private string? _draftId;
    private string _baseText = "", _baseStart = "", _baseEnd = "";
    private bool _committingDraft;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private bool _revertingSelection;
    private SubtitleItem? _revertTo;

    partial void OnSubtitleDraftTextChanged(string value) => OnPropertyChanged(nameof(HasSubtitleDraft));
    partial void OnSubtitleDraftStartChanged(string value) => OnPropertyChanged(nameof(HasSubtitleDraft));
    partial void OnSubtitleDraftEndChanged(string value) => OnPropertyChanged(nameof(HasSubtitleDraft));

    /// <summary>下書きが、選択中の字幕の現在の内容と違う（未反映の入力がある）。</summary>
    public bool HasSubtitleDraft => SelectedSubtitle is { } s
        && (SubtitleValidator.NormalizeText(SubtitleDraftText) != s.Text
            || SubtitleDraftStart != TimeFormat.Format(s.Start)
            || SubtitleDraftEnd != TimeFormat.Format(s.End));

    private bool DraftEditedSinceLoad => SubtitleDraftText != _baseText || SubtitleDraftStart != _baseStart || SubtitleDraftEnd != _baseEnd;

    /// <summary>選択中の字幕の内容を下書きに読み込む（未反映の入力は捨てる）。</summary>
    private void LoadSubtitleDraft()
    {
        var s = SelectedSubtitle;
        _draftId = s?.Id;
        _baseText = s?.Text ?? "";
        _baseStart = s is null ? "" : TimeFormat.Format(s.Start);
        _baseEnd = s is null ? "" : TimeFormat.Format(s.End);
        SubtitleDraftText = _baseText;
        SubtitleDraftStart = _baseStart;
        SubtitleDraftEnd = _baseEnd;
        SubtitleDraftError = "";
        OnPropertyChanged(nameof(HasSubtitleDraft));
    }

    /// <summary>編集・Undo/Redo で字幕の内容が変わったあとの下書きの扱い。入力中（未反映）の下書きは残し、そうでなければ読み込み直す。</summary>
    private void SyncSubtitleDraftAfterRefresh()
    {
        var s = SelectedSubtitle;
        if (!_committingDraft && s is not null && s.Id == _draftId && DraftEditedSinceLoad)
        {
            _baseText = s.Text;
            _baseStart = TimeFormat.Format(s.Start);
            _baseEnd = TimeFormat.Format(s.End);
            OnPropertyChanged(nameof(HasSubtitleDraft));
            return;
        }
        LoadSubtitleDraft();
    }

    /// <summary>下書きを字幕へ反映する。反映するものが無ければ true。反映できなければ false（理由は error）。</summary>
    private bool TryApplySubtitleDraft(out string error)
    {
        error = "";
        if (Editor is null || !IsSubtitlePhase || SelectedSubtitle is not { } s || !HasSubtitleDraft) return true;
        if (!TimeFormat.TryParse(SubtitleDraftStart, out var start)) { error = "開始位置を HH:MM:SS.mmm 形式で入力してください。"; return false; }
        if (!TimeFormat.TryParse(SubtitleDraftEnd, out var end)) { error = "終了位置を HH:MM:SS.mmm 形式で入力してください。"; return false; }
        _committingDraft = true;
        try
        {
            var r = Editor.UpdateSubtitle(s.Id, start, end, SubtitleDraftText, s.Position, s.FontSize);
            if (!r.Success) { error = r.Message; return false; }
        }
        finally
        {
            _committingDraft = false;
        }
        // 変更がなく（時刻の書き方だけの違い）Changed が起きない場合も、下書きを正規の表記にそろえる。
        LoadSubtitleDraft();
        StatusMessage = Loc.Get("SubEdit.Applied");
        return true;
    }

    /// <summary>保存・書き出し・プロジェクトの切り替えの前に、入力中の字幕を反映する（反映できなければエラーを出して false）。</summary>
    private bool FlushSubtitleDraft()
    {
        if (!HasSubtitleDraft) return true;
        if (TryApplySubtitleDraft(out var error)) return true;
        SubtitleDraftError = Loc.Format("SubEdit.CannotApply", error);
        StatusMessage = SubtitleDraftError;
        return false;
    }

    [RelayCommand]
    private void ApplySubtitleDraft()
    {
        if (!TryApplySubtitleDraft(out var error)) SubtitleDraftError = Loc.Format("SubEdit.CannotApply", error);
    }

    [RelayCommand]
    private void RevertSubtitleDraft() => LoadSubtitleDraft();

    /// <summary>別の字幕を選ぶ前に、入力中の内容を反映する。反映できないときは、破棄して移るか、いまの字幕に留まるかを選ばせる。</summary>
    partial void OnSelectedSubtitleItemChanging(SubtitleItem? oldValue, SubtitleItem? newValue)
    {
        if (_revertingSelection || oldValue is null || oldValue.Id != _draftId || !HasSubtitleDraft) return;
        if (TryApplySubtitleDraft(out var error)) return;
        if (!_dialogs.Confirm(Loc.Format("SubEdit.DiscardConfirm", error), Loc.Get("SubEdit.DiscardTitle"))) _revertTo = oldValue;
    }
}
