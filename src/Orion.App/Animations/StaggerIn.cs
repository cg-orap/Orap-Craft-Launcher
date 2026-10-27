using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orion.App.Animations;

/// <summary>
/// 列表逐条淡入（stagger）：给 ItemsControl 挂上 <c>anim:StaggerIn.IsEnabled="True"</c> 即可，
/// 每项延迟 40ms、220ms 内完成透明 0→1 与上移 10→0，只动 transform/opacity 不动布局。
/// </summary>
public static class StaggerIn
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(StaggerIn),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items || e.NewValue is not true) return;

        items.Loaded += (_, _) => Animate(items);
        items.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (items.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                Animate(items);
            }
        };
    }

    private static void Animate(ItemsControl items)
    {
        var index = 0;
        foreach (var item in items.Items)
        {
            if (items.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;

            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 12) * 40);
            index++;

            container.Opacity = 0;
            var transform = container.RenderTransform as TranslateTransform ?? new TranslateTransform();
            container.RenderTransform = transform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            container.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = delay, EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = delay, EasingFunction = ease });
        }
    }
}
