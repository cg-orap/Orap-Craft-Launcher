using System.Windows;
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
