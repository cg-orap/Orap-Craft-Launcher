using System.Windows;
using System.Windows.Controls;

namespace Orion.App.Views;

// All seven page views are pure XAML + data binding; the code-behind only initialises the
// component, so they are kept together in one file instead of seven near-empty files.

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

public partial class VersionsView : UserControl
{
    public VersionsView() => InitializeComponent();
}

public partial class InstancesView : UserControl
{
    public InstancesView() => InitializeComponent();
}

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();
}

public partial class DownloadsView : UserControl
{
    public DownloadsView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Accent swatch click: forwards the scheme key to the view model.</summary>
    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && DataContext is ViewModels.SettingsViewModel vm)
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


