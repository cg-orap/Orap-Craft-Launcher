using System.Windows;
using Microsoft.Web.WebView2.Core;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;

namespace Orion.App.Views;

/// <summary>
/// In-app Microsoft sign-in.
///
/// This is the approach mainstream launchers use: a real browser surface is embedded in the
/// launcher, the user picks an account and consents, and the launcher intercepts the OAuth
/// redirect itself. Nothing has to be copied back, and no Azure app registration by the user is
/// required because the built-in public client id already allows the
/// <c>oauth20_desktop.srf</c> redirect.
/// </summary>
public partial class MicrosoftLoginWindow : Window
{
    private readonly MicrosoftAuthService _auth;
    private readonly string _userDataFolder;
    private readonly bool _modernFlow;
    private readonly string _redirectUri;
    private readonly string _state = Guid.NewGuid().ToString("N");

    private bool _completed;
    private bool _browserReady;

    public MicrosoftLoginWindow(MicrosoftAuthService auth, string userDataFolder)
    {
        _auth = auth;
        _userDataFolder = userDataFolder;

        // Use the v2.0 endpoint (XboxLive.signin scope) when the user configured an app id:
        // that is the flow whose token Xbox Live accepts, and the embedded browser hides the
        // loopback redirect, so the UX is identical and nothing has to be copied.
        _modernFlow = auth.CanUseModernFlows;
        _redirectUri = _modernFlow
            ? $"http://localhost:{MicrosoftAuthService.PickLoopbackPort()}/"
            : auth.Options.RedirectUri;

        InitializeComponent();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>The authorization code captured from the redirect, or null when cancelled.</summary>
    public string? AuthorizationCode { get; private set; }

    /// <summary>OAuth error reported by Microsoft (access_denied etc.), if any.</summary>
    public string? AuthorizationError { get; private set; }

    /// <summary>True when the user asked to switch to the device-code flow instead.</summary>
    public bool SwitchToDeviceCode { get; private set; }

    /// <summary>True when the code must be exchanged against the v2.0 endpoint.</summary>
    public bool UsedModernFlow => _modernFlow;

    /// <summary>The redirect URI that must be repeated in the token request.</summary>
    public string RedirectUriUsed => _redirectUri;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeBrowserAsync().ConfigureAwait(true);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            AppLog.Info("auth", "webview2-runtime", ("version", version));
            StatusText.Text = $"正在载入微软登录页面…（WebView2 {version}）";
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException)
        {
            AppLog.Warn("auth", "webview2-missing", ("error", ex.Message));
            ShowFallback("未检测到 Microsoft Edge WebView2 运行时，无法内嵌登录。\n"
                         + "可以点击右下角「改用设备代码登录」，或安装 WebView2 运行时后重试。");
            return;
        }

        try
        {
            Directory.CreateDirectory(_userDataFolder);

            var environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: _userDataFolder)
                .ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            var settings = Browser.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsPasswordAutosaveEnabled = true;
            settings.IsGeneralAutofillEnabled = true;

            // Keep the login contained: external links open in the system browser instead of
            // spawning windows inside the launcher.
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternally(args.Uri);
            };

            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.DocumentTitleChanged += (_, _) => UpdateTitle();

            _browserReady = true;
            Overlay.Visibility = Visibility.Collapsed;

            var authorizeUri = _modernFlow
                ? _auth.BuildModernAuthorizeUri(_redirectUri, _state)
                : _auth.BuildAuthorizationUri();

            AppLog.Info("auth", "webview2-navigating",
                ("host", authorizeUri.Host),
                ("flow", _modernFlow ? "modern" : "legacy"));

            Browser.CoreWebView2.Navigate(authorizeUri.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Error("auth", "webview2-init-failed", ex);
            ShowFallback("无法初始化内嵌浏览器：" + ex.Message
                         + "\n可以点击右下角「改用设备代码登录」。");
        }
    }

    private void ShowFallback(string message)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
        PlaceholderText.Text = message;
        StatusText.Text = "内嵌登录不可用";
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!MicrosoftAuthService.IsRedirectTo(e.Uri, _redirectUri)) return;

        // This is our redirect: stop the browser and take the code out of the URL.
        e.Cancel = true;

        var error = MicrosoftAuthService.TryGetAuthorizationError(e.Uri);
        if (error is not null)
        {
            AuthorizationError = error;
            AppLog.Warn("auth", "browser-login-error", ("error", error));
            Finish(success: false, "登录未完成：" + error);
            return;
        }

        // CSRF guard for the modern flow (the legacy redirect has no state contract).
        if (_modernFlow)
        {
            var parameters = LoopbackAuthListener.ParseQuery(e.Uri);
            if (parameters.TryGetValue("state", out var returned) &&
                !string.Equals(returned, _state, StringComparison.Ordinal))
            {
                AppLog.Warn("auth", "browser-login-state-mismatch");
                Finish(success: false, "登录回调校验失败（state 不匹配），请重试。");
                return;
            }
        }

        var code = MicrosoftAuthService.TryExtractCode(e.Uri);
        if (code is null)
        {
            AppLog.Warn("auth", "browser-login-no-code");
            Finish(success: false, "登录页面没有返回授权码，请重试。");
            return;
        }

        AuthorizationCode = code;
        AppLog.Info("auth", "browser-login-captured", ("length", code.Length), ("flow", _modernFlow ? "modern" : "legacy"));
        Finish(success: true, "已获得授权，正在完成登录…");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_browserReady) return;

        if (!e.IsSuccess)
        {
            AppLog.Warn("auth", "webview2-navigation-failed", ("status", e.WebErrorStatus.ToString()));
            StatusText.Text = $"页面加载失败（{e.WebErrorStatus}），可以重试或改用设备代码登录。";
        }
        else
        {
            AppLog.Info("auth", "webview2-navigation-ok");
            StatusText.Text = "等待你在页面中完成登录…";
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        try
        {
            var title = Browser.CoreWebView2?.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(title)) Title = $"{title} — Orap Craft Launcher";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void Finish(bool success, string status)
    {
        if (_completed) return;
        _completed = true;

        StatusText.Text = status;

        // Give the UI a moment to show the status before closing.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            DialogResult = success;
            Close();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _completed = true;
        DialogResult = false;
        Close();
    }

    private void OnSwitchToDeviceCode(object sender, RoutedEventArgs e)
    {
        SwitchToDeviceCode = true;
        _completed = true;
        DialogResult = false;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            Browser.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static void OpenExternally(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("auth", "open-external-failed", ("error", ex.Message));
        }
    }
}
