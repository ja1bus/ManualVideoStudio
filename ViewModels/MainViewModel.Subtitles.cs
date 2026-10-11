using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;

namespace ManualVideoStudio.ViewModels;

/// <summary>タイムラインの字幕トラック表示用（出力時刻の字幕を元動画の時刻へ写した区間）。</summary>
public sealed record SubtitleTimelineItem(string Id, string Text, IReadOnlyList<TimeRange> SourceRanges);

/// <summary>
/// 手動字幕と編集段階（カット → 字幕）。字幕の時刻は出力動画（カット適用後）基準で持ち、
/// 画面の再生位置（元動画基準）とは EditTimelineCalculator で相互に変換する。
/// 字幕編集中はカットをロックし、再生時は削除される部分を飛ばして編集後の動画と同じ流れで見せる。
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<SubtitleItem> SubtitleItems { get; } = [];

    public IReadOnlyList<double> SubtitleFontSizes => SubtitleStyle.FontSizes;

    public bool IsSubtitlePhase => Project?.EditPhase == EditPhase.Subtitle;
    public bool IsCutPhase => HasProject && !IsSubtitlePhase;
    public bool IsCutLocked => Editor?.IsCutLocked ?? false;

    public string PhaseText => !HasProject ? ""
        : IsSubtitlePhase ? "Step 2  字幕編集中（カットは確定済み・変更不可）"
        : "Step 1  カット編集中";

    public string PhaseHint => !HasProject ? ""
        : IsSubtitlePhase ? "再生・移動は編集後の動画と同じ流れ（削除部分を飛ばす）です。I / O で表示区間を指定し、［字幕を追加］(T)・［図形を追加］(G) で入れます。"
        : "不要部分をカットしたら［カットを確定して字幕編集へ］を押してください。字幕は編集後の動画の時刻で付けます。";

    /// <summary>出力に残す区間（元動画基準）。</summary>
    private IReadOnlyList<TimeRange> KeepRanges => Editor?.KeepRanges ?? [];

    public double OutputDuration => Editor?.OutputDuration ?? 0;

    /// <summary>再生位置を出力動画の時刻にした値（削除部分では直後に残る位置）。</summary>
    public double CurrentOutputTime => HasProject ? EditTimelineCalculator.MapToOutputClamped(CurrentTime, KeepRanges) : 0;

    public string CurrentOutputTimeText => IsSubtitlePhase
        ? $"編集後 {TimeFormat.FormatShort(CurrentOutputTime)} / {TimeFormat.FormatShort(OutputDuration)}"
        : "";

    /// <summary>タイムラインの字幕トラック。</summary>
    public IReadOnlyList<SubtitleTimelineItem> TimelineSubtitles
    {
        get
        {
            if (Project is null) return [];
            var keep = KeepRanges;
            return Project.SubtitleSegments
                .Select(s => new SubtitleTimelineItem(s.Id, s.Text, EditTimelineCalculator.MapOutputRangeToSource(s.Start, s.End, keep)))
                .ToList();
        }
    }

    /// <summary>プレビューに重ねる字幕（現在位置で表示中のもの）。</summary>
    [ObservableProperty] private SubtitleSegment? _currentSubtitle;

    public int PreviewVideoWidth => Metadata?.Width ?? 0;
    public int PreviewVideoHeight => Metadata?.Height ?? 0;

    /// <summary>プレビューに渡す字幕の見た目（v1.5 からアプリ設定。変更のたびに別のインスタンスにして再描画させる）。</summary>
    public SubtitleStyle PreviewSubtitleStyle => Settings.Subtitle.Clone();

    // ---------------- 選択中の字幕の位置・サイズ（v1.5。主画面の字幕の欄） ----------------

    /// <summary>
    /// 主画面の字幕の欄で位置・サイズ・上下のずらしを変える対象。一覧で選んだ字幕。
    /// 再生・移動で表示される字幕が変わったら、その字幕を自動で選ぶ。
    /// </summary>
    [ObservableProperty] private SubtitleItem? _selectedSubtitleItem;

    private SubtitleSegment? SelectedSubtitle => SelectedSubtitleItem is { } i
        ? Project?.SubtitleSegments.FirstOrDefault(s => s.Id == i.Id) : null;

    public bool HasSelectedSubtitle => IsSubtitlePhase && SelectedSubtitle is not null;

    public string SelectedSubtitleText
    {
        get
        {
            if (SelectedSubtitle is not { } s) return "選択中: なし（一覧で選択）";
            var index = Project!.SubtitleSegments.IndexOf(s) + 1;
            var t = s.Text.Replace("\n", " ");
            return $"選択中: No.{index}「{(t.Length > 8 ? t[..8] + "…" : t)}」";
        }
    }

    /// <summary>選択中の字幕の位置（0 上 / 1 中央 / 2 下、選択なしは -1）。</summary>
    public int SelectedSubtitlePositionIndex => SelectedSubtitle is { } s ? (int)s.Position : -1;

    /// <summary>選択中の字幕の文字サイズ（ComboBox から変える）。</summary>
    public double SelectedSubtitleFontSize
    {
        get => SelectedSubtitle?.FontSize ?? Settings.Subtitle.FontSize;
        set
        {
            if (SelectedSubtitle is not { } s || s.FontSize == SubtitleStyle.NormalizeFontSize(value)) return;
            ApplySubtitleLayout(s, s.Position, value, s.VerticalOffset);
        }
    }

    /// <summary>上下のずらしの表示（段。0 は「ずらしなし」）。</summary>
    public string SelectedSubtitleOffsetText
    {
        get
        {
            var steps = (int)Math.Round((SelectedSubtitle?.VerticalOffset ?? 0) / SubtitleStyle.OffsetStep);
            return steps switch { > 0 => $"↑{steps}", < 0 => $"↓{-steps}", _ => "±0" };
        }
    }

    partial void OnSelectedSubtitleItemChanged(SubtitleItem? value)
    {
        SelectedSubtitleIds = value is null ? [] : [value.Id];
        // 未反映の入力を反映できず、利用者が「留まる」を選んだ場合は、選択を元の字幕へ戻す（v1.13）。
        if (_revertTo is { } back)
        {
            _revertTo = null;
            // 一覧（DataGrid）が選択を変えている最中に同じ値を書き戻すと、画面の選択が戻らない。UI のスレッドで少し遅らせて戻す（テストなど同期の文脈が無い場合はすぐ戻す）。
            void Revert()
            {
                _revertingSelection = true;
                try { SelectedSubtitleItem = back; }
                finally { _revertingSelection = false; }
                NotifySelectedSubtitle();
            }
            if (_uiContext is not null) _uiContext.Post(_ => Revert(), null);
            else Revert();
            return;
        }
        NotifySelectedSubtitle();
        if (!_revertingSelection) LoadSubtitleDraft();
    }

    private void NotifySelectedSubtitle()
    {
        OnPropertyChanged(nameof(HasSelectedSubtitle));
        OnPropertyChanged(nameof(SelectedSubtitleText));
        OnPropertyChanged(nameof(SelectedSubtitlePositionIndex));
        OnPropertyChanged(nameof(SelectedSubtitleFontSize));
        OnPropertyChanged(nameof(SelectedSubtitleOffsetText));
        OnPropertyChanged(nameof(HasSubtitleDraft));
    }

    /// <summary>選択中の字幕の位置（上 / 中央 / 下）を変える。</summary>
    [RelayCommand]
    private void SetSubtitlePosition(string? position)
    {
        if (SelectedSubtitle is not { } s || !Enum.TryParse<SubtitlePosition>(position, out var p)) return;
        ApplySubtitleLayout(s, p, s.FontSize, s.VerticalOffset);
    }

    /// <summary>選択中の字幕を上下に 1 段ずらす（"up" / "down"）。"reset" でずらしをなくす。</summary>
    [RelayCommand]
    private void NudgeSubtitle(string? direction)
    {
        if (SelectedSubtitle is not { } s) return;
        var offset = direction switch
        {
            "up" => s.VerticalOffset + SubtitleStyle.OffsetStep,
            "down" => s.VerticalOffset - SubtitleStyle.OffsetStep,
            _ => 0,
        };
        ApplySubtitleLayout(s, s.Position, s.FontSize, offset);
    }

    private void ApplySubtitleLayout(SubtitleSegment s, SubtitlePosition position, double fontSize, double offset)
    {
        if (Editor is null || !IsSubtitlePhase) return;
        var r = Editor.SetSubtitleLayout(s.Id, position, fontSize, offset);
        if (!r.Success) { StatusMessage = r.Message; return; }
        StatusMessage = "字幕の位置・サイズを変更しました（元に戻す: Ctrl+Z）。";
    }

    /// <summary>字幕の欄の［字幕の設定...］: 設定画面の字幕タブを開く。</summary>
    [RelayCommand]
    private Task ShowSubtitleSettingsAsync() => OpenSettingsAsync(SettingsTab.Subtitle);

    /// <summary>設定画面の字幕タブの確認画像に使う場面（再生位置と、選択中の字幕）。</summary>
    private SubtitleSampleContext SubtitleSampleForSettings()
    {
        if (Project is null || Metadata is null || !File.Exists(VideoPath)) return SubtitleSampleContext.Empty;
        var seg = SelectedSubtitle ?? CurrentSubtitle;
        double time = CurrentTime;
        if (seg is not null && !(CurrentSubtitle is { } c && c.Id == seg.Id))
        {
            // 選択中の字幕が再生位置に無い場合は、その字幕が出ている場面を使う。
            var mid = seg.Start + Math.Min(seg.Duration / 2, 1);
            time = EditTimelineCalculator.MapToSource(mid, KeepRanges);
        }
        return new SubtitleSampleContext(VideoPath, time, Metadata.Width, Metadata.Height, seg);
    }

    // ---------------- 編集段階 ----------------

    private bool CanConfirmCuts() => IsCutPhase;
    private bool CanReturnToCuts() => HasProject && IsSubtitlePhase;
    private bool CanEditSubtitles() => HasProject && IsSubtitlePhase;

    [RelayCommand(CanExecute = nameof(CanConfirmCuts))]
    private void ConfirmCuts()
    {
        if (Editor is null) return;
        var pending = PendingCandidateCount;
        var message = "カットを確定して字幕編集へ進みます。" + Environment.NewLine + Environment.NewLine
            + $"編集後の動画の長さ: {TimeFormat.Format(Editor.OutputDuration)}（カット区間 {Editor.Project.CutSegments.Count} 件）" + Environment.NewLine
            + "字幕編集中はカット（削除する区間）を変更できません。字幕を追加した後にカットを変更する場合は、字幕・図形を付け直す必要があります（現在の編集を残して別の編集を作ることもできます）。";
        if (pending > 0) message += Environment.NewLine + Environment.NewLine + $"未判断のカット候補が {pending} 件あります（候補は採用しない限り反映されません）。";
        // プロジェクトファイルがまだ無い（録画・動画から開いて一度も保存していない）場合は、進むときに保存する（v1.10）。
        var unsaved = ProjectPath is null;
        var firstSave = unsaved ? ProjectFolders.FirstSavePath(VideoPath, Settings.Recording.ResolveOutputDirectory()) : null;
        if (unsaved)
            message += Environment.NewLine + Environment.NewLine + (firstSave is not null
                ? "プロジェクトはまだ保存されていないため、字幕編集へ進むときに保存します。" + Environment.NewLine + $"保存先: {firstSave}"
                : "プロジェクトはまだ保存されていないため、字幕編集へ進むときに保存先を尋ねます。");
        if (!_dialogs.Confirm(message, "カットの確定")) return;
        PauseIfPlaying();
        MarkIn = null;
        MarkOut = null;
        var r = Editor.ConfirmCuts();
        if (!r.Success)
        {
            _dialogs.ShowWarning(r.Message);
            return;
        }
        SelectedTabIndex = TabSubtitles;
        const string next = "I / O で字幕の表示区間を指定し、［字幕を追加］(T) で文字を入れてください（元に戻す: Ctrl+Z）。";
        if (!unsaved)
        {
            StatusMessage = "カットを確定しました。" + next;
            return;
        }
        // 保存先を尋ねる画面で取り消した・保存に失敗した場合も、カットの確定はそのまま（保存の失敗は Save がエラーを表示する）。
        StatusMessage = Save(saveAs: false, firstSave)
            ? $"カットを確定し、プロジェクトを保存しました（{ProjectPath}）。" + next
            : "カットを確定しました。プロジェクトはまだ保存されていません。［ファイル］→［プロジェクトを保存］(Ctrl+S) で保存してください。";
    }

    [RelayCommand(CanExecute = nameof(CanReturnToCuts))]
    private void ReturnToCutEditing()
    {
        if (Editor is null) return;
        PauseIfPlaying();
        var count = Editor.Project.SubtitleSegments.Count;
        var shapes = Editor.Project.ShapeGroups.Count;
        if (count == 0 && shapes == 0)
        {
            var r = Editor.ReturnToCutEditing();
            if (!r.Success) { _dialogs.ShowWarning(r.Message); return; }
            MarkIn = null;
            MarkOut = null;
            SelectedTabIndex = TabCuts;
            StatusMessage = "カット編集へ戻りました。";
            return;
        }
        // 字幕・図形がある場合は、先に「現在の編集を残して、カットからやり直す」を勧める（v1.12）。破棄して戻る操作も残す。
        var nlc = Environment.NewLine;
        var keepAnswer = _dialogs.ConfirmYesNoCancel(
            $"カットを変更するには、字幕・図形を付け直す必要があります（時刻がカット後の動画基準のため）。{nlc}{nlc}"
            + $"［はい］現在の編集を残して、カットからやり直す{nlc}"
            + $"　　現在の編集（字幕 {count} 件・図形 {shapes} 件）をプロジェクトファイルに保存して残し、カットだけ引き継いだ別の編集を作って開きます。元の編集はプロジェクト一覧から開けます（元動画はコピーせず、同じものを使います）。{nlc}"
            + $"［いいえ］この編集の字幕・図形を破棄してカット編集へ戻る{nlc}"
            + $"　　別の編集は作らず、この編集の字幕・図形をなくします（直後なら［元に戻す］で戻せます）。{nlc}"
            + "［キャンセル］戻らない",
            "カットからやり直す");
        if (keepAnswer is null)
        {
            StatusMessage = shapes == 0 ? "カット編集へ戻るのを取りやめました（字幕はそのままです）。" : "カット編集へ戻るのを取りやめました（字幕・図形はそのままです）。";
            return;
        }
        if (keepAnswer == true)
        {
            _ = RestartFromCutsKeepingCurrentAsync();
            return;
        }
        // 図形（v1.6）も時刻が編集後の動画基準のため、字幕と一緒に破棄する。
        var what = shapes == 0 ? $"字幕: {count} 件" : $"字幕: {count} 件、図形: {shapes} 件";
        var ok = _windows.ConfirmDanger("カット編集へ戻る",
            (shapes == 0 ? ProjectEditor.SubtitlesExistMessage : ProjectEditor.SubtitlesOrShapesExistMessage) + Environment.NewLine + Environment.NewLine
            + $"破棄する{what}（直後であれば［元に戻す］(Ctrl+Z) で戻せます）",
            shapes == 0 ? "字幕をすべて破棄してカット編集へ戻る" : "字幕・図形をすべて破棄してカット編集へ戻る", "キャンセル");
        if (!ok)
        {
            StatusMessage = shapes == 0 ? "カット編集へ戻るのを取りやめました（字幕はそのままです）。" : "カット編集へ戻るのを取りやめました（字幕・図形はそのままです）。";
            return;
        }
        var result = Editor.DiscardSubtitlesAndReturnToCutEditing();
        if (!result.Success) { _dialogs.ShowWarning(result.Message); return; }
        MarkIn = null;
        MarkOut = null;
        SelectedTabIndex = TabCuts;
        StatusMessage = shapes == 0
            ? $"字幕 {count} 件を破棄してカット編集へ戻りました（元に戻す: Ctrl+Z）。"
            : $"字幕 {count} 件・図形 {shapes} 件を破棄してカット編集へ戻りました（元に戻す: Ctrl+Z）。";
    }

    /// <summary>字幕編集中にカット操作をしようとした場合の案内。</summary>
    private bool GuardCutLocked()
    {
        if (!IsCutLocked) return false;
        StatusMessage = Loc.Get("Status.CutLocked");
        return true;
    }

    // ---------------- 字幕 ----------------

    [RelayCommand(CanExecute = nameof(CanEditSubtitles))]
    private void AddSubtitle()
    {
        if (Editor is null) return;
        PauseIfPlaying();
        var keep = KeepRanges;
        double start = EditTimelineCalculator.MapToOutputClamped(MarkIn ?? CurrentTime, keep);
        double end = MarkOut is { } o ? EditTimelineCalculator.MapToOutputClamped(o, keep) : Math.Min(start + 3, OutputDuration);
        if (end <= start) end = Math.Min(start + 3, OutputDuration);
        var vm = SubtitleEditViewModel.ForNew(Editor, start, end, () => CurrentOutputTime);
        if (_windows.EditSubtitle(vm))
        {
            MarkIn = null;
            MarkOut = null;
            SelectedTabIndex = TabSubtitles;
            StatusMessage = "字幕を追加しました（元に戻す: Ctrl+Z）。";
        }
    }

    [RelayCommand]
    private void DeleteSubtitles(IList? items)
    {
        if (Editor is null || items is null || items.Count == 0 || !IsSubtitlePhase) return;
        var ids = items.OfType<SubtitleItem>().Select(i => i.Id).ToList();
        var r = Editor.RemoveSubtitles(ids);
        StatusMessage = r.Success ? $"字幕を {ids.Count} 件削除しました（元に戻す: Ctrl+Z）。" : r.Message;
    }

    [RelayCommand]
    private void SeekToSubtitle(SubtitleItem? item)
    {
        if (item is null) return;
        PauseIfPlaying();
        SeekTo(EditTimelineCalculator.MapToSource(item.Model.Start, KeepRanges));
    }

    private void RefreshSubtitles()
    {
        var p = Project;
        CollectionSync.Sync(SubtitleItems, p?.SubtitleSegments ?? [], i => i.Id, m => m.Id,
            m => new SubtitleItem(m), (item, m) => item.Update(m));
        if (SelectedSubtitleItem is { } sel && !SubtitleItems.Contains(sel)) SelectedSubtitleItem = null;
        // 位置・サイズの変更は同じ字幕のまま中身が変わるため、プレビューを描き直させる（一覧での選択は変えない）。
        var shown = CurrentSubtitle?.Id;
        _keepSubtitleSelection = true;
        try
        {
            CurrentSubtitle = null;
            UpdateCurrentSubtitle();
        }
        finally
        {
            _keepSubtitleSelection = false;
        }
        if (CurrentSubtitle is { } now && now.Id != shown) SelectShownSubtitle(now);
        NotifySelectedSubtitle();
        SyncSubtitleDraftAfterRefresh();
        OnPropertyChanged(nameof(IsSubtitlePhase));
        OnPropertyChanged(nameof(IsCutPhase));
        OnPropertyChanged(nameof(IsCutLocked));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(PhaseHint));
        OnPropertyChanged(nameof(OutputDuration));
        OnPropertyChanged(nameof(CurrentOutputTimeText));
        OnPropertyChanged(nameof(TimelineSubtitles));
        OnPropertyChanged(nameof(PreviewSubtitleStyle));
        OnPropertyChanged(nameof(PreviewVideoWidth));
        OnPropertyChanged(nameof(PreviewVideoHeight));
        ConfirmCutsCommand.NotifyCanExecuteChanged();
        ReturnToCutEditingCommand.NotifyCanExecuteChanged();
        OpenGeminiCaptionCommand.NotifyCanExecuteChanged();
        RestartFromCutsKeepingCurrentCommand.NotifyCanExecuteChanged();
        AddSubtitleCommand.NotifyCanExecuteChanged();
        AddRemoveCutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>再生位置の字幕をプレビューに出す（削除される位置では出さない）。</summary>
    private void UpdateCurrentSubtitle()
    {
        var p = Project;
        if (p is null || p.SubtitleSegments.Count == 0 || EditTimelineCalculator.IsRemoved(CurrentTime, KeepRanges))
        {
            CurrentSubtitle = null;
            return;
        }
        var t = CurrentOutputTime;
        CurrentSubtitle = p.SubtitleSegments.FirstOrDefault(s => t >= s.Start && t < s.End);
    }

    /// <summary>再生・移動で表示される字幕が変わったら、主画面の字幕の欄の対象にする。</summary>
    partial void OnCurrentSubtitleChanged(SubtitleSegment? value)
    {
        if (value is not null && !_keepSubtitleSelection) SelectShownSubtitle(value);
    }

    /// <summary>字幕一覧を更新する間は、表示中の字幕を選び直さない（一覧で選んだ字幕を保つ）。</summary>
    private bool _keepSubtitleSelection;

    private void SelectShownSubtitle(SubtitleSegment value)
    {
        if (SelectedSubtitleItem?.Id == value.Id) return;
        if (HasSubtitleDraft) return; // 入力中は、再生で表示される字幕に選択を移さない（入力を失わせない）
        if (SubtitleItems.FirstOrDefault(i => i.Id == value.Id) is { } item) SelectedSubtitleItem = item;
    }

    /// <summary>
    /// 字幕編集中、またはカット編集中に［カットを反映して再生］を選んでいるとき（v1.17.0）、および試し再生・つなぎ目の再生中は、
    /// 再生で削除される部分に入ったら次に残る位置へ飛ばす（出力動画と同じ流れで確認できるように）。
    /// 確定カットの削除部分と短縮で残さない部分だけが対象で、未採用の候補は飛ばさない（試し再生を除く）。飛ばした場合は true。
    /// </summary>
    private bool SkipRemovedWhilePlaying(double sourceTime)
    {
        if (_player is not { IsPlaying: true } player) return false;
        var (active, keep) = PlaybackSkipPlan();
        if (!active) return false;
        if (!EditTimelineCalculator.IsRemoved(sourceTime, keep)) return false;
        if (EditTimelineCalculator.NextKeepStart(sourceTime, keep) is { } next)
        {
            player.Position = next;
            CurrentTime = next;
        }
        else
        {
            // 最後に残る区間より後は出力に含まれないため、そこで止める。
            player.Pause();
            IsPlaying = false;
            var end = keep.Count > 0 ? keep[^1].End : Duration;
            player.Position = end;
            CurrentTime = Math.Round(end, 3);
        }
        return true;
    }

    public const int TabSubtitles = 2;
}
