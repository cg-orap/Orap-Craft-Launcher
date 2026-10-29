using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Orion.App.Controls;
using Orion.App.ViewModels;
using Orion.Core.World;

namespace Orion.App.Views;

/// <summary>工具箱：简化版群系地图 / 查看截图 / 游戏默认设置。</summary>
public partial class ToolboxView : UserControl
{
    private ToolboxViewModel? _attached;

    public ToolboxView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        BiomeCanvas.HoverChanged += OnMapHover;
    }

    private void Attach()
    {
        if (_attached is not null) _attached.PropertyChanged -= OnViewModelPropertyChanged;

        _attached = DataContext as ToolboxViewModel;
        if (_attached is not null)
        {
            _attached.PropertyChanged += OnViewModelPropertyChanged;
            BiomeCanvas.Seed = _attached.SeedValue;
            BiomeCanvas.ShowSlime = _attached.ShowSlimeOverlay;
            BiomeCanvas.ShowGrid = _attached.ShowGridOverlay;
            BiomeCanvas.EngineVersionIndex = CubiomesNative.VersionIndexFor(_attached.SeedVersion);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_attached is not { } vm) return;

        switch (e.PropertyName)
        {
            case nameof(ToolboxViewModel.SeedValue):
                BiomeCanvas.Seed = vm.SeedValue;
                BiomeCanvas.EngineVersionIndex = CubiomesNative.VersionIndexFor(vm.SeedVersion);
                BiomeCanvas.CenterOn(0, 0);
                break;
            case nameof(ToolboxViewModel.ShowSlimeOverlay):
                BiomeCanvas.ShowSlime = vm.ShowSlimeOverlay;
                BiomeCanvas.InvalidateVisual();
                break;
            case nameof(ToolboxViewModel.ShowGridOverlay):
                BiomeCanvas.ShowGrid = vm.ShowGridOverlay;
                BiomeCanvas.InvalidateVisual();
                break;
        }
    }

    private void OnMapHover(object? sender, BiomeHoverEventArgs e)
    {
        if (_attached is not { } vm) return;

        var slime = e.Slime ? " · 史莱姆区块" : "";
        var biome = e.NativeName ?? SimplifiedBiomeMap.DisplayName(e.FallbackBiome);
        vm.MapStatus = $"方块 {e.BlockX}, {e.BlockZ} · {biome}{slime}";
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => BiomeCanvas.Zoom(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => BiomeCanvas.Zoom(0.8);

    private void OnCenterOrigin(object sender, RoutedEventArgs e) => BiomeCanvas.CenterOn(0, 0);
}