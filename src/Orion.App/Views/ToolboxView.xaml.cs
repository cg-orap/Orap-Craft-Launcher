using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        BiomeCanvas.StructureClicked += OnStructureClicked;
    }

    private void Attach()
    {
        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnViewModelPropertyChanged;
            _attached.LocateRequested -= OnLocateRequested;
            _attached.DimensionChanged -= OnDimensionChanged;
            _attached.SpawnRequested -= OnSpawnRequested;
            _attached.StructureFilterChanged -= OnStructureFilterChanged;
        }

        _attached = DataContext as ToolboxViewModel;
        if (_attached is not null)
        {
            _attached.PropertyChanged += OnViewModelPropertyChanged;
            BiomeCanvas.Seed = _attached.SeedValue;
            BiomeCanvas.ShowSlime = _attached.ShowSlimeOverlay;
            BiomeCanvas.ShowGrid = _attached.ShowGridOverlay;
            BiomeCanvas.EngineVersionIndex = CubiomesNative.VersionIndexFor(_attached.SeedVersion);
            _attached.LocateRequested += OnLocateRequested;
            _attached.DimensionChanged += OnDimensionChanged;
            _attached.SpawnRequested += OnSpawnRequested;
            _attached.StructureFilterChanged += OnStructureFilterChanged;
            _attached.ChangeStructureFilter();
            BiomeCanvas.Dimension = DimensionOf(_attached.DimensionIndex);
            BiomeCanvas.ShowStructures = _attached.ShowStructures;
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
            case nameof(ToolboxViewModel.ShowStructures):
                BiomeCanvas.ShowStructures = vm.ShowStructures;
                break;
            case nameof(ToolboxViewModel.ShowGridOverlay):
                BiomeCanvas.ShowGrid = vm.ShowGridOverlay;
                BiomeCanvas.InvalidateVisual();
                break;
        }
    }

    private void OnLocateRequested(int blockX, int blockZ) => BiomeCanvas.LocateTo(blockX, blockZ);

    private void OnSpawnRequested()
    {
        if (_attached is not { } vm) return;
        var spawn = CubiomesNative.Spawn(vm.SeedValue, CubiomesNative.VersionIndexFor(vm.SeedVersion));
        if (spawn is { } position) BiomeCanvas.LocateTo(position.X, position.Z);
    }

    private void OnDimensionChanged(int index) => BiomeCanvas.Dimension = DimensionOf(index);

    private void OnStructureFilterChanged(IEnumerable<string>? keys) => BiomeCanvas.SetStructureFilter(keys);

    /// <summary>点地图上的结构图标：把坐标填进输入框并定位过去。</summary>
    private void OnStructureClicked(object? sender, StructureMarker marker)
    {
        if (_attached is not { } vm) return;

        vm.LocateX = marker.BlockX.ToString();
        vm.LocateZ = marker.BlockZ.ToString();
        vm.NotifyLocateInputsChanged();
        BiomeCanvas.LocateTo(marker.BlockX, marker.BlockZ);
        vm.MapStatus = $"{marker.Label}：方块 {marker.BlockX}, {marker.BlockZ}";
    }

    private static int DimensionOf(int index) => index switch
    {
        1 => CubiomesNative.DimNether,
        2 => CubiomesNative.DimEnd,
        _ => CubiomesNative.DimOverworld,
    };

    private void OnMapHover(object? sender, BiomeHoverEventArgs e)
    {
        if (_attached is not { } vm) return;

        var slime = e.Slime ? " · 史莱姆区块" : "";
        var biome = e.NativeName ?? SimplifiedBiomeMap.DisplayName(e.FallbackBiome);
        var structure = e.NearStructure is { } near ? $" · 附近：{near}" : "";
        vm.MapStatus = $"方块 {e.BlockX}, {e.BlockZ} · {biome}{slime}{structure}";
    }

    /// <summary>
    /// 页面级滚轮：指针在左栏时滚动设置面板，在地图区则让画布自己缩放。
    ///
    /// 之所以放在页面级：左栏里的分段切换/输入框有内部滚动或会吃掉滚轮事件，
    /// 挂在 ScrollViewer 上时经常收不到，用户就会觉得"没法滚动"。
    /// </summary>
    private void OnSeedPageWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Grid page) return;

        var viewer = SeedPanelScroll;
        if (viewer is null) return;

        // 指针在右半边（地图）时不做处理，交给 BiomeMapCanvas 缩放
        var position = e.GetPosition(page);
        var panelWidth = viewer.ActualWidth + viewer.Margin.Left + viewer.Margin.Right + 20;
        if (position.X > panelWidth) return;

        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>键盘也能滚动（PageUp/PageDown/Home/End），触控板或无滚轮时的兜底。</summary>
    private void OnSeedPageKeyDown(object sender, KeyEventArgs e)
    {
        var viewer = SeedPanelScroll;
        if (viewer is null) return;

        var page = Math.Max(60, viewer.ViewportHeight * 0.9);
        switch (e.Key)
        {
            case Key.PageDown: viewer.ScrollToVerticalOffset(viewer.VerticalOffset + page); break;
            case Key.PageUp: viewer.ScrollToVerticalOffset(viewer.VerticalOffset - page); break;
            case Key.End: viewer.ScrollToEnd(); break;
            case Key.Home: viewer.ScrollToTop(); break;
            default: return;
        }

        e.Handled = true;
    }

    private ScrollViewer? SeedPanelScroll => FindName("SeedPanelScroll") as ScrollViewer;

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        BiomeCanvas.ClearZoomAnchor();   // 按钮缩放以面板中心为准
        BiomeCanvas.ZoomIn();
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        BiomeCanvas.ClearZoomAnchor();
        BiomeCanvas.ZoomOut();
    }

    private void OnCenterOrigin(object sender, RoutedEventArgs e) => BiomeCanvas.CenterOn(0, 0);
}