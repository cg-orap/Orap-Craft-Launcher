using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Orion.App.Controls;
using Orion.App.ViewModels;
using Orion.Core.World;

namespace Orion.App.Views;

/// <summary>工具箱：离线种子地图 / 查看截图 / 游戏默认设置。</summary>
public partial class ToolboxView : UserControl
{
    private ToolboxViewModel? _attached;

    public ToolboxView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        SeedCanvas.HoverChanged += OnMapHover;
    }

    private void Attach()
    {
        if (_attached is not null) _attached.PropertyChanged -= OnViewModelPropertyChanged;

        _attached = DataContext as ToolboxViewModel;
        if (_attached is not null)
        {
            _attached.PropertyChanged += OnViewModelPropertyChanged;
            SeedCanvas.Seed = _attached.SeedValue;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolboxViewModel.SeedValue) && _attached is { } vm)
        {
            SeedCanvas.Seed = vm.SeedValue;
            SeedCanvas.CenterOn(0, 0);
        }
    }

    private void OnMapHover(object? sender, SeedMapHoverEventArgs e)
    {
        if (_attached is not { } vm) return;
        vm.MapStatus = SeedMap.Describe(e.ChunkX, e.ChunkZ, e.Slime);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SeedCanvas.Zoom(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => SeedCanvas.Zoom(0.8);

    private void OnCenterOrigin(object sender, RoutedEventArgs e) => SeedCanvas.CenterOn(0, 0);
}