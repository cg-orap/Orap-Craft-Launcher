using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orion.App.Animations;

/// <summary>
/// 切换版本（实例）时的过渡动画。
///
/// 设计原则是"柔和、不闪"：
/// · 不做明显的变暗再变亮（之前 1 → 0.45 → 1 看起来像闪一下）；
/// · 只做一次缓慢的淡入 + 轻微上浮 + 极小幅度放大，340ms 走完；
/// · 缓动统一 ease-out，收尾自然。
///
/// 用法（XAML 一行）：
///   &lt;Border anim:InstanceSwitch.Watch="{Binding CurrentInstance}" /&gt;
///
/// 覆盖所有切换入口（首页下拉框 / 最近游玩 / 侧栏列表 / 版本管理页下拉框），
/// 因为它们最终都会改 MainViewModel.CurrentInstance，一处绑定全部生效。
/// 只动 Opacity / RenderTransform（不触发重新布局）；ReduceMotion 时整体跳过。
/// </summary>
public static class InstanceSwitch
{
    /// <summary>被监视的值：换了就播一次过渡。</summary>
    public static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(object), typeof(InstanceSwitch),
        new PropertyMetadata(null, OnWatchChanged));

    public static void SetWatch(DependencyObject element, object? value) => element.SetValue(WatchProperty, value);

    public static object? GetWatch(DependencyObject element) => element.GetValue(WatchProperty);

    /// <summary>上浮距离（像素）。整块卡片用 10，小控件用 4。</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(InstanceSwitch), new PropertyMetadata(10.0));

    public static void SetOffset(DependencyObject element, double value) => element.SetValue(OffsetProperty, value);

    public static double GetOffset(DependencyObject element) => (double)element.GetValue(OffsetProperty);

    private static void OnWatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // 首次绑定（null → 初始值）不播，避免启动时整页闪一下
        if (e.OldValue is null) return;
        if (Motion.ReduceMotion) return;

        Play(element, GetOffset(element));
    }

    private static void Play(FrameworkElement element, double offset)
    {
        // 埋点：方便验证"每条切换路径都真的播了动画"（日志里数 anim/instance-switch）
        Orion.Core.Diagnostics.AppLog.Info("anim", "instance-switch",
            ("kind", element.GetType().Name),
            ("id", element.Name is { Length: > 0 } n ? n : "-"));

        // 340ms 一次性淡入：中途不变暗，观感更像"内容轻轻换了一页"
        var total = TimeSpan.FromMilliseconds(340);

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.62, 1, total) { EasingFunction = Motion.Soft },
            HandoffBehavior.SnapshotAndReplace);

        // 只用一个 TranslateTransform 做位移（并通过 Motion.EnsureTranslate 复用已有的），
        // 绝不把 RenderTransform 换成 TransformGroup：
        // 样式里的悬停动效会取 RenderTransform.Y，换成组之后就解析失败 →
        // "无法解析属性路径 RenderTransform.Y"。放大不再是必需，去掉它。
        var translate = Motion.EnsureTranslate(element);
        translate.Y = offset;

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offset, 0, total) { EasingFunction = Motion.Soft });

        // 动画结束后把位移归零，避免影响后续布局测量
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = total + TimeSpan.FromMilliseconds(80),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
        };

        timer.Start();
    }
}
