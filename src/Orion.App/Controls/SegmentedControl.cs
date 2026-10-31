using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orion.App.Controls;

/// <summary>
/// 分段控件：一个长条里放若干等权选项，选中态是一块会滑过去的底色。
///
/// 用途与下载中心的分类栏一致（游戏版本 / 整合包 / 模组 / 光影 / 材质包），所以版本详情的
/// 页签、模组筛选、日志级别也都用它，保证整个启动器里"同类按钮"是同一种交互。
///
/// 为什么不用 ListBox：ListBoxItem 自带容器视觉，z 序上和滑块打架，点击后滑块会被悬停底
/// 色盖住甚至消失。这里用 ItemsControl + 自己的容器（<see cref="SegmentedItem"/>），滑块
/// 在模板里固定为第一个子元素，层级完全可控。
/// </summary>
public class SegmentedControl : ItemsControl
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(Orientation.Horizontal,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnOrientationChanged));

    /// <summary>Horizontal = 顶部横条；Vertical = 左侧竖条（滑块沿 Y 轴滑动）。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedControl control) control.QueuePosition(animate: false);
    }

    /// <summary>滑块与选项文字的间距、圆角都由模板提供，这里只负责"选中第几个"。</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private FrameworkElement? _indicator;
    private TranslateTransform? _translate;
    private bool _positionQueued;

    public SegmentedControl()
    {
        // 点击/键盘都由容器转成 SelectedIndex，避免每个页面再写一遍点击处理。
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        Loaded += (_, _) => PositionIndicator(animate: false);
        SizeChanged += (_, _) => PositionIndicator(animate: false);
        ItemContainerGenerator.StatusChanged += (_, _) =>
            PositionIndicator(animate: false);
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is SegmentedItem;

    protected override DependencyObject GetContainerForItemOverride() => new SegmentedItem();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is SegmentedItem container)
        {
            container.IsSelected = IndexOf(container) == SelectedIndex;

            // 竖向模式左对齐、撑满宽度，横条模式居中（沿用容器默认值）
            if (Orientation == Orientation.Vertical)
            {
                container.HorizontalContentAlignment = HorizontalAlignment.Left;
                container.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("PART_Indicator") as FrameworkElement;
        _translate = _indicator?.RenderTransform as TranslateTransform;
        QueuePosition(animate: false);
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        QueuePosition(animate: false);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is SegmentedItem item)
            {
                var index = IndexOf(item);
                if (index >= 0 && index != SelectedIndex)
                {
                    SelectedIndex = index;
                    e.Handled = true;
                }
                return;
            }

            if (ReferenceEquals(node, this)) return;
        }
    }

    private int IndexOf(SegmentedItem container)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(ItemContainerGenerator.ContainerFromIndex(i), container)) return i;
        }
        return -1;
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedControl control) control.QueuePosition(animate: true);
    }

    private void QueuePosition(bool animate)
    {
        if (_positionQueued) return;
        _positionQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _positionQueued = false;
            PositionIndicator(animate);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>把滑块移到当前选中项；布局还没量出宽度时保持上一次的位置，不会闪没。</summary>
    private void PositionIndicator(bool animate)
    {
        if (_indicator is null) return;

        // 同步容器选中态（数据变化后容器可能刚生成）
        for (var i = 0; i < Items.Count; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is SegmentedItem container)
            {
                container.IsSelected = i == SelectedIndex;
            }
        }

        if (SelectedIndex < 0 || ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not FrameworkElement active)
        {
            _indicator.Opacity = 0;
            return;
        }

        if (active.ActualWidth <= 0)
        {
            QueuePosition(animate);
            return;
        }

        UpdateLayout();
        var origin = active.TranslatePoint(new Point(0, 0), this);
        var vertical = Orientation == Orientation.Vertical;

        if (vertical)
        {
            _indicator.Height = active.ActualHeight;
            _indicator.Width = double.NaN;      // 竖向时宽度由模板拉伸
        }
        else
        {
            _indicator.Width = active.ActualWidth;
        }

        // 模板里的 TranslateTransform 是共享且已冻结的，动它必然抛"对象已密封"。
        // 每个控件实例自己 new 一个可动画的 transform。
        if (_indicator.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
        {
            translate = new TranslateTransform();
            _indicator.RenderTransform = translate;
        }
        _translate = translate;

        var axis = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var offset = vertical ? origin.Y : origin.X;

        if (!animate)
        {
            _translate.BeginAnimation(axis, null);
            if (vertical) _translate.Y = offset;
            else _translate.X = offset;
            _indicator.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        _translate.BeginAnimation(axis, new DoubleAnimation(offset, duration) { EasingFunction = ease });
        _indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
    }
}

/// <summary>分段控件里的一个选项容器；选中态由父控件写入，样式在 Themes/Controls.xaml。</summary>
public class SegmentedItem : ContentControl
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(SegmentedItem), new PropertyMetadata(false));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    static SegmentedItem()
    {
        HorizontalContentAlignmentProperty.OverrideMetadata(
            typeof(SegmentedItem), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalContentAlignmentProperty.OverrideMetadata(
            typeof(SegmentedItem), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }
}
