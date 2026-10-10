using System.Windows;
using System.Windows.Input;

namespace ManualVideoStudio.Views;

/// <summary>文字入力。複数行（Enter で改行、Ctrl+Enter で決定）または 1 行（Enter で決定）。</summary>
public partial class TextInputWindow : Window
{
    public TextInputWindow(string title, string message, string initial, bool singleLine = false)
    {
        InitializeComponent();
        if (singleLine)
        {
            Input.AcceptsReturn = false;
            Input.TextWrapping = TextWrapping.NoWrap;
            Input.MinHeight = 0;
            OkButton.IsDefault = true;
        }
        Title = title;
        Message.Text = message;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Text => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
