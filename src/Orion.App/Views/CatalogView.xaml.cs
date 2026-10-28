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
        Loaded += (_, _) => SyncSegments(animate: false);
        DataContextChanged += (_, _) => AttachViewModel();
    }



    private CatalogViewModel? ViewModel => DataContext as CatalogViewModel;

    private CatalogViewModel? _attached;
    private bool _segmentRetryQueued;

    /// <summary>跟随 ViewModel.Kind 的变动，让滑块和字色保持同步。</summary>
    private void AttachViewModel()
    {
        if (_attached is not null) _attached.PropertyChanged -= OnViewModelPropertyChanged;

        _attached = ViewModel;
        if (_attached is not null) _attached.PropertyChanged += OnViewModelPropertyChanged;

        Dispatcher.BeginInvoke(new Action(() => SyncSegments(animate: false)), DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CatalogViewModel.Kind) or null)
        {
            Dispatcher.BeginInvoke(new Action(() => SyncSegments(animate: true)), DispatcherPriority.Loaded);
        }
    }

    private void OnKindTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || ViewModel is not { } vm) return;
        vm.SelectKindCommand.Execute(key);
        Dispatcher.BeginInvoke(new Action(() => SyncSegments(animate: true)), DispatcherPriority.Loaded);
    }

    private void OnKindTabsHostSizeChanged(object sender, SizeChangedEventArgs e) => SyncSegments(animate: false);

    private (Button Button, string Key)[] SegmentButtons() =>
    [
        (KindTabVersions, "versions"),
        (KindTabModpack, "modpack"),
        (KindTabMod, "mod"),
        (KindTabShader, "shader"),
        (KindTabResourcepack, "resourcepack"),
    ];

    /// <summary>把滑块移到当前分类，并同步五个按钮的字色与字重。</summary>
    private void SyncSegments(bool animate)
    {
        if (KindIndicator is null || KindTabsHost is null) return;

        var current = ViewModel?.Kind?.Key ?? "versions";
        Button? active = null;

        foreach (var (button, key) in SegmentButtons())
        {
            var isActive = string.Equals(key, current, StringComparison.OrdinalIgnoreCase);
            button.Foreground = (Brush)FindResource(isActive ? "TextBrush" : "MutedTextBrush");
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            if (isActive) active = button;
        }

        if (active is null)
        {
            KindIndicator.Opacity = 0;
            return;
        }

        if (active.ActualWidth <= 0)
        {
            // 布局还没量出宽度：稍后再试一次，但不要先把滑块藏起来（否则点一下就"消失"）。
            if (!_segmentRetryQueued)
            {
                _segmentRetryQueued = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _segmentRetryQueued = false;
                    SyncSegments(animate: animate);
                }), DispatcherPriority.ContextIdle);
            }
            return;
        }

        KindTabsHost.UpdateLayout();
        var point = active.TranslatePoint(new Point(0, 0), KindTabsHost);
        KindIndicator.Width = active.ActualWidth;

        if (KindIndicator.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            KindIndicator.RenderTransform = translate;
        }

        if (!animate)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = point.X;
            KindIndicator.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(point.X, duration) { EasingFunction = ease });
        KindIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
    }

    /// <summary>Keeps one radio checked without firing SearchAsync twice during construction.</summary>
    /// <summary>
    /// A left click anywhere on a result row opens its detail page (the row is the affordance, no
    /// separate 详情 button). Clicks on the 安装 button are handled by the button and never bubble here.
    /// </summary>
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
