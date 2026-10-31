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
        // 减少动效：直接显示，不做交错
        if (Motion.ReduceMotion)
        {
            foreach (var item in items.Items)
            {
                if (items.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement element)
                {
                    element.Opacity = 1;
                }
            }

            return;
        }

        var index = 0;
        foreach (var item in items.Items)
        {
            if (items.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;

            // 只对前 StaggerMax 项做交错，其余一起出现：否则 376 行要等十几秒
            var staggerIndex = Math.Min(index, Motion.StaggerMax);
            var delay = TimeSpan.FromMilliseconds(staggerIndex * Motion.StaggerStep);
            index++;

            container.Opacity = 0;
            var translate = Motion.EnsureTranslate(container);

            var duration = TimeSpan.FromMilliseconds(Motion.Base + 40);
            container.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = Motion.Enter });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, duration) { BeginTime = delay, EasingFunction = Motion.Enter });
        }
    }
}
