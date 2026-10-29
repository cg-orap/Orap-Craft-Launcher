using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Controls;
using Orion.App.ViewModels;

namespace Orion.App.Views;

/// <summary>
/// 下载中心 code-behind: the sub-tab radios, plus drag &amp; drop of a pack file onto the page.
///
/// Drop is handled here (not in the view model) because only a UI element receives the drag events;
/// the view model does the actual installation.
/// </summary>
public partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }



    private CatalogViewModel? ViewModel => DataContext as CatalogViewModel;


    /// <summary>Keeps one radio checked without firing SearchAsync twice during construction.</summary>
    /// <summary>
    /// A left click anywhere on a result row opens its detail page (the row is the affordance, no
    /// separate 详情 button). Clicks on the 安装 button are handled by the button and never bubble here.
    /// </summary>
    /// <summary>点击 CurseForge 卡片（不是「安装」按钮）→ 打开详情页。</summary>
    private async void OnCurseForgeRowClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        // 点到行内按钮时不打开详情
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
        }

        if (sender is FrameworkElement element && element.DataContext is CurseForgeRow row)
        {
            await vm.OpenCurseForgeDetailAsync(row);
        }
    }

    /// <summary>CurseForge 列表：点卡片主体（非按钮）→ 打开详情。</summary>
    private async void OnCurseForgeListClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || e.OriginalSource is not DependencyObject source) return;

        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
        }

        var container = ItemsControl.ContainerFromElement((ItemsControl)sender, source) as ListBoxItem;
        if (container?.DataContext is CurseForgeRow row)
        {
            await vm.OpenCurseForgeDetailAsync(row);
        }
    }

    private async void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // A click on 安装 (or any button inside the row) belongs to that button.
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
            if (node is ListBoxItem) break;
        }

        var container = ItemsControl.ContainerFromElement((ItemsControl)sender, source) as ListBoxItem;
        if (container?.DataContext is not CatalogRow row) return;

        e.Handled = true;
        await vm.ShowDetailsAsync(row).ConfigureAwait(true);
    }

    private void OnKindChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: CatalogKindOption kind } && ViewModel is { } vm)
        {
            vm.Kind = kind;
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var hasFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFile ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (hasFile) vm.DropActive = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) vm.DropActive = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        vm.DropActive = false;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;

        foreach (var file in files)
        {
            await vm.HandleDroppedFileAsync(file).ConfigureAwait(true);
        }
    }
}
