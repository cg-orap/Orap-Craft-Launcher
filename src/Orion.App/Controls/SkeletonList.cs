// 骨架屏：列表首次加载（还没有任何数据）时的占位。
//
// 用法：<controls:SkeletonList Rows="5" Visibility="{Binding ShowSkeleton, Converter={StaticResource BoolToVisibility}}" />
//
// 设计意图见 docs/UI-MOTION-DESIGN.md P5：骨架屏只在"真的没数据"时出现，
// 有缓存/已有列表时立刻换成真数据（否则会闪一下，比空白更烦躁）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Orion.App.Animations;

namespace Orion.App.Controls;

public sealed class SkeletonList : StackPanel
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(int), typeof(SkeletonList),
        new PropertyMetadata(5, OnRowsChanged));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public SkeletonList()
    {
        Orientation = Orientation.Vertical;
        IsHitTestVisible = false;
        MinHeight = 220;   // 骨架屏本身要有高度，否则卡片会塌成一条
        Build();
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SkeletonList list) list.Build();
    }

    private void Build()
    {
        Children.Clear();

        var barBrush = TryBrush("BorderBrushSoft") ?? TryBrush("SurfaceAltBg")
                       ?? new SolidColorBrush(Color.FromArgb(40, 120, 130, 150));

        for (var i = 0; i < Math.Clamp(Rows, 1, 20); i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10), Height = 46 };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = barBrush,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 40,
                Width = 40,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

            // 三行灰条：标题 60%、副标题 90%、标签 30%，宽度错开才有"正在加载"的味道
            lines.Children.Add(MakeBar(barBrush, 0.55, 12, new Thickness(0, 0, 0, 7)));
            lines.Children.Add(MakeBar(barBrush, 0.85, 9, new Thickness(0, 0, 0, 7)));
            lines.Children.Add(MakeBar(barBrush, 0.28, 8, new Thickness(0)));

            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);
            Children.Add(row);
        }

        StartShimmer();
    }

    /// <summary>
    /// 按比例生成的灰条。宽度用"星号列"分配，绝对宽度不由内容决定——
    /// 否则骨架屏会把所在的列撑宽，页面布局会跟着变形（踩过一次）。
    /// </summary>
    private static FrameworkElement MakeBar(Brush brush, double widthFactor, double height, Thickness margin)
    {
        var host = new Grid { Margin = margin };

        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widthFactor, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - widthFactor, GridUnitType.Star) });

        var bar = new Rectangle
        {
            Height = height,
            RadiusX = height / 2,
            RadiusY = height / 2,
            Fill = brush,
            Opacity = 0.9,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Grid.SetColumn(bar, 0);
        host.Children.Add(bar);
        return host;
    }

    private void StartShimmer()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        // 减少动效：静态灰块即可，不做呼吸
        if (Motion.ReduceMotion) return;

        var shimmer = new DoubleAnimation(0.55, 1, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Soft,
        };

        BeginAnimation(OpacityProperty, shimmer);
    }

    private static Brush? TryBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;

        // 先构造，资源可能还没合并；退化成中性灰
        return new SolidColorBrush(Color.FromArgb(20, 120, 130, 150));
    }
}
