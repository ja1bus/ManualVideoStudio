using System.Windows;

namespace ManualVideoStudio.Views;

/// <summary>バージョン情報（v1.17.0。［ヘルプ］の一番下）。版・著作権・MIT License・公開リポジトリ・ライセンス全文・第三者の表記。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
