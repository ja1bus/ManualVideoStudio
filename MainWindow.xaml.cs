using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ManualVideoStudio.Core.Persistence;
using ManualVideoStudio.Services;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio;

/// <summary>主画面。コードビハインドは View 固有の処理（キー操作・D&amp;D・プレーヤーの接続・ウィンドウ位置）だけを持つ。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ISettingsService _settings;

    public MainWindow(MainViewModel vm, ISettingsService settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        vm.AttachPlayer(new MediaElementPlayer(Media));
        Timeline.SeekRequested += (_, t) => vm.SeekTo(t);
        Timeline.ItemClicked += (_, e) => vm.OnTimelineItemClicked(e.Kind, e.Id, e.Additive);
        Timeline.CutEdgeDragged += (_, e) => vm.ApplyCutEdgeDrag(e.Id, e.Start, e.End); // v1.21: カットの端のドラッグ
        vm.ListSelectionRequested += OnListSelectionRequested;
        RestorePlacement();
    }

    public Task InitializeAsync(string? startupPath) => _vm.InitializeAsync(startupPath);

    // ---------------- キーボード ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool inText = e.OriginalSource is TextBoxBase or PasswordBox || (e.OriginalSource is ComboBox cb && cb.IsEditable);

        // タイムラインでカットの端をドラッグ中の Esc は、ドラッグの取り消し（v1.21。他のキーより先に受ける）。
        if (e.Key == Key.Escape && Timeline.CancelEdgeDrag())
        {
            e.Handled = true;
            return;
        }

        // 保存・開くは入力中でも有効（入力中のキャプションを先に確定させる）。
        if (ctrl && e.Key == Key.S)
        {
            CommitFocusedCaption();
            Execute(shift ? _vm.SaveProjectAsCommand : _vm.SaveProjectCommand, e);
            return;
        }
        if (e.Key == Key.F1)
        {
            Execute(_vm.OpenManualCommand, e);
            return;
        }
        if (ctrl && e.Key == Key.O)
        {
            Execute(shift ? _vm.OpenProjectCommand : _vm.OpenVideoCommand, e);
            return;
        }
        if (inText)
        {
            // 選択中の字幕の入力欄での Esc は、反映していない入力を捨てて戻す（v1.13）。
            if (e.Key == Key.Escape && e.OriginalSource is TextBox { Tag: "draft" }) _vm.RevertSubtitleDraftCommand.Execute(null);
            if (e.Key == Key.Escape) { FocusWindow(); e.Handled = true; }
            return; // テキスト入力中は Undo・矢印などを TextBox に任せる
        }

        if (ctrl && e.Key == Key.Z) { Execute(shift ? _vm.RedoCommand : _vm.UndoCommand, e); return; }
        if (ctrl && e.Key == Key.Y) { Execute(_vm.RedoCommand, e); return; }
        if (ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        switch (e.Key)
        {
            case Key.Space: Execute(_vm.PlayPauseCommand, e); break;
            case Key.Left: Execute(shift ? _vm.JumpCommand : _vm.StepFrameCommand, e, shift ? null : "-1", shift ? "-" + JumpSeconds : null); break;
            case Key.Right: Execute(shift ? _vm.JumpCommand : _vm.StepFrameCommand, e, shift ? null : "1", shift ? "+" + JumpSeconds : null); break;
            case Key.Home: Execute(_vm.SeekStartCommand, e); break;
            case Key.End: Execute(_vm.SeekEndCommand, e); break;
            case Key.I: Execute(_vm.SetMarkInCommand, e); break;
            case Key.O: Execute(_vm.SetMarkOutCommand, e); break;
            // R: 片方だけの指定なら、もう片方は再生位置。字幕編集中は字幕・図形の時刻を合わせて直す追加カット（v1.21）。
            case Key.R: Execute(_vm.QuickCutCommand, e); break;
            // v1.21: 再生速度（J / K / L）、カットの端（Q / W）、候補（A / D / P / N）、マーカー（M）。
            case Key.L: Execute(_vm.PlayForwardCommand, e); break;
            case Key.J: Execute(_vm.PlayReverseCommand, e); break;
            case Key.K: Execute(_vm.StopPlaybackCommand, e); break;
            case Key.Q: Execute(_vm.AlignCutStartCommand, e); break;
            case Key.W: Execute(_vm.AlignCutEndCommand, e); break;
            case Key.A: Execute(_vm.AcceptSelectedCandidatesCommand, e); break;
            case Key.D: Execute(_vm.DismissSelectedCandidatesCommand, e); break;
            case Key.P: Execute(_vm.TrialSelectedCandidateCommand, e); break;
            case Key.N: Execute(shift ? _vm.SelectPrevCandidateCommand : _vm.SelectNextCandidateCommand, e); break;
            case Key.M: Execute(shift ? _vm.SeekPrevMarkerCommand : _vm.SeekNextMarkerCommand, e); break;
            case Key.T: Execute(_vm.AddSubtitleCommand, e); break;
            case Key.G: Execute(_vm.AddShapesCommand, e); break;
            case Key.C: Execute(_vm.AddCaptureCommand, e); break;
            case Key.Escape: Execute(_vm.ClearMarksCommand, e); break;
        }
    }

    private string JumpSeconds => _settings.Current.Ui.JumpSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void Execute(ICommand command, KeyEventArgs e, string? parameter = null, string? altParameter = null)
    {
        var p = parameter ?? altParameter;
        if (command.CanExecute(p)) command.Execute(p);
        e.Handled = true;
    }

    private void CommitFocusedCaption()
    {
        if (Keyboard.FocusedElement is TextBox { DataContext: CapturePointItem item } tb) _vm.CommitCaption(item, tb.Text);
    }

    // ---------------- キーボードのフォーカス ----------------
    // フォーカスを持っていたボタンが非表示・無効になると（［カットを確定して字幕編集へ］・［カット編集へ戻る］、
    // 録画中に主画面を無効にするなど）、主画面のどこにもフォーカスが無くなり、クリックするまで Space などのキー操作が効かない。
    // その場合は主画面自体にフォーカスを戻す（v1.6.2）。

    private void Window_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) ScheduleFocusRestore();
    }

    private void Window_Activated(object? sender, EventArgs e) => ScheduleFocusRestore();

    private void Window_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) ScheduleFocusRestore();
    }

    /// <summary>フォーカスの移動が落ち着いてから確かめる（別の画面・メニューへ移った場合は何もしない）。</summary>
    private void ScheduleFocusRestore() => Dispatcher.BeginInvoke(RestoreFocusIfLost, DispatcherPriority.Input);

    private void RestoreFocusIfLost()
    {
        if (!IsActive || !IsEnabled || IsKeyboardFocusWithin || Keyboard.FocusedElement is not null) return;
        FocusWindow();
    }

    private void FocusWindow()
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    // ---------------- 選択中の字幕の入力欄（v1.13） ----------------

    /// <summary>入力欄を離れたら反映する。ただし、字幕の入力欄どうしの移動と［入力を戻す］へ移るときは反映しない。</summary>
    private void SubtitleDraft_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is FrameworkElement { Tag: "draft" or "NoCommit" }) return;
        _vm.ApplySubtitleDraftCommand.Execute(null);
    }

    private void SubtitleDraftSingle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.ApplySubtitleDraftCommand.Execute(null);
        e.Handled = true;
        FocusWindow(); // 続けて T・G などのキー操作が使えるよう、入力欄から抜ける（キャプション欄と同じ）
    }

    /// <summary>文章の欄は改行できるため、Ctrl+Enter で反映する。</summary>
    private void SubtitleDraftMulti_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.ApplySubtitleDraftCommand.Execute(null);
        e.Handled = true;
        FocusWindow(); // 続けて T・G などのキー操作が使えるよう、入力欄から抜ける（キャプション欄と同じ）
    }

    // ---------------- キャプション入力 ----------------

    private void CaptionBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitCaption((TextBox)sender);

    private void CaptionBox_KeyDown(object sender, KeyEventArgs e)
    {
        var tb = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            CommitCaption(tb);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); // 入力を取り消す
        }
    }

    private void CommitCaption(TextBox tb)
    {
        if (tb.DataContext is not CapturePointItem item) return;
        if (tb.Text != item.Caption) _vm.CommitCaption(item, tb.Text);
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    // 手順書の文（操作文・補足・章。v1.9）。Tag が項目名。補足（複数行）は Ctrl+Enter / フォーカス移動で確定する。
    private void DetailBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDetail((TextBox)sender);

    private void DetailBox_KeyDown(object sender, KeyEventArgs e)
    {
        var tb = (TextBox)sender;
        if (e.Key == Key.Enter && (!tb.AcceptsReturn || Keyboard.Modifiers == ModifierKeys.Control))
        {
            CommitDetail(tb);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); // 入力を取り消す
        }
    }

    private void CommitDetail(TextBox tb)
    {
        if (tb.DataContext is not CapturePointItem item || tb.Tag is not string field) return;
        _vm.CommitCaptureText(item, field, tb.Text);
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    // ---------------- 一覧 ----------------

    private void CutGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CutGrid.SelectedItem is CutSegmentItem item) _vm.EditCutCommand.Execute(item);
    }

    private void SubtitleGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // ダブルクリックは、その字幕の開始位置へ移動する（編集は右の「選択中の字幕」の欄）。
        if (SubtitleGrid.SelectedItem is SubtitleItem item) _vm.SeekToSubtitleCommand.Execute(item);
    }

    private void ShapeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // ダブルクリックは、その図形の開始位置へ移動する（字幕と同じ。時間は「選択中の図形」の欄、描き直しは一覧の下の［図形再編集］）。
        if (ShapeGrid.SelectedItem is ShapeGroupItem item) _vm.SeekToShapesCommand.Execute(item);
    }

    // ---------------- 一覧とタイムラインの選択の連動（v1.17.0） ----------------

    /// <summary>プログラムから行を選んでいる間は、選択の変更を ViewModel へ二重に通知しない（通知しても同じ内容）。</summary>
    private void CutGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SetSelectedCuts(CutGrid.SelectedItems.OfType<CutSegmentItem>().Select(i => i.Id));

    private void SubtitleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SetSelectedSubtitles(SubtitleGrid.SelectedItems.OfType<SubtitleItem>().Select(i => i.Id));

    private void CandidateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SetSelectedCandidates(CandidateGrid.SelectedItems.OfType<CandidateItem>().Select(i => i.Id));

    /// <summary>タイムラインのクリック・次へ進む操作で、対応する一覧の行を選んで見える位置へスクロールする（空なら選択を解除する。再生位置の移動は呼び出し側が決める）。</summary>
    private void OnListSelectionRequested(ListKind kind, IReadOnlyList<string> ids)
    {
        // 一覧のタブが表示されてから選ぶ。
        Dispatcher.BeginInvoke(() =>
        {
            if (kind == ListKind.Cuts) SelectRows(CutGrid, _vm.CutItems.Where(i => ids.Contains(i.Id)));
            else if (kind == ListKind.Subtitles) SelectRows(SubtitleGrid, _vm.SubtitleItems.Where(i => ids.Contains(i.Id)));
            else if (kind == ListKind.Shapes) SelectRows(ShapeGrid, _vm.ShapeItems.Where(i => ids.Contains(i.Id)));
            else SelectRows(CandidateGrid, _vm.CandidateItems.Where(i => ids.Contains(i.Id)));
        }, DispatcherPriority.Background);
    }

    private static void SelectRows(DataGrid grid, IEnumerable<object> rows)
    {
        var list = rows.ToList();
        grid.SelectedItems.Clear();
        if (list.Count == 0) return;
        foreach (var row in list) grid.SelectedItems.Add(row);
        grid.ScrollIntoView(list[0]);
    }

    private void ShapeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SetSelectedShapes(ShapeGrid.SelectedItems.OfType<ShapeGroupItem>().Select(i => i.Id));

    private void SelectAllPendingCandidates_Click(object sender, RoutedEventArgs e)
    {
        // 一覧に表示している未判断だけを選ぶ（絞り込みで隠れている行は選ばない）。
        CandidateGrid.SelectedItems.Clear();
        foreach (var item in _vm.CandidateItems.Where(i => i.Model.Status != Core.Models.CandidateStatus.Accepted)) CandidateGrid.SelectedItems.Add(item);
        CandidateGrid.Focus();
    }

    private void CandidateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateGrid.SelectedItem is CandidateItem item) _vm.SeekToCandidateCommand.Execute(item);
    }

    // ---------------- ドラッグ＆ドロップ ----------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Activate();
            await _vm.OpenPathAsync(files[0]);
        }
    }

    // ---------------- メニュー ----------------

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        // 録画のショートカットは設定で変えられる（v1.21）。表示は設定値から作る。
        var h = _vm.Settings.Recording.Hotkeys ?? new Core.Models.RecordingHotkeys();
        var recording = $"""
            録画中（録画画面を開いている間、どのアプリからでも。設定の［操作・その他］で変えられます）:
            {h.StartStop.DisplayText}  録画開始 / 停止
            {h.Pause.DisplayText}  一時停止 / 再開
            {h.Marker.DisplayText}  不要マーカー（この直前は不要、の印）
            """;
        MessageBox.Show(this,
            """
            Space        再生 / 一時停止（再生は常に ×1）
            L / J / K    順方向に再生 / 逆方向に再生 / 停止（L・J は押すたびに ×1 → ×2 → ×4。K で ×1 に戻す）
            ← / →        1 フレーム戻る / 進む
            Shift+← / →  設定の秒数だけ戻る / 進む（既定 1 秒）
            Home / End   先頭 / 末尾へ
            M / Shift+M  次 / 前のマーカー（録画中に付けた不要の印）へ
            I / O        カット開始 / 終了位置を設定（字幕編集中は字幕の開始 / 終了）
            R            この区間をカット（片方だけの指定なら、もう片方は再生位置。字幕編集中は確認のうえ字幕・図形の時刻も直す）
            Q / W        カットの開始 / 終了を再生位置に合わせる（カット編集中のみ）
            A / D / P    選択中の候補をカットする / 却下 / 試す（カット編集中のみ）
            N / Shift+N  次 / 前の未判断の候補へ（カット編集中のみ）
            T            字幕を追加（字幕編集中のみ）
            G            図形を追加（四角・矢印・直線・文字・モザイク。字幕編集中のみ）
            Esc          カット位置の指定を解除（入力欄ではフォーカスを外す）
            C            この位置を画像に追加（全機能モードのみ）
            Ctrl+Z / Y   元に戻す / やり直し
            Ctrl+S       プロジェクトを保存（Shift で名前を付けて保存）
            Ctrl+O       動画を開く（Shift でプロジェクトを開く）
            F1           取扱説明書（Web 版）を開く

            """ + recording + """


            タイムライン: クリック・ドラッグで移動、Ctrl+ホイールで拡大、ホイールで左右移動、ダブルクリックで全体表示
            カット編集中は、確定したカットの端をドラッグして開始・終了を直せます（Shift で吸着なし、Esc で取り消し）
            """, "キーボード操作", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 終了・ウィンドウ位置 ----------------

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CommitFocusedCaption();
        if (!_vm.CanClose() || !_vm.ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        SavePlacement();
        _vm.SaveSettingsQuietly();
    }

    private void RestorePlacement()
    {
        var ui = _settings.Current.Ui;
        if (ui.WindowWidth >= MinWidth && ui.WindowHeight >= MinHeight)
        {
            Width = ui.WindowWidth;
            Height = ui.WindowHeight;
        }
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(ui.WindowLeft) && !double.IsNaN(ui.WindowTop) && screen.Contains(new Point(ui.WindowLeft + 40, ui.WindowTop + 20)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = ui.WindowLeft;
            Top = ui.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (ui.WindowMaximized) WindowState = WindowState.Maximized;
        if (ui.RightPaneWidth >= 340) RightColumn.Width = new GridLength(ui.RightPaneWidth);
    }

    private void SavePlacement()
    {
        var ui = _settings.Current.Ui;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        ui.WindowLeft = r.Left;
        ui.WindowTop = r.Top;
        ui.WindowWidth = r.Width;
        ui.WindowHeight = r.Height;
        ui.WindowMaximized = WindowState == WindowState.Maximized;
        ui.RightPaneWidth = RightColumn.ActualWidth;
    }
}
