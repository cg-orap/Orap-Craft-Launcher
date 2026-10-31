// 数值补间：RangeBase.Value / TextBlock 文本这类"数值一变就直接跳"的地方，改成 200ms 缓动。
//
// 用法：
//   <ProgressBar anim:AnimatedValue.To="{Binding Fraction}" />
//   <TextBlock anim:TextFade.Text="{Binding Status}" />          <!-- 文字变化时淡入 -->
//   <TextBlock anim:CountUp.To="{Binding Mods.Count}" />          <!-- 数字 count-up（首屏统计用） -->

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Orion.App.Animations;

public static class AnimatedValue
{
    /// <summary>ProgressBar / Slider 的数值补间。</summary>
    public static readonly DependencyProperty ToProperty = DependencyProperty.RegisterAttached(
        "To", typeof(double), typeof(AnimatedValue), new PropertyMetadata(0.0, OnToChanged));

    public static void SetTo(DependencyObject element, double value) => element.SetValue(ToProperty, value);

    public static double GetTo(DependencyObject element) => (double)element.GetValue(ToProperty);

    private static void OnToChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase range) return;

        var target = (double)e.NewValue;
        if (double.IsNaN(target) || double.IsInfinity(target)) return;

        if (Motion.ReduceMotion)
        {
            range.BeginAnimation(RangeBase.ValueProperty, null);
            range.Value = target;
            return;
        }

        // 从当前显示值缓动到新值；首次（无动画时）也从 0 起，避免一进来就满格
        var from = range.Value;
        range.BeginAnimation(RangeBase.ValueProperty,
            Motion.Tween(from, target, Motion.Page + 80, Motion.Soft));
    }
}

/// <summary>文本变化时淡入：状态行、耗时、计数这类。</summary>
public static class TextFade
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextFade), new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        if (e.NewValue is not string text) return;

        block.Text = text;

        // 首次赋值不播动画
        if (e.OldValue is null) return;

        block.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.25, 1, Motion.Pick(Motion.Fast)) { EasingFunction = Motion.Soft });
    }
}

/// <summary>数字 count-up：首屏统计数字从 0 滚到目标值（只对少量数字用，不要对列表每行用）。</summary>
public static class CountUp
{
    public static readonly DependencyProperty ToProperty = DependencyProperty.RegisterAttached(
        "To", typeof(double), typeof(CountUp), new PropertyMetadata(0.0, OnToChanged));

    public static void SetTo(DependencyObject element, double value) => element.SetValue(ToProperty, value);

    public static double GetTo(DependencyObject element) => (double)element.GetValue(ToProperty);

    private static void OnToChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        var target = (double)e.NewValue;
        if (Motion.ReduceMotion)
        {
            block.Text = Format(target);
            return;
        }

        var animation = new DoubleAnimation(0, target, Motion.Pick(320)) { EasingFunction = Motion.Enter };

        // 用动画驱动一个中间值，再在回调里写文本
        var proxy = new CountProxy(block);
        animation.Completed += (_, _) => block.Text = Format(target);
        proxy.Begin(animation);
    }

    private static string Format(double value) =>
        Math.Abs(value) >= 1
            ? ((long)Math.Round(value)).ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>把动画值转成文本的代理（Freezable 才能挂动画）。</summary>
    private sealed class CountProxy : Animatable
    {
        private readonly TextBlock _block;

        public CountProxy(TextBlock block) => _block = block;


        protected override Freezable CreateInstanceCore() => new CountProxy(_block);
        public void Begin(DoubleAnimation animation) =>
            BeginAnimation(ValueProperty, animation);

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(CountProxy),
            new PropertyMetadata(0.0, OnValueChanged));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CountProxy proxy && e.NewValue is double value)
            {
                proxy._block.Text = Format(value);
            }
        }
    }
}
