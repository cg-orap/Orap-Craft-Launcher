using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using Orion.App.ViewModels;
using Orion.Core;
using Orion.Core.Diagnostics;

namespace Orion.App;

/// <summary>
/// Application entry point.
///
/// Startup is explicit (no <c>StartupUri</c>) because the launcher has to build its core
/// context and load accounts and instances before the first window is useful.
/// </summary>
public partial class App : Application
{
    private LauncherContext? _context;

    public static MainViewModel? Shell { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            // "--root <dir>" lets a user (or a test) pin the data directory explicitly.
            var rootIndex = Array.FindIndex(e.Args, a => a.Equals("--root", StringComparison.OrdinalIgnoreCase));
            var rootOverride = rootIndex >= 0 && rootIndex + 1 < e.Args.Length ? e.Args[rootIndex + 1] : null;

            var clock = System.Diagnostics.Stopwatch.StartNew();

            _context = LauncherContext.Create(rootOverride);
            var contextMs = clock.ElapsedMilliseconds;

            var window = new MainWindow();
            MainWindow = window;
            Services.WindowPlacement.FitToMonitor(window);
            var windowMs = clock.ElapsedMilliseconds;

            var vmClock = System.Diagnostics.Stopwatch.StartNew();
            var viewModel = new MainViewModel(_context);
            Shell = viewModel;
            var vmMs = vmClock.ElapsedMilliseconds;

            ApplyTheme(_context.Settings.Theme, _context.Settings.Accent);
            var themeMs = vmClock.ElapsedMilliseconds;
        Animations.Motion.ReduceMotion = _context.Settings.ReduceMotion;

            window.DataContext = viewModel;
            var bindMs = vmClock.ElapsedMilliseconds;

            window.Show();
            var showMs = vmClock.ElapsedMilliseconds;

            // 启动期间测「UI 线程最大停顿」：动画卡不卡看这个数，而不是看总耗时。
            StartUiStallProbe();

            // 启动耗时埋点：定位"双击后要等一会儿才看到窗口"到底花在哪一步
            Orion.Core.Diagnostics.AppLog.Info("startup", "timing",
                ("contextMs", contextMs),
                ("windowMs", windowMs),
                ("vmMs", vmMs),
                ("themeMs", themeMs),
                ("bindMs", bindMs),
                ("showMs", showMs),
                ("shownMs", clock.ElapsedMilliseconds));

            await viewModel.InitializeAsync().ConfigureAwait(true);

            // Deep link: "OrapCraftLauncher.exe --page versions" opens a page directly.
            var pageIndex = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
            if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
            {
                viewModel.Navigate(e.Args[pageIndex + 1]);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("app", "startup-failed", ex);
            Dialogs.Error("启动器初始化失败", $"启动器初始化失败：{ex.Message}", "详细信息已写入日志目录。");
            Shutdown(1);
        }
    }


    /// <summary>
    /// 启动阶段每 15 ms 打一次点，记录 UI 线程最长的一次停顿。
    /// 动画卡顿 = UI 线程被同步 IO 占住；把这个数记进日志，改完能立刻验证。
    /// </summary>
    private static void StartUiStallProbe()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = 0L;
        var worst = 0L;
        var ticks = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };

        timer.Tick += (_, _) =>
        {
            var now = clock.ElapsedMilliseconds;
            var gap = now - last;
            last = now;
            ticks++;

            if (ticks > 2 && gap > worst) worst = gap;

            if (ticks > 2 && gap > 100)
            {
                Orion.Core.Diagnostics.AppLog.Info("startup", "ui-gap", ("atMs", now), ("gapMs", gap));
            }

            // 6 秒后收工（启动早就结束）
            if (now < 6000) return;

            timer.Stop();
            Orion.Core.Diagnostics.AppLog.Info("startup", "ui-stall",
                ("maxGapMs", worst),
                ("samples", ticks));
        };

        timer.Start();
    }

    /// <summary>
    /// Fits the window into the current monitor's work area.
    ///
    /// The default size is expressed in device independent pixels, so on a 150% scaled small
    /// screen it can be wider than the desktop. The check is repeated once the window is loaded
    /// because <see cref="SystemParameters.WorkArea"/> is only DPI-correct after the window has
    /// a monitor/DPI context.
    /// </summary>
    private static void ClampToWorkingArea(Window window)
    {
        static void Fit(Window w)
        {
            var area = SystemParameters.WorkArea;
            var maxWidth = Math.Max(720, area.Width - 16);
            var maxHeight = Math.Max(520, area.Height - 16);

            if (w.Width > maxWidth) w.Width = maxWidth;
            if (w.Height > maxHeight) w.Height = maxHeight;

            w.MinWidth = Math.Min(w.MinWidth, maxWidth);
            w.MinHeight = Math.Min(w.MinHeight, maxHeight);

            // Re-centre: a window that was centred while still too large can sit off-screen.
            if (w.Left + w.Width > area.Right) w.Left = Math.Max(area.Left, area.Right - w.Width);
            if (w.Top + w.Height > area.Bottom) w.Top = Math.Max(area.Top, area.Bottom - w.Height);
            if (w.Left < area.Left) w.Left = area.Left;
            if (w.Top < area.Top) w.Top = area.Top;
        }

        Fit(window);
        window.Loaded += (_, _) => Fit(window);
    }

    /// <summary>
    /// Makes the native title bar follow the theme and enables the DWM backdrop (Mica / Acrylic on
    /// Windows 11) so the translucent palette picks up the desktop behind the window. Older Windows
    /// builds simply ignore the attributes.
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var current = (Current as App)?._context?.Settings.Theme;
        var dark = !(string.Equals(current, "Light", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(current, "Frost", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(current, "Snow", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(current, "Paper", StringComparison.OrdinalIgnoreCase));
            var darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref darkValue, sizeof(int));   // immersive dark title bar

            var backdrop = 3;                                              // 3 = Acrylic
            DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));    // system backdrop type

            var corners = 2;                                               // round window corners
            DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));

            // caption colour matches the palette (COLORREF is 0x00BBGGRR)
            var caption = dark ? 0x001A1F27 : 0x00FCF9F7;
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));

            var captionText = dark ? 0x00F0ECE9 : 0x001A1F27;
            DwmSetWindowAttribute(hwnd, 36, ref captionText, sizeof(int));

            AppLog.Info("ui", "window-chrome", ("dark", dark));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Debug("ui", "backdrop-unavailable");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Swaps the palette (dark / light) and the accent colour scheme at runtime.
    /// Both are merged resource dictionaries, so no restart is needed.
    /// </summary>
    public static void ApplyTheme(string theme, string? accent = null)
    {
        var app = Current;
        if (app is null) return;

        // 三套设计稿主题优先；旧主题名保留兼容，未知值回落到深岩。
        var fileName = theme?.ToLowerInvariant() switch
        {
            "snow" => "Snow.xaml",
            "paper" => "Paper.xaml",
            "light" => "Light.xaml",
            "frost" => "Frost.xaml",
            "midnight" => "Midnight.xaml",
            "dark" => "Dark.xaml",
            _ => "Rock.xaml",
        };
        var dictionaries = app.Resources.MergedDictionaries;

        var palette = dictionaries.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.EndsWith("Rock.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Snow.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Paper.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Midnight.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)
             || d.Source.OriginalString.EndsWith("Frost.xaml", StringComparison.OrdinalIgnoreCase)));

        if (palette?.Source is null ||
            !palette.Source.OriginalString.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            var replacement = new ResourceDictionary { Source = new Uri("Themes/" + fileName, UriKind.Relative) };

            if (palette is not null) dictionaries[dictionaries.IndexOf(palette)] = replacement;
            else dictionaries.Insert(0, replacement);
        }

        ApplyAccent(accent);
        ApplyGlass();
        BreatheAfterThemeSwap();
    }


    /// <summary>
    /// 玻璃材质：把界面底色做成半透明（alpha 由「界面不透明度」决定），
    /// 再让 DWM 把窗口背后模糊，形成毛玻璃。
    ///
    /// 只覆盖几个"大片底色"，按钮/文字的画笔保持不透明，避免整体发灰。
    /// </summary>
    public static void ApplyGlass()
    {
        var app = Current;
        if (app is null) return;

        var settings = (app as App)?._context?.Settings;
        if (settings is null) return;

        var enabled = settings.GlassEnabled;
        var opacity = Math.Clamp(settings.GlassOpacity, 0.45, 1.0);

        // 主题底色（用于算玻璃上的着色 + 界面底色）
        var windowColor = app.TryFindResource("WindowBgColor") is Color wc ? wc : Color.FromRgb(0x0F, 0x11, 0x15);
        var surfaceColor = app.TryFindResource("SurfaceBgColor") is Color sc ? sc : Color.FromRgb(0x17, 0x1A, 0x21);
        var altColor = app.TryFindResource("SurfaceAltBgColor") is Color ac ? ac : Color.FromRgb(0x1E, 0x22, 0x2B);
        var railColor = app.TryFindResource("NavRailColor") is Color rc ? rc : Color.FromRgb(0x14, 0x17, 0x1D);

        // 透明度分配（这是"精致"的关键）：
        //   · 最底层窗口背景最透（透出毛玻璃）
        //   · 卡片次之（能看见玻璃，但内容必须清楚）
        //   · 内容区/侧栏最实，保证文字对比度
        // 都是 opacity 的线性函数：用户把 0.45 拉到底时整体才真正通透。
        byte AlphaOf(double baseAlpha, double gain)
        {
            // 不透明度 = 1（默认）时直接给不透明，避免玻璃关掉后还留着淡淡的透边
            if (opacity >= 0.995) return 255;

            return (byte)Math.Clamp((int)Math.Round(255 * Math.Clamp(baseAlpha + (opacity - 0.45) * gain, 0, 1)), 0, 255);
        }

        var glass = new ResourceDictionary
        {
            // 窗口底：0.45 → 0.42，1.0 → 1.0
            ["WindowBg"] = new SolidColorBrush(Color.FromArgb(AlphaOf(0.30, 1.10), windowColor.R, windowColor.G, windowColor.B)),
            // 卡片：保底 0.80，保证文字对比度
            ["SurfaceBg"] = new SolidColorBrush(Color.FromArgb(AlphaOf(0.80, 0.34), surfaceColor.R, surfaceColor.G, surfaceColor.B)),
            // 次级面（输入框、块内小卡片）
            ["SurfaceAltBg"] = new SolidColorBrush(Color.FromArgb(AlphaOf(0.86, 0.25), altColor.R, altColor.G, altColor.B)),
            // 侧栏：比窗口稍实一点，让导航图标清楚
            ["NavRailBg"] = new SolidColorBrush(Color.FromArgb(AlphaOf(0.62, 0.70), railColor.R, railColor.G, railColor.B)),
            // 小标签
            ["ChipBg"] = new SolidColorBrush(Color.FromArgb(AlphaOf(0.70, 0.45), altColor.R, altColor.G, altColor.B)),
        };

        // 玻璃边缘：比纯色边框更"亮"一点，看起来像玻璃切边
        var borderColor = app.TryFindResource("BorderColor") is Color bc ? bc : Color.FromRgb(0x2A, 0x30, 0x3B);
        glass["BorderBrushSoft"] = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(borderColor.A * 0.75 + 30, 0, 255),
            (byte)Math.Min(255, borderColor.R + 26),
            (byte)Math.Min(255, borderColor.G + 26),
            (byte)Math.Min(255, borderColor.B + 26)));

        // 玻璃的"高光边"：顶部一条极淡的白线，看起来更像一块玻璃
        var highlight = Color.FromArgb((byte)(enabled ? 22 : 0), 255, 255, 255);
        glass["GlassHighlight"] = new SolidColorBrush(highlight);

        // 替换（或追加）到合并字典末尾 —— 末尾优先
        var existing = app.Resources.MergedDictionaries.FirstOrDefault(d => d.Contains("GlassHighlight"));
        if (existing is not null) app.Resources.MergedDictionaries.Remove(existing);
        app.Resources.MergedDictionaries.Add(glass);

        if (app.MainWindow is Window window)
        {
            // 注意：WindowStyle=SingleBorderWindow 时背景必须是**不透明**的，
            // 否则最大化后边框一圈会透出桌面（表现为"没铺满"）。
            window.Background = new SolidColorBrush(windowColor);

            var dark = windowColor.R + windowColor.G + windowColor.B < 384;

            // 着色层要"薄"：太厚会把玻璃糊成一块纯色（这就是之前偏灰的原因）。
            // 深色主题用更小的 alpha，让背后模糊的光影透出来。
            var tintAlpha = dark
                ? 120 + (int)((opacity - 0.45) * 130)     // 0.45 → 120，1.0 → 191
                : 150 + (int)((opacity - 0.45) * 140);    // 浅色要多一点，否则文字发飘

            var tint = Color.FromArgb((byte)Math.Clamp(tintAlpha, 0, 255),
                surfaceColor.R, surfaceColor.G, surfaceColor.B);

            Services.GlassService.Apply(window, enabled, tint, dark);
        }
    }

    /// <summary>
    /// 主题切换的「呼吸」：换字典是瞬时的，直接硬切会有一下白闪。
    /// 换完把整窗从 0.85 淡回 1（220ms），看起来像眼睛适应了一下。
    /// </summary>
    private static void BreatheAfterThemeSwap()
    {
        if (Current?.MainWindow is not Window window) return;

        Animations.Motion.FadeIn(window, Animations.Motion.Page, 0.85, 1);
    }

    /// <summary>Accent keys must be merged after the palette so they override its defaults.</summary>
    private static void ApplyAccent(string? accent)
    {
        var app = Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        var name = string.IsNullOrWhiteSpace(accent) ? "Moss" : accent.Trim();
        var known = new[] { "Moss", "Jade", "Ochre", "Blue", "Violet", "Teal", "Amber", "Rose", "Orange", "Emerald" };
        if (!known.Contains(name, StringComparer.OrdinalIgnoreCase)) name = "Moss";

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source is not null &&
            d.Source.OriginalString.StartsWith("Themes/Accent-", StringComparison.OrdinalIgnoreCase));

        if (existing?.Source is not null &&
            existing.Source.OriginalString.EndsWith("Accent-" + name + ".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var replacement = new ResourceDictionary { Source = new Uri("Themes/Accent-" + name + ".xaml", UriKind.Relative) };

        if (existing is not null) dictionaries[dictionaries.IndexOf(existing)] = replacement;
        else dictionaries.Add(replacement);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("app", "unhandled-ui-exception", e.Exception);
        Dialogs.Error("发生未处理的错误", $"发生未处理的错误：{e.Exception.Message}", $"日志目录：{AppLog.LogDirectory}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _context?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
