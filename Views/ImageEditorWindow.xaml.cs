using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

public partial class ImageEditorWindow : Window
{
    private bool _closeConfirmed;

    public ImageEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Canvas.Focus();
    }

    private ImageEditorViewModel? Vm => DataContext as ImageEditorViewModel;

    /// <summary>プルダウンで選んだ後は画像側へフォーカスを戻す（Delete・矢印キーを注釈の操作に使うため）。</summary>
    private void FontSizeBox_DropDownClosed(object? sender, EventArgs e) => Canvas.Focus();

    /// <summary>閉じる前に未保存の変更を確認する（×ボタン・閉じるボタン・Alt+F4 共通）。</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || Vm is not { } vm) return;
        if (!vm.ConfirmClose()) e.Cancel = true;
        else _closeConfirmed = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl && key == Key.S) { Run(vm.SaveCommand, e); return; }
        if (ctrl && key == Key.Z) { Run(shift ? vm.RedoCommand : vm.UndoCommand, e); return; }
        if (ctrl && key == Key.Y) { Run(vm.RedoCommand, e); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || ctrl) return;
        // 文字サイズのプルダウン・表示時間の入力欄（動画の図形）の操作中は、矢印キー・文字キーをそちらに任せる。
        if (e.OriginalSource is DependencyObject src && (src is System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox
                || ItemsControl.ItemsControlFromItemContainer(src) is System.Windows.Controls.ComboBox))
        {
            if (key == Key.Escape) Canvas.Focus();
            return;
        }

        switch (key)
        {
            case Key.Escape:
                if (!Canvas.CancelDrag()) vm.Escape();
                e.Handled = true;
                break;
            case Key.Delete: Run(vm.DeleteSelectedCommand, e); break;
            case Key.V: Run(vm.SetToolCommand, e, EditorTool.Select); break;
            case Key.R: Run(vm.SetToolCommand, e, EditorTool.Rectangle); break;
            case Key.A: Run(vm.SetToolCommand, e, EditorTool.Arrow); break;
            case Key.L: Run(vm.SetToolCommand, e, EditorTool.Line); break;
            case Key.T: Run(vm.SetToolCommand, e, EditorTool.Text); break;
            case Key.M: Run(vm.SetToolCommand, e, EditorTool.Mosaic); break;
            case Key.Left: Nudge(vm, e, shift ? -10 : -1, 0); break;
            case Key.Right: Nudge(vm, e, shift ? 10 : 1, 0); break;
            case Key.Up: Nudge(vm, e, 0, shift ? -10 : -1); break;
            case Key.Down: Nudge(vm, e, 0, shift ? 10 : 1); break;
        }
    }

    private static void Nudge(ImageEditorViewModel vm, KeyEventArgs e, double dx, double dy)
    {
        if (!vm.HasSelection) return;
        vm.NudgeSelected(dx, dy);
        e.Handled = true;
    }

    private static void Run(ICommand command, KeyEventArgs e, object? parameter = null)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
        e.Handled = true;
    }
}
