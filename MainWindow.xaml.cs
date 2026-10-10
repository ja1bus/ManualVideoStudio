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
        RestorePlacement();
    }

    public Task InitializeAsync(string? startupPath) => _vm.InitializeAsync(startupPath);

    // ---------------- キーボード ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool inText = e.OriginalSource is TextBoxBase or PasswordBox || (e.OriginalSource is ComboBox cb && cb.IsEditable);

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
            case Key.R or Key.S when _vm.IsCutLocked:
                _vm.StatusMessage = Loc.Get("Status.CutLocked");
                e.Handled = true;
                break;
            case Key.R: Execute(_vm.AddRemoveCutCommand, e); break;
            case Key.S: Execute(_vm.AddShortenCutCommand, e); break;
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

    private void JumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.GoToTimeCommand.Execute(null);
        // 移動できたら（入力欄が空になる）入力欄から抜け、続けて Space で再生できるようにする（v1.6.2）。
        if (string.IsNullOrEmpty(_vm.JumpText)) FocusWindow();
        e.Handled = true;
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
        if (SubtitleGrid.SelectedItem is SubtitleItem item) _vm.EditSubtitleCommand.Execute(item);
    }

    private void ShapeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ShapeGrid.SelectedItem is ShapeGroupItem item) _vm.EditShapesCommand.Execute(item);
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
        => MessageBox.Show(this,
            """
            Space        再生 / 一時停止
            ← / →        1 フレーム戻る / 進む
            Shift+← / →  設定の秒数だけ戻る / 進む（既定 1 秒）
            Home / End   先頭 / 末尾へ
            I / O        カット開始 / 終了位置を設定（字幕編集中は字幕の開始 / 終了）
            R            削除区間として登録（カット編集中のみ）
            S            短縮区間として登録（カット編集中のみ）
            T            字幕を追加（字幕編集中のみ）
            G            図形を追加（四角・矢印・直線・文字・モザイク。字幕編集中のみ）
            Esc          カット位置の指定を解除（入力欄ではフォーカスを外す）
            C            この位置を画像に追加（全機能モードのみ）
            Ctrl+Z / Y   元に戻す / やり直し
            Ctrl+S       プロジェクトを保存（Shift で名前を付けて保存）
            Ctrl+O       動画を開く（Shift でプロジェクトを開く）
            F1           取扱説明書を開く

            録画中（録画画面を開いている間、どのアプリからでも）:
            Ctrl+Shift+F9  録画開始 / 停止
            Ctrl+Shift+F8  一時停止 / 再開

            タイムライン: クリック・ドラッグで移動、Ctrl+ホイールで拡大、ホイールで左右移動、ダブルクリックで全体表示
            """, "キーボード操作", MessageBoxButton.OK, MessageBoxImage.Information);

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
