using System.Windows;

using Orion.App.ViewModels;

namespace Orion.App;

/// <summary>The shell window; all behaviour lives in <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        Loaded += (_, _) => { App.ApplyWindowChrome(this); FixMaximizedBounds(); };
        StateChanged += (_, _) => FixMaximizedBounds();
        InitializeComponent();
    }
    /// <summary>
    /// 下载中心 owns a sub-menu: a click folds it open/shut instead of navigating. The click is
    /// swallowed so the ListBox does not select-and-navigate to the page as well.
    /// </summary>
    private void OnNavClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // The sub-buttons live inside this same row: their clicks must reach the button.
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is System.Windows.Controls.ListBoxItem) break;
        }

        if (System.Windows.Controls.ItemsControl.ContainerFromElement((System.Windows.Controls.ItemsControl)sender, source)
            is not System.Windows.Controls.ListBoxItem { DataContext: NavItem nav }) return;

        if (nav.SubItems.Count == 0) return;

        nav.IsExpanded = !nav.IsExpanded;
        e.Handled = true;
    }

    private void OnMinimize(object sender, System.Windows.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, System.Windows.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => Close();

    /// <summary>
    /// A WindowChrome window maximises to the full monitor bounds, which hides the taskbar.
    /// Clamping MaxHeight to the work area keeps the taskbar visible and is re-applied whenever
    /// the window changes state or moves to another monitor.
    /// </summary>
    private void FixMaximizedBounds()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = System.Windows.SystemParameters.WorkArea;

        MaxHeight = WindowState == WindowState.Maximized
            ? work.Height / dpi.DpiScaleY
            : double.PositiveInfinity;

        MaxWidth = WindowState == WindowState.Maximized
            ? work.Width / dpi.DpiScaleX
            : double.PositiveInfinity;
    }
}
