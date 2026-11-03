using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Orion.App.Animations;

namespace Orion.App.Controls;

/// <summary>
/// 启动动画：图标落位 + 光环描边 + 字标逐字升起 + 呼吸光晕 + 进度扫光 + 阶段文字。
///
/// 设计要点：
/// · 总时长约 1.2 s，比"转圈等"更有仪式感，但不会让启动变慢（它在 UI 线程外等真实初始化）；
/// · 三层景深：背景光晕（慢呼吸）、图标（BackEase 落位）、文字（错峰淡入）；
/// · 进度条用真实阶段驱动（0..1），扫光只是装饰；
/// · ReduceMotion 打开时全部退化为静态，只保留进度与文字。
/// </summary>
public sealed class BootAnimation : Grid
{
    private readonly Border _halo;
    private readonly Border _logoFrame;
    private readonly System.Windows.Shapes.Path _ring;
    private readonly StackPanel _wordmark;
    private readonly Border _track;
    private readonly Border _fill;
    private readonly Border _sweep;
    private readonly TextBlock _phase;

    private readonly ScaleTransform _logoScale = new(0.86, 0.86);
    private readonly RotateTransform _logoRotate = new(-6);

    public BootAnimation()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        // ── 背景光晕：两层叠加，制造纵深 ──
        _halo = new Border
        {
            Width = 460,
            Height = 460,
            CornerRadius = new CornerRadius(230),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5,
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(WithAlpha(0x40), 0),
                    new GradientStop(WithAlpha(0x00), 1),
                },
            },
            Effect = new BlurEffect { Radius = 40 },
            // 关键：BlurEffect 每帧重算会把 UI 线程占满（实测启动动画卡 1 秒多）。
            // 加 BitmapCache 后模糊只栅格化一次，呼吸动画只是合成透明度。
            CacheMode = new System.Windows.Media.BitmapCache(),
            IsHitTestVisible = false,
        };

        // ── 图标 + 描边环 ──
        _logoFrame = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("SurfaceBg"),
            BorderBrush = new SolidColorBrush(WithAlpha(0x33)),
            BorderThickness = new Thickness(1),
            RenderTransform = new TransformGroup { Children = { _logoScale, _logoRotate } },
            RenderTransformOrigin = new Point(0.5, 0.5),
            CacheMode = new System.Windows.Media.BitmapCache(),
            Child = new Image
            {
                Source = IconSource(),
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(8),
            },
        };

        _ring = new System.Windows.Shapes.Path
        {
            Width = 132,
            Height = 132,
            Stretch = Stretch.Uniform,
            Stroke = ThemeBrush("AccentBrush"),
            StrokeThickness = 1.6,
            StrokeDashArray = [1, 0],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M 66,4 A 62,62 0 1 1 65.9,4"),
            IsHitTestVisible = false,
        };

        // ── 字标：逐字升起 ──
        _wordmark = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
        };

        var title = "Orap Craft Launcher";
        for (var i = 0; i < title.Length; i++)
        {
            var glyph = title[i] == ' ' ? "\u00A0" : title[i].ToString();
            var letter = new TextBlock
            {
                Text = glyph,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("TextBrush"),
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 10),
            };

            _wordmark.Children.Add(letter);
        }

        // ── 阶段文字 ──
        _phase = new TextBlock
        {
            Text = "正在启动…",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = ThemeBrush("MutedTextBrush"),
            Opacity = 0,
        };

        // ── 进度条：轨道 + 填充 + 扫光 ──
        _fill = new Border
        {
            Width = 0,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush("AccentBrush"),
        };

        _sweep = new Border
        {
            Width = 56,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(WithAlpha(0x00), 0),
                    new GradientStop(Colors.White, 0.5),
                    new GradientStop(WithAlpha(0x00), 1),
                },
            },
            IsHitTestVisible = false,
        };

        var trackContent = new Grid();
        trackContent.Children.Add(_fill);
        trackContent.Children.Add(_sweep);

        _track = new Border
        {
            Width = 240,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(WithAlpha(0x26)),
            Child = trackContent,
        };

        // 图标 + 光环叠在一起
        var logoLayer = new Grid();
        logoLayer.Children.Add(_ring);
        logoLayer.Children.Add(_logoFrame);

        var column = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        column.Children.Add(logoLayer);
        column.Children.Add(_wordmark);
        column.Children.Add(_phase);
        column.Children.Add(_track);

        Children.Add(_halo);
        Children.Add(column);

        // 颜色走 DynamicResource：构造时机可能早于主题字典合并，
        // 直接 TryFindResource 会拿到错误的值（例如字标变白看不见）。
        _logoFrame.SetResourceReference(Border.BackgroundProperty, "SurfaceBg");
        _logoFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
        _ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _phase.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _track.Background = new SolidColorBrush(WithAlpha(0x26));

        foreach (var letter in _wordmark.Children.OfType<TextBlock>())
        {
            letter.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }

        Loaded += (_, _) => StartIntro();
    }

    /// <summary>0..1 的真实启动进度（由 ViewModel 提供）。</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(BootAnimation),
        new PropertyMetadata(0d, OnProgressChanged));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>当前阶段文字。</summary>
    public static readonly DependencyProperty PhaseTextProperty = DependencyProperty.Register(
        nameof(PhaseText), typeof(string), typeof(BootAnimation),
        new PropertyMetadata("正在启动…", OnPhaseTextChanged));

    public string PhaseText
    {
        get => (string)GetValue(PhaseTextProperty);
        set => SetValue(PhaseTextProperty, value);
    }

    /// <summary>进入 / 退场：遮罩显示时播放。</summary>
    private void StartIntro()
    {
        if (Motion.ReduceMotion)
        {
            // 静态版本：只保留图标与进度
            _logoScale.ScaleX = _logoScale.ScaleY = 1;
            _logoRotate.Angle = 0;
            _halo.Opacity = 0.35;
            _phase.Opacity = 0.85;
            _ring.Opacity = 0.4;

            foreach (var letter in _wordmark.Children.OfType<TextBlock>())
            {
                letter.Opacity = 1;
                if (letter.RenderTransform is TranslateTransform t) t.Y = 0;
            }

            return;
        }

        // 图标：落位（缩放 + 轻微回正旋转）
        var settle = new Storyboard();
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        AddEase(settle, _logoScale, "ScaleX", 0.86, 1, 320, pop);
        AddEase(settle, _logoScale, "ScaleY", 0.86, 1, 320, pop);
        AddEase(settle, _logoRotate, "Angle", -6, 0, 320, Motion.Enter);

        // 光环描边：从 0 到满，稍晚于图标
        var draw = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            EasingFunction = Motion.Enter,
        };
        _ring.BeginAnimation(UIElement.OpacityProperty, draw);

        // 字标：逐字升起（24 ms 一步，与列表 stagger 一致）
        var index = 0;
        foreach (var letter in _wordmark.Children.OfType<TextBlock>())
        {
            var delay = TimeSpan.FromMilliseconds(220 + (index * 24));
            letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = Motion.Enter,
                BeginTime = delay,
            });

            if (letter.RenderTransform is TranslateTransform translate)
            {
                translate.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.Emphasis, BeginTime = delay });
            }

            index++;
        }

        // 阶段文字：最后淡入
        _phase.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = Motion.Soft,
            BeginTime = TimeSpan.FromMilliseconds(220 + (index * 24) + 60),
        });

        // 背景光晕：慢呼吸（2.6 s 一个来回，无限循环）
        var breath = new DoubleAnimation(0.34, 0.6, new Duration(TimeSpan.FromMilliseconds(1300)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Soft,
        };
        _halo.BeginAnimation(OpacityProperty, breath);

        // 扫光：沿进度条来回扫，作为"还在动"的提示
        var trackWidth = _track.Width - _sweep.Width;
        var sweep = new DoubleAnimation(-_sweep.Width, trackWidth, new Duration(TimeSpan.FromMilliseconds(1400)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Soft,
            BeginTime = TimeSpan.FromMilliseconds(420),
        };
        _sweep.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = Motion.Soft,
            BeginTime = TimeSpan.FromMilliseconds(420),
        });
        _sweep.RenderTransform = new TranslateTransform();
        ((TranslateTransform)_sweep.RenderTransform).BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BootAnimation boot) return;

        var value = Math.Clamp((double)e.NewValue, 0, 1);

        // 走到 100% 就收起扫光，不然进度条中间会留一块白斑
        boot._sweep.BeginAnimation(OpacityProperty, new DoubleAnimation(
            value >= 1 ? 0 : 0.85, TimeSpan.FromMilliseconds(160)));

        var target = Math.Clamp((double)e.NewValue, 0, 1) * (boot._track.Width - 2);

        if (Motion.ReduceMotion)
        {
            boot._fill.Width = Math.Max(0, target);
            return;
        }

        var animation = new DoubleAnimation(boot._fill.Width, Math.Max(0, target), TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = Motion.Soft,
            FillBehavior = FillBehavior.HoldEnd,
        };

        boot._fill.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnPhaseTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BootAnimation boot) return;

        var text = e.NewValue as string ?? "";
        boot._phase.Text = text;

        if (Motion.ReduceMotion) return;

        // 换阶段时文字轻微上移淡入，避免"啪"地换掉
        var translate = boot._phase.RenderTransform as TranslateTransform ?? new TranslateTransform();
        boot._phase.RenderTransform = translate;

        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = Motion.Soft,
        });

        boot._phase.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0.9, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = Motion.Soft,
        });
    }

    private static void AddEase(Storyboard board, Animatable target, string path, double from, double to,
        int milliseconds, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(path));
        board.Children.Add(animation);
        board.Begin();
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Color WithAlpha(byte alpha)
    {
        var color = Application.Current.TryFindResource("AccentColor") is Color accent
            ? accent
            : Color.FromRgb(0x4C, 0x8B, 0xF5);

        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static ImageSource? IconSource()
    {
        try
        {
            return new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/app-icon.png", UriKind.Absolute));
        }
        catch (Exception ex) when (ex is UriFormatException or IOException)
        {
            return null;
        }
    }
}
