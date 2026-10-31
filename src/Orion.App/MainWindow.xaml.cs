using System.Windows;

using Orion.App.ViewModels;

namespace Orion.App;

/// <summary>The shell window; all behaviour lives in <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        Loaded += (_, _) => { App.ApplyWindowChrome(this); FixMaximizedBounds(); };
        SourceInitialized += OnTitleBarSourceInitialized;   // 双击标题栏/系统菜单不再最大化
        StateChanged += (_, _) => FixMaximizedBounds();
        InitializeComponent();
        FitToWorkArea();

        // 每次导航都播放一次页面进入动效（内容变化不会触发 Loaded，所以走属性通知）。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                };
            }
        };

        // 支持 --page <key> 直接打开某个页面（截图、排障、深链接都用得上）。
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            var key = args[index + 1];
            Loaded += (_, _) => (DataContext as ViewModels.MainViewModel)?.Navigate(key);
        }

        // --instance-tab <0..5>：直接打开版本详情的某个页签（概览/模组/存档/日志/快照/设置）。
        var tabIndex = Array.FindIndex(args, a => string.Equals(a, "--instance-tab", StringComparison.OrdinalIgnoreCase));
        if (tabIndex >= 0 && tabIndex + 1 < args.Length && int.TryParse(args[tabIndex + 1], out var tab))
        {
            Loaded += (_, _) =>
            {
                if (DataContext is ViewModels.MainViewModel main)
                {
                    main.Navigate("instances");
                    main.InstancesPage.TabIndex = tab;
                    main.InstancesPage.RefreshSnapshots();
                }
            };
        }

        // --demo-updates：模组页填充演示用的更新候选（截图/验收，不联网）。
        if (Array.Exists(args, a => string.Equals(a, "--demo-updates", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += (_, _) =>
            {
                if (DataContext is ViewModels.MainViewModel demo)
                {
                    demo.Navigate("mods");
                    demo.ModsPage.SeedDemoUpdates();
                }
            };
        }

        // --kind <modpack|mod|shader|resourcepack|versions>：直接定位到下载中心的某个资源分类。
        var kindIndex = Array.FindIndex(args, a => string.Equals(a, "--kind", StringComparison.OrdinalIgnoreCase));
        if (kindIndex >= 0 && kindIndex + 1 < args.Length)
        {
            var kindKey = args[kindIndex + 1];
            Loaded += (_, _) =>
            {
                if (DataContext is not ViewModels.MainViewModel main) return;
                var kind = main.CatalogPage.Kinds.FirstOrDefault(k =>
                    string.Equals(k.Key, kindKey, StringComparison.OrdinalIgnoreCase));
                if (kind is not null) main.CatalogPage.Kind = kind;
                main.Navigate("catalog");
            };
        }
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

        // 图标导航不再有子项：这里不做任何拦截，点击直接交给 ListBox 选择 → 导航。
    }

    /// <summary>窗口默认 1280x800，但在小屏或高 DPI 下按工作区收窄，避免超出屏幕。</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var w = Math.Min(Width, area.Width - 64);
        var h = Math.Min(Height, area.Height - 64);
        Width = w;
        Height = h;
    }

    private void OnMinimize(object sender, System.Windows.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>让指示条滑到当前选中的导航项旁边（200ms，CubicEase）。</summary>
    private void OnNavSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(PositionRailIndicator), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PositionRailIndicator()
    {
        if (NavList.SelectedItem is null) { RailIndicator.Opacity = 0; return; }

        NavList.UpdateLayout();
        if (NavList.ItemContainerGenerator.ContainerFromItem(NavList.SelectedItem) is not FrameworkElement container) return;

        var point = container.TranslatePoint(new Point(0, 0), (UIElement)RailIndicator.Parent);
        var target = point.Y + (container.ActualHeight - RailIndicator.Height) / 2;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));

        if (RailIndicator.RenderTransform is not System.Windows.Media.TranslateTransform translate)
        {
            translate = new System.Windows.Media.TranslateTransform();
            RailIndicator.RenderTransform = translate;
        }

        translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, duration) { EasingFunction = ease });
        RailIndicator.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, duration));
        RailIndicator.Margin = new Thickness(0, 0, 0, 0);
    }

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => Close();

    /// <summary>
    /// 全屏/最大化功能已移除：窗口仍可拖边缩放，但双击标题栏不再最大化，
    /// 也不响应系统菜单里的"最大化"（吞掉 WM_NCLBUTTONDBLCLK 与 WM_SYSCOMMAND 的最大化命令）。
    /// </summary>
    private void OnTitleBarSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource source) return;
        source.AddHook(TitleBarWndProc);
    }

    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;

    private IntPtr TitleBarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcLButtonDblClk)
        {
            handled = true;   // 双击标题栏不再最大化
            return IntPtr.Zero;
        }

        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMaximize)
        {
            handled = true;   // 系统菜单 / Win+Up 也不最大化
            return IntPtr.Zero;
        }

        // 贴靠到屏幕边缘 / 系统最大化都会问"最大能多大"：这里直接回"就是现在这个尺寸"，
        // 于是窗口永远铺不满屏幕，但拖边框缩放（走 WM_SIZING）仍然是自由的。
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            LockMaxSizeToCurrent(lParam);
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private const int WmGetMinMaxInfo = 0x0024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    private void LockMaxSizeToCurrent(IntPtr lParam)
    {
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        if (!GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        // ptMaxSize 决定"最大化/贴靠后"的尺寸：直接等于当前尺寸 → 视觉上没有任何变化。
        info.MaxSize.X = width;
        info.MaxSize.Y = height;
        info.MaxPosition.X = rect.Left;
        info.MaxPosition.Y = rect.Top;

        // 手动拖边框仍允许放大，但不超过当前工作区（不会盖住任务栏）。
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var work = System.Windows.SystemParameters.WorkArea;
        info.MaxTrackSize.X = Math.Max(width, (int)(work.Width * dpi.DpiScaleX));
        info.MaxTrackSize.Y = Math.Max(height, (int)(work.Height * dpi.DpiScaleY));

        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
    }

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

    private static readonly string[] PackExtensions = [".zip", ".mrpack"];

    private static string[] DroppedPacks(System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return [];

        return (e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[] ?? [])
            .Where(path => PackExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Any page accepts a dropped pack: the caret turns into a copy cursor while hovering.</summary>
    private void OnWindowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        var packs = DroppedPacks(e);
        e.Effects = packs.Length > 0
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping a .zip/.mrpack anywhere in the window installs it as a new version.</summary>
    private async void OnWindowDrop(object sender, System.Windows.DragEventArgs e)
    {
        var packs = DroppedPacks(e);
        if (packs.Length == 0) return;

        e.Handled = true;

        if (DataContext is not ViewModels.MainViewModel main) return;

        // Jump to the download centre so the install (and its progress) is visible wherever the user
        // dropped the file; the task itself also shows up in 下载管理.
        main.Navigate("catalog");

        foreach (var path in packs)
        {
            await main.CatalogPage.InstallPackFileAsync(path, null).ConfigureAwait(true);
        }
    }
}
