using CommunityToolkit.Mvvm.Input;
using ManualVideoStudio.Core.Editing;
using ManualVideoStudio.Core.Models;

namespace ManualVideoStudio.ViewModels;

/// <summary>
/// 手動カットを速くするキー操作（v1.21.0。設計 3.1）。MainWindow の PreviewKeyDown から呼ぶ RelayCommand で、
/// 後から別の入力機器（Speed Editor など）にも同じコマンドを割り当てられるようにしている。
/// R（片方だけの指定はもう片方を再生位置にする）、Q / W（カットの端を再生位置に合わせる）、
/// A / D / P / N / Shift+N（選択中の候補の操作）、M / Shift+M（録画時のマーカーへ移動）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>この長さ未満の範囲は、R でカットしない（開始と終了がほぼ同じ位置）。</summary>
    private const double QuickCutMinSeconds = 0.05;

    // ---------------- R ----------------

    /// <summary>
    /// R: 開始・終了の両方を指定していればその区間、片方だけなら、もう片方を再生位置にした区間をカットする。
    /// カット編集中は通常のカット、字幕編集中は字幕・図形の時刻を合わせて直す追加カット（確認あり）。
    /// </summary>
    [RelayCommand]
    private void QuickCut()
    {
        if (Editor is null) return;
        if (!TryResolveMarkedRange(out var start, out var end, out var error))
        {
            StatusMessage = error;
            return;
        }
        if (IsCutLocked)
        {
            CutInSubtitlePhaseRange(start, end);
            return;
        }
        PauseIfPlaying();
        AddCutRange(start, end);
    }

    /// <summary>開始・終了の指定から区間を決める（片方だけなら、もう片方は再生位置）。決まらなければ理由を返す。</summary>
    private bool TryResolveMarkedRange(out double start, out double end, out string error)
    {
        start = end = 0;
        error = "";
        if (MarkIn is null && MarkOut is null)
        {
            error = Loc.Get("CutKey.NoMarks");
            return false;
        }
        double a = MarkIn ?? CurrentTime, b = MarkOut ?? CurrentTime;
        start = Math.Min(a, b);
        end = Math.Max(a, b);
        if (end - start < QuickCutMinSeconds)
        {
            error = Loc.Get("CutKey.RangeTooShort");
            return false;
        }
        return true;
    }

    // ---------------- Q / W ----------------

    /// <summary>Q: カットの開始を再生位置に合わせる。</summary>
    [RelayCommand]
    private void AlignCutStart() => AlignCutEdge(CutEdge.Start);

    /// <summary>W: カットの終了を再生位置に合わせる。</summary>
    [RelayCommand]
    private void AlignCutEnd() => AlignCutEdge(CutEdge.End);

    private void AlignCutEdge(CutEdge edge)
    {
        if (Editor is null || GuardCutLocked()) return;
        PauseIfPlaying();
        var pick = CutEdgeTarget.Pick(Editor.Project.CutSegments, SelectedCutIds, CurrentTime, edge);
        if (!pick.Success)
        {
            StatusMessage = Loc.Format("CutKey.EdgeFailed", pick.Error);
            return;
        }
        var cut = pick.Cut!;
        var r = Editor.UpdateCut(cut.Id, pick.Start, pick.End, SegmentAction.Remove, 0);
        if (!r.Success)
        {
            StatusMessage = Loc.Format("CutKey.EdgeFailed", r.Message);
            return;
        }
        StatusMessage = Loc.Format(edge == CutEdge.Start ? "CutKey.StartAligned" : "CutKey.EndAligned",
            TimeFormat.FormatShort(pick.Start), TimeFormat.FormatShort(pick.End))
            + (r.Message.Length > 0 ? " " + r.Message : "");
    }

    // ---------------- 候補（A / D / P / N / Shift+N） ----------------

    /// <summary>一覧で選択中の候補の行（一覧の並び順）。</summary>
    private List<CandidateItem> SelectedCandidateItems()
        => CandidateItems.Where(i => _selectedCandidateIdList.Contains(i.Id)).ToList();

    /// <summary>候補のキー操作はカット編集中だけ。字幕編集中は案内して false。</summary>
    private bool CanUseCandidateKeys()
    {
        if (Editor is null) return false;
        if (GuardCutLocked()) return false;
        return true;
    }

    /// <summary>A: 選択中の候補を［カットする］。</summary>
    [RelayCommand]
    private void AcceptSelectedCandidates()
    {
        if (!CanUseCandidateKeys()) return;
        var items = SelectedCandidateItems();
        if (items.Count == 0) { StatusMessage = Loc.Get("CutKey.NoCandidate"); return; }
        AcceptCandidates(items);
    }

    /// <summary>D: 選択中の候補を［却下］（却下済みだけなら未判断に戻す。ボタンと同じ）。</summary>
    [RelayCommand]
    private void DismissSelectedCandidates()
    {
        if (!CanUseCandidateKeys()) return;
        var items = SelectedCandidateItems();
        if (items.Count == 0) { StatusMessage = Loc.Get("CutKey.NoCandidate"); return; }
        DismissOrRestore(items);
    }

    /// <summary>P: 選択中の候補を［▶ 試す］（複数選択では最初の 1 件）。</summary>
    [RelayCommand]
    private void TrialSelectedCandidate()
    {
        if (!CanUseCandidateKeys()) return;
        var first = _selectedCandidateIdList.Select(id => CandidateItems.FirstOrDefault(i => i.Id == id)).FirstOrDefault(i => i is not null);
        if (first is null) { StatusMessage = Loc.Get("CutKey.NoCandidate"); return; }
        PreviewCandidateTrial(first);
    }

    /// <summary>N: 次の未判断の候補を選んで、開始の 2 秒前へ移動する。</summary>
    [RelayCommand]
    private void SelectNextCandidate() => StepCandidate(next: true);

    /// <summary>Shift+N: 前の未判断の候補を選んで、開始の 2 秒前へ移動する。</summary>
    [RelayCommand]
    private void SelectPrevCandidate() => StepCandidate(next: false);

    private void StepCandidate(bool next)
    {
        if (!CanUseCandidateKeys()) return;
        // 基準は選択中の候補（複数なら最初）の開始。選択が無ければ再生位置。
        var selected = SelectedCandidateItems();
        double basis = selected.Count > 0 ? selected.Min(i => i.Model.Start) : CurrentTime;
        var pending = CandidateItems.Where(i => i.IsPending && !selected.Contains(i)).OrderBy(i => i.Model.Start).ToList();
        var target = next
            ? pending.FirstOrDefault(i => selected.Count > 0 ? i.Model.Start >= basis - 0.0005 : i.Model.Start > basis + 0.0005)
            : pending.LastOrDefault(i => i.Model.Start < basis - 0.0005);
        if (target is null)
        {
            StatusMessage = Loc.Get(next ? "CutKey.NoNextCandidate" : "CutKey.NoPrevCandidate");
            return;
        }
        PauseIfPlaying();
        MoveToCandidate(target);
        if (!HasPreviewBanner) StatusMessage = Loc.Format("CutKey.CandidateSelected", target.StartText, target.EndText);
    }

    // ---------------- M / Shift+M ----------------

    /// <summary>M: 次のマーカー（録画中に付けた不要の印）へ移動する。</summary>
    [RelayCommand]
    private void SeekNextMarker() => SeekToMarker(next: true);

    /// <summary>Shift+M: 前のマーカーへ移動する。</summary>
    [RelayCommand]
    private void SeekPrevMarker() => SeekToMarker(next: false);

    private void SeekToMarker(bool next)
    {
        if (!HasProject) return;
        if (RecordingEvents is null) { StatusMessage = Loc.Get("CutKey.NoRecord"); return; }
        var markers = RecordingMarkers;
        if (markers.Count == 0) { StatusMessage = Loc.Get("CutKey.NoMarkers"); return; }
        int index = next
            ? markers.ToList().FindIndex(m => m > CurrentTime + 0.01)
            : markers.ToList().FindLastIndex(m => m < CurrentTime - 0.01);
        if (index < 0)
        {
            StatusMessage = Loc.Get(next ? "CutKey.NoNextMarker" : "CutKey.NoPrevMarker");
            return;
        }
        PauseIfPlaying();
        SeekTo(markers[index]);
        StatusMessage = Loc.Format("CutKey.MarkerMoved", index + 1, markers.Count, TimeFormat.Format(markers[index]));
    }
}
