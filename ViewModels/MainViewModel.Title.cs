using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>動画冒頭の黒背景タイトル（v1.15）。共通バーの［タイトル...］から専用の画面で編集する。</summary>
public sealed partial class MainViewModel
{
    /// <summary>タイトルがあるか（文章が空でない）。ボタンの色・文言の切り替えに使う。</summary>
    public bool HasTitle => Project?.Title.HasTitle ?? false;

    /// <summary>ボタンの文言。色だけでなくチェックと文言でも状態を示す。</summary>
    public string TitleButtonText => Loc.Get(HasTitle ? "Toolbar.Title.On" : "Toolbar.Title");

    /// <summary>ボタンのツールチップ。タイトルがあるときは文章・表示秒数・フェードの有無を出す。</summary>
    public string TitleButtonTip => Project?.Title is { HasTitle: true } t
        ? Loc.Format("Toolbar.Title.TipOn", t.NormalizedText, VideoTitle.NormalizeSeconds(t.Seconds), Loc.Get(t.FadeIn ? "Title.FadeOn" : "Title.FadeOff"))
        : Loc.Get("Toolbar.Title.Tip");

    private void NotifyTitle()
    {
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(TitleButtonText));
        OnPropertyChanged(nameof(TitleButtonTip));
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void EditTitle()
    {
        if (Editor is null) return;
        PauseIfPlaying();
        if (!FlushSubtitleDraft()) return;
        var before = Editor.History.UndoCount;
        var vm = new TitleEditViewModel(Editor, Metadata?.Width ?? 0, Metadata?.Height ?? 0);
        if (!_windows.EditTitle(vm)) return;
        var t = Editor.Project.Title;
        if (Editor.History.UndoCount == before) return; // 変更なし
        StatusMessage = t.HasTitle
            ? Loc.Format("Status.TitleSet", VideoTitle.NormalizeSeconds(t.Seconds))
            : Loc.Get("Status.TitleRemoved");
    }
}
