// 全局动效参数：时长阶梯 + 曲线 + 统一入口。
//
// 约定：页面/视图代码不再自己 new DoubleAnimation，一律走这里，
// 这样参数统一、减少动效（ReduceMotion）也能一处关闭。
//
// 曲线选择依据 docs/UI-MOTION-DESIGN.md：稳健风格，不用 ElasticEase/BounceEase。

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orion.App.Animations;

public static class Motion
{
    // ── 时长阶梯（毫秒） ──────────────────────────────────────────────
    public const int Instant = 60;
    public const int Fast = 120;
    public const int Base = 180;
    public const int Page = 220;
    public const int Slow = 320;

    /// <summary>列表交错间隔（毫秒）与最多交错的项数（超过就一起出现，不让用户等）。</summary>
    public const int StaggerStep = 24;
    public const int StaggerMax = 8;

    private static bool _reduceMotion;

    /// <summary>减少动效：由设置驱动，也可被系统"显示动画"设置覆盖。</summary>
    public static bool ReduceMotion
    {
        get => _reduceMotion;
        set => _reduceMotion = value || !SystemParameters.ClientAreaAnimation;
    }

    /// <summary>系统是否允许动画（Windows 的"显示动画"开关）。</summary>
    public static bool SystemAllowsMotion => SystemParameters.ClientAreaAnimation;

    // ── 曲线（冻结后跨控件复用） ──────────────────────────────────────
    public static readonly IEasingFunction Enter = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Exit = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Soft = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Press = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>氛围动效专用的轻微回弹（振幅很小，稳）。</summary>
    public static readonly IEasingFunction Emphasis = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 });

    private static IEasingFunction Freeze(IEasingFunction ease)
    {
        if (ease is Freezable freezable && freezable.CanFreeze) freezable.Freeze();
        return ease;
    }

    /// <summary>ReduceMotion 时把时长压成 0（动画立刻到位，但事件/状态仍会触发）。</summary>
    public static Duration Pick(int milliseconds) =>
        new(TimeSpan.FromMilliseconds(ReduceMotion ? 0 : milliseconds));

    public static DoubleAnimation Tween(double to, int milliseconds, IEasingFunction? ease = null) =>
        new(to, Pick(milliseconds)) { EasingFunction = ease ?? Enter };

    public static DoubleAnimation Tween(double from, double to, int milliseconds, IEasingFunction? ease = null) =>
        new(from, to, Pick(milliseconds)) { EasingFunction = ease ?? Enter };

    // ── 常用组合 ─────────────────────────────────────────────────────
    /// <summary>淡入（不位移）。</summary>
    public static void FadeIn(UIElement element, int milliseconds = Base, double from = 0, double to = 1)
    {
        if (element is null) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, to, Pick(milliseconds))
        {
            EasingFunction = Enter,
        });
    }

    /// <summary>淡入 + 上滑（页面/详情/卡片入场）。</summary>
    public static void SlideFadeIn(UIElement element, double dy = 8, int milliseconds = Page)
    {
        if (element is null) return;

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Pick(milliseconds))
        {
            EasingFunction = Enter,
        });

        var translate = EnsureTranslate(element);
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Pick(milliseconds))
        {
            EasingFunction = Enter,
        });
    }

    /// <summary>淡出（页面退出、覆盖层收起）。</summary>
    public static void FadeOut(UIElement element, int milliseconds = Fast)
    {
        if (element is null) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, Pick(milliseconds))
        {
            EasingFunction = Exit,
        });
    }

    /// <summary>按压缩放：按下 0.97（60ms），松开回 1（180ms）。</summary>
    public static void PressScale(UIElement element, bool down, double scale = 0.97)
    {
        if (element is not FrameworkElement target) return;

        var transform = EnsureScale(target);
        var to = down ? scale : 1.0;
        var duration = down ? Instant : Base;
        var ease = down ? Press : Enter;

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(to, duration, ease));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(to, duration, ease));
    }

    /// <summary>悬停轻微上浮（1px，稳健风格不做大位移）。</summary>
    public static void HoverLift(FrameworkElement element, bool up, double dy = -1)
    {
        if (!ReduceMotion) { /* 正常路径 */ }

        var translate = EnsureTranslate(element);
        translate.BeginAnimation(TranslateTransform.YProperty,
            Tween(up ? dy : 0, Fast, Soft));
    }

    /// <summary>
    /// 氛围动效：给元素来一次"脉冲"（缩放 1 → 1.06 → 1）。
    /// 用在启动按钮点击、安装成功这类需要一点呼吸感的时刻。
    /// </summary>
    public static void Pulse(FrameworkElement element, double peak = 1.06, int milliseconds = 260)
    {
        if (element is null || ReduceMotion) return;

        var transform = EnsureScale(element);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)) };

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.45), Soft));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Enter));

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>数字补间（下载量、进度百分比这类数字变化）。</summary>
    public static void TweenDouble(DependencyObject target, DependencyProperty property, double to,
        int milliseconds = Base)
    {
        if (target is not Animatable animatable) return;
        animatable.BeginAnimation(property, Tween(to, milliseconds, Soft));
    }

    // ── transform 管理：模板里的 transform 会被冻结，必须每实例自建 ──
    public static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform translate && !translate.IsFrozen) return translate;

        translate = new TranslateTransform();
        element.RenderTransform = translate;
        return translate;
    }

    public static ScaleTransform EnsureScale(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform scale && !scale.IsFrozen) return scale;

        // 同时需要位移与缩放的场景用 TransformGroup
        if (element.RenderTransform is TransformGroup group)
        {
            var existing = group.Children.OfType<ScaleTransform>().FirstOrDefault(t => !t.IsFrozen)
                           ?? group.Children.OfType<ScaleTransform>().FirstOrDefault();

            if (existing is not null && !existing.IsFrozen) return existing;

            var added = new ScaleTransform();
            group.Children.Add(added);
            return added;
        }

        scale = new ScaleTransform();
        if (element.RenderTransformOrigin == new Point(0, 0))
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        element.RenderTransform = scale;
        return scale;
    }
}
