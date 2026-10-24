using System.Windows;
using System.Windows.Controls;
using Orion.App.ViewModels;

namespace Orion.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Accent swatch click: forwards the scheme key to the view model.</summary>
    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && DataContext is SettingsViewModel vm)
        {
            vm.Accent = key;
            vm.SaveCommand.Execute(null);
        }
    }
}

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();
}

/// <summary>
/// 下载中
