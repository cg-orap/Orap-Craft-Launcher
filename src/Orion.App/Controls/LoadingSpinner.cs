// 加载动画：一段沿圆环旋转的弧线。
//
// 用法：<controls:LoadingSpinner Width="18" Height="18" />（Size 自动设为宽高）
// 设计：900ms/圈、匀速（旋转类动画用 Linear，不用缓动，否则会"一顿一顿"）；
// 颜色取 AccentBrush，减少动效时改为静态弧线（不旋转），避免转圈引起不适。

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Orion.App.Animations;

namespace Orion.App.Controls;

public sealed class LoadingSpinner : System.Windows.Controls.Grid
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LoadingSpinner),
        new PropertyMetadata(18.0, OnSizeChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(LoadingSpinner),
        new PropertyMetadata(2.4, OnSizeChanged));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>弧线颜色（默认强调色由调用方传入）。</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LoadingSpinner),
        new PropertyMetadata(Brushes.Gray, OnStrokeChanged));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingSpinner spinner && e.NewValue is Brush brush)
        {
            spinner._arc.Stroke = brush;
            spinner._arc.Rebuild(spinner.Size, spinner.Thickness);
        }
    }

    private readonly Arc _arc;

    public LoadingSpinner()
    {
        // 外形尺寸交给外部（Width/Height）或 Size 决定
        Width = Size;
        Height = Size;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        _arc = new Arc { Stroke = Stroke };
        Children.Add(_arc);

        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => _arc.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingSpinner spinner) spinner.Rebuild();
    }

    private void Rebuild()
    {
        Width = Size;
        Height = Size;
        _arc.Rebuild(Size, Thickness);
        StartSpin();
    }

    private void StartSpin()
    {
        if (_arc.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            _arc.RenderTransformOrigin = new Point(0.5, 0.5);
            _arc.RenderTransform = rotate;
        }

        rotate.BeginAnimation(RotateTransform.AngleProperty, null);

        if (Motion.ReduceMotion)
        {
            rotate.Angle = 0;
            _arc.Opacity = 0.55;
            return;
        }

        _arc.Opacity = 1;
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromMilliseconds(900)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = null, // 匀速：旋转类动画不该有缓入缓出
        });
    }

    /// <summary>内部弧线：3/4 圆的一段，端点圆头。几何放进 DefiningGeometry，Shape 才会正确测量与描边。</summary>
    private sealed class Arc : Shape
    {
        private double _size = 18;
        private double _thickness = 2.4;

        public Arc()
        {
            StrokeStartLineCap = PenLineCap.Round;
            StrokeEndLineCap = PenLineCap.Round;
            StrokeThickness = _thickness;
            Fill = null;
        }

        public void Rebuild(double size, double thickness)
        {
            _size = size;
            _thickness = thickness;
            StrokeThickness = thickness;
            InvalidateVisual();
        }

        protected override Geometry DefiningGeometry
        {
            get
            {
                var radius = Math.Max(0.5, (_size - _thickness) / 2);
                var centre = new Point(_size / 2, _size / 2);

                var startAngle = -90.0 * Math.PI / 180;
                const double sweep = 270.0 * Math.PI / 180;

                var start = new Point(centre.X + radius * Math.Cos(startAngle),
                                      centre.Y + radius * Math.Sin(startAngle));
                var endAngle = startAngle + sweep;
                var end = new Point(centre.X + radius * Math.Cos(endAngle),
                                    centre.Y + radius * Math.Sin(endAngle));

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI,
                              SweepDirection.Clockwise, true, false);
                }

                geometry.Freeze();
                return geometry;
            }
        }
    }
}
