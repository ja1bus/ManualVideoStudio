using System.Windows;
using System.Windows.Input;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

public partial class FrameCandidatesWindow : Window
{
    public FrameCandidatesWindow() => InitializeComponent();

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not FrameCandidatesViewModel vm || sender is not FrameworkElement { DataContext: FrameCandidateItem item }) return;
        if (e.ClickCount == 2) vm.SelectAndCloseCommand.Execute(item);
        else vm.SelectCommand.Execute(item);
    }
}
