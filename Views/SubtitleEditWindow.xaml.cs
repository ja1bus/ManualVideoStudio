using System.Windows;
using System.Windows.Input;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>字幕の追加・編集（Enter で改行、Ctrl+Enter で決定）。</summary>
public partial class SubtitleEditWindow : Window
{
    public SubtitleEditWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SubtitleText.Focus();
            SubtitleText.CaretIndex = SubtitleText.Text.Length;
        };
    }

    private void TextInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DataContext is SubtitleEditViewModel vm)
        {
            vm.OkCommand.Execute(null);
            e.Handled = true;
        }
    }
}
