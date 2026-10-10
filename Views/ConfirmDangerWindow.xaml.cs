using System.Windows;
using System.Windows.Automation;

namespace ManualVideoStudio.Views;

/// <summary>取り消しにくい操作の確認（ボタンに操作名を出し、既定はキャンセル）。</summary>
public partial class ConfirmDangerWindow : Window
{
    public ConfirmDangerWindow(string title, string message, string okLabel, string cancelLabel)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        OkButton.Content = okLabel;
        CancelButton.Content = cancelLabel;
        AutomationProperties.SetName(OkButton, okLabel);
        AutomationProperties.SetName(CancelButton, cancelLabel);
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
