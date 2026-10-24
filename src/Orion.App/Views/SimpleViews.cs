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
}
