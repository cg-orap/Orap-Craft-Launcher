using System.Windows.Controls;

namespace Orion.App.Views;

// All page views are pure XAML + data binding; the code-behind only initialises the component.

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

public partial class CatalogDetailView : UserControl
{
    public CatalogDetailView() => InitializeComponent();
}

public partial class VersionPickerView : UserControl
{
    public VersionPickerView() => InitializeComponent();
    /// <summary>A click anywhere on a row selects that version, mirroring the catalogue detail rows.</summary>
    private void OnVersionClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not ViewModels.VersionPickerViewModel vm) return;
        if (e.OriginalSource is not System.Windows.DependencyObject source) return;

        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;   // buttons keep their click
            if (node is System.Windows.Controls.ListBoxItem) break;
        }

        if (System.Windows.Controls.ItemsControl.ContainerFromElement((System.Windows.Controls.ItemsControl)sender, source)
            is not System.Windows.Controls.ListBoxItem item) return;

        if (item.DataContext is not Orion.Core.Instances.Instance instance) return;

        // Clicking the row is the action: switch the current version, then run the page's select
        // command (it navigates home and refreshes the launch key).
        if (System.Windows.Window.GetWindow(this)?.DataContext is ViewModels.MainViewModel main)
        {
            main.CurrentInstance = instance;
        }

        vm.SelectCommand.Execute(null);
        e.Handled = true;
    }
}
