using System.Windows;

namespace ManualVideoStudio.Views;

/// <summary>
/// 字幕依頼用動画を削除するかの確認（v1.12）。対象のフルパスを出す。DialogResult: true = 削除する（既定）/ false = 削除しない /
/// null（ウィンドウを閉じた）= 削除しない。選択は今回限りで、記憶しない。
/// </summary>
public partial class DeleteRequestVideoWindow : Window
{
    public DeleteRequestVideoWindow(string message, string path)
    {
        InitializeComponent();
        Message.Text = message;
        PathBox.Text = path;
        Loaded += (_, _) => DeleteButton.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
