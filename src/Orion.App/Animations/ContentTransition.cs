// 内容切换动效：把「属性一变就硬切」的地方改成淡入 + 轻微滑入。
//
// 用法（XAML 一行）：
//   <ContentControl anim:ContentTransition.Watch="{Binding CurrentPage}" />
//   <ContentControl anim:ContentTransition.Watch="{Binding TabIndex}" anim:ContentTransition.Offset="6" />
//
// 只动 Opacity 与 RenderTransform，不触发布局；ReduceMotion 时由 Motion 统一下调为 0。

using System.Windows;
using System.Windows.Media;

namespace Orion.App.Animations;

public static class ContentTransition
{
    /// <summary>被监视的值：变了就播一次淡入。</summary>
    public static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(object), typeof(ContentTransition),
        new PropertyMetadata(null, OnWatchChanged));

    public static void SetWatch(DependencyObject element, object? value) => element.SetValue(WatchProperty, value);

    public static object? GetWatch(DependencyObject element) => element.GetValue(WatchProperty);

    /// <summary>滑入位移（像素）。页面用 12，页签/局部内容用 6。</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(ContentTransition), new PropertyMetadata(8.0));

    public static void SetOffset(DependencyObject element, double value) => element.SetValue(OffsetProperty, value);

    public static double GetOffset(DependencyObject element) => (double)element.GetValue(OffsetProperty);

    /// <summary>时长（毫秒），默认 200。</summary>
    public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached(
        "Duration", typeof(int), typeof(ContentTransition), new PropertyMetadata(200));

    public static void SetDuration(DependencyObject element, int value) => element.SetValue(DurationProperty, value);

    public static int GetDuration(DependencyObject element) => (int)element.GetValue(DurationProperty);

    private static void OnWatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        // 首次绑定（null → 初始值）不播动画，避免启动时整页闪一下
        if (e.OldValue is null) return;

        Motion.SlideFadeIn(element, GetOffset(d), GetDuration(d));

        // 滑入结束后把 RenderTransform 归零，避免影响后续布局测量
        element.Dispatcher.BeginInvoke(() =>
        {
            if (element.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
