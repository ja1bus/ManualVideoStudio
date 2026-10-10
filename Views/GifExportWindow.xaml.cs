using System.Windows;
using System.Windows.Controls;
using ManualVideoStudio.Core.Models;
using ManualVideoStudio.ViewModels;

namespace ManualVideoStudio.Views;

/// <summary>GIF 書き出しの品質（軽量 / 標準 / 高品質）の選択。</summary>
public partial class GifExportWindow : Window
{
    public GifExportWindow() => InitializeComponent();

    private GifExportViewModel? Vm => DataContext as GifExportViewModel;

    private void Preset_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is GifQuality q && Vm is { } vm) rb.IsChecked = vm.Quality == q;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: GifQuality q } && Vm is { } vm) vm.Quality = q;
    }
}
