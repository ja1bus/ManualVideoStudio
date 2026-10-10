using System.Windows;
using System.Windows.Input;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>動画タイトルの編集（v1.15）。Enter は改行（画面を閉じない）、Ctrl+Enter で［OK］と同じ確定。</summary>
public partial class TitleEditWindow : Window
{
    public TitleEditWindow()
    {
        InitializeComponent();
        PreviewKeyDown += TitleText_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            TitleText.Focus();
            TitleText.CaretIndex = TitleText.Text.Length;
        };
    }

    /// <summary>入力欄にも、フォント・秒数の選択欄にフォーカスがあるときにも、Ctrl+Enter で確定できる（Enter だけでは何も確定しない）。</summary>
    private void TitleText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DataContext is TitleEditViewModel vm)
        {
            vm.OkCommand.Execute(null);
            e.Handled = true;
        }
    }
}
