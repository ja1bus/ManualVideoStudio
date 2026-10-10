using System.Windows;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

public partial class ProgressWindow : Window
{
    public ProgressWindow() => InitializeComponent();

    /// <summary>×ボタンはキャンセル扱い。処理が終わるまでは閉じない（処理側が Close する）。</summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is ProgressViewModel { IsCompleted: false } vm)
        {
            vm.CancelCommand.Execute(null);
            e.Cancel = true;
        }
    }
}
