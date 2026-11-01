using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orion.App.Animations;

/// <summary>
/// 切换版本（实例）时的过渡动画。
///
/// 只要绑定的值变了就播一次：淡出 → 淡入，同时从下方轻微上浮 + 一点点放大，
/// 让人看得出"内容换了"，而不是版本名/内存数字瞬间被替换。
///
/// 用法（XAML 一行）：
///   &lt;Border anim:InstanceSwitch.Watch="{Binding CurrentInstance}" /&gt;
///
/// 覆盖所有切换入口：首页下拉框、最近游玩、侧栏列表、版本管理页的下拉框 ——
/// 它们最终都会改 MainViewModel.CurrentInstance，一处绑定全部生效。
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

    /// <summary>上浮距离（像素）。整块卡片用 8，小控件用 4。</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(InstanceSwitch), new PropertyMetadata(8.0));

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

        var total = TimeSpan.FromMilliseconds(280);

        // ── 透明度：1 → 0.45（70ms）→ 1（剩下的时间）──
        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), Motion.Exit));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(total), Motion.Soft));

        element.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);

        // ── 变换：位移 + 缩放放在一个 TransformGroup 里（不破坏已有的变换）──
        var translate = new TranslateTransform(0, offset);
        var scale = new ScaleTransform(0.985, 0.985);

        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = group;

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offset, 0, total) { EasingFunction = Motion.Soft });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, total) { EasingFunction = Motion.Soft });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, total) { EasingFunction = Motion.Soft });

        // 动画结束后把变换归零并摘掉，避免影响后续布局测量
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = total + TimeSpan.FromMilliseconds(60),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };

        timer.Start();
    }
}
