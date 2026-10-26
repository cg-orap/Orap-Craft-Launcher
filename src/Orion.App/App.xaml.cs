using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

            _context = LauncherContext.Create(rootOverride);

            var window = new MainWindow();
            MainWindow = window;
            Services.WindowPlacement.FitToMonitor(window);

            var viewModel = new MainViewModel(_context);
            Shell = viewModel;

            ApplyTheme(_context.Settings.Theme, _context.Settings.Accent);

            window.DataContext = viewModel;
            window.Show();

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
            MessageBox.Show($"启动器初始化失败：{ex.Message}\n\n详细信息已写入日志。",
                "Orap Craft Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        MessageBox.Show($"发生未处理的错误：{e.Exception.Message}\n\n日志目录: {AppLog.LogDirectory}",
            "Orap Craft Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _context?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
