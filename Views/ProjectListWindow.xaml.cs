using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>プロジェクト一覧（v1.7）。選択の受け渡し・ダブルクリック・Enter だけを扱う。</summary>
public partial class ProjectListWindow : Window
{
    private readonly ProjectListViewModel _vm;

    public ProjectListWindow(ProjectListViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.SelectPathRequested += row =>
        {
            if (row is null) return;
            List.SelectedItems.Clear();
            List.SelectedItem = row;
            List.ScrollIntoView(row);
        };
        Loaded += (_, _) =>
        {
            if (List.Items.Count > 0)
            {
                List.SelectedIndex = 0;
                (List.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
            }
            else
            {
                SearchBox.Focus();
            }
        };
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm.SetSelection(List.SelectedItems.OfType<ProjectListRow>());

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 行の上のダブルクリックだけ（スクロールバーなどは除く）。
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListViewItem
            && _vm.OpenCommand.CanExecute(null))
            _vm.OpenCommand.Execute(null);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.IsKeyboardFocusWithin && _vm.OpenCommand.CanExecute(null))
        {
            _vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }
}
