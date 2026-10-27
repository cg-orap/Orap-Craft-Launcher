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
        FitToWorkArea();

        // 每次导航都播放一次页面进入动效（内容变化不会触发 Loaded，所以走属性通知）。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ViewModels.MainViewModel.CurrentPage)) AnimatePage();
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

    /// <summary>页面切换动效：220ms 淡入 + 8px 上移，节奏与设计稿一致。</summary>
    private void AnimatePage()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        PageHost.BeginAnimation(OpacityProperty, fade);

        var slide = new System.Windows.Media.Animation.DoubleAnimation(8, 0, duration) { EasingFunction = ease };
        if (PageHost.RenderTransform is System.Windows.Media.TranslateTransform translate)
        {
            translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        }
    }

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
