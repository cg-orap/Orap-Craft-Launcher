using System.Windows;
using Microsoft.Web.WebView2.Core;
using Orion.App.Services;
using Orion.App.ViewModels;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;

namespace Orion.App.Views;

/// <summary>
/// The only sign-in surface: a dedicated window with saved accounts on the left and the real
/// Microsoft sign-in page embedded on the right.
///
/// The browser is created lazily the first time the user clicks "用浏览器登录", so merely opening
/// the window to switch to an offline account does not touch the network.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;
    private readonly string _userDataFolder;

    private MicrosoftBrowserLogin? _attempt;
    private bool _browserReady;


    public LoginWindow(LoginViewModel viewModel, string userDataFolder)
    {
        _vm = viewModel;
        _userDataFolder = userDataFolder;

        InitializeComponent();
        DataContext = _vm;

        Loaded += (_, _) =>
        {
            // 窗口入场：淡入 + 10px 上滑（对话框不该"啪"地出现）
            Animations.Motion.SlideFadeIn(this, 10, Animations.Motion.Page);
            _ = InitializeAsync();
        };
        Closed += OnClosed;
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _vm.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("ui", "login-window-init-failed", ex);
            SetStatus("读取账户失败：" + ex.Message);
        }
    }

    private async void OnMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;

        MicrosoftLoginButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;

        try
        {
            if (!await EnsureBrowserAsync().ConfigureAwait(true))
            {
                MicrosoftLoginButton.IsEnabled = true;
                LoadingBar.Visibility = Visibility.Collapsed;
                return;
            }

            _attempt = new MicrosoftBrowserLogin(_vm.Auth);


            Overlay.Visibility = Visibility.Collapsed;
            SetStatus("正在载入微软登录页面…");
            _vm.IsBusy = true;

            Browser.CoreWebView2.Navigate(_attempt.AuthorizeUri.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Error("ui", "login-window-browser-failed", ex);
            ShowOverlay("无法打开登录页面", ex.Message);
            SetStatus("登录失败：" + ex.Message);
            _vm.IsBusy = false;
            MicrosoftLoginButton.IsEnabled = true;
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Creates the WebView2 on first use; explains clearly when the runtime is missing.</summary>
    private async Task<bool> EnsureBrowserAsync()
    {
        if (_browserReady) return true;

        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            AppLog.Info("auth", "webview2-runtime", ("version", version));
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException)
        {
            AppLog.Warn("auth", "webview2-missing", ("error", ex.Message));
            ShowOverlay("缺少 Microsoft Edge WebView2 运行时",
                "启动器使用 Windows 自带的 WebView2 组件显示微软登录页。请安装 WebView2 运行时后重试"
                + "（Windows 11 通常已自带）。");
            return false;
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

            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternally(args.Uri);
            };

            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _browserReady = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("auth", "webview2-init-failed", ex);
            ShowOverlay("无法初始化内置浏览器", ex.Message);
            return false;
        }
    }

    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_attempt is null || !_attempt.IsRedirect(e.Uri)) return;

        // This is our redirect: stop the browser and take the code out of the URL.
        e.Cancel = true;

        var error = MicrosoftAuthService.TryGetAuthorizationError(e.Uri);
        if (error is not null)
        {
            AppLog.Warn("auth", "browser-login-error", ("error", error));
            Finish("登录未完成：" + error);
            return;
        }

        if (_attempt.IsModern)
        {
            var parameters = OAuthQuery.Parse(e.Uri);
            if (parameters.TryGetValue("state", out var returned) &&
                !string.Equals(returned, _attempt.State, StringComparison.Ordinal))
            {
                AppLog.Warn("auth", "browser-login-state-mismatch");
                Finish("登录回调校验失败（state 不匹配），请重试。");
                return;
            }
        }

        var code = MicrosoftAuthService.TryExtractCode(e.Uri);
        if (code is null)
        {
            AppLog.Warn("auth", "browser-login-no-code");
            Finish("登录页面没有返回授权码，请重试。");
            return;
        }

        AppLog.Info("auth", "browser-login-captured",
            ("length", code.Length),
            ("flow", _attempt.IsModern ? "modern" : "legacy"));

        SetStatus("已获得授权，正在与微软服务交换令牌…");

        try
        {
            var account = await _attempt.ExchangeAsync(code).ConfigureAwait(true);
            await _vm.AcceptMicrosoftAccountAsync(account).ConfigureAwait(true);
            Finish($"登录成功：{account.Username}");
        }
        catch (MicrosoftAuthException ex)
        {
            AppLog.Warn("auth", "browser-login-rejected", ("code", ex.ErrorCode ?? "unknown"));
            Finish(MicrosoftAuthMessages.Describe(ex));
        }
        catch (Exception ex)
        {
            AppLog.Error("ui", "browser-login-failed", ex);
            Finish("登录失败：" + ex.Message);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_browserReady) return;

        if (!e.IsSuccess)
        {
            AppLog.Warn("auth", "webview2-navigation-failed", ("status", e.WebErrorStatus.ToString()));
            SetStatus($"页面加载失败（{e.WebErrorStatus}），可以再点一次「用浏览器登录」。");
            _vm.IsBusy = false;
            MicrosoftLoginButton.IsEnabled = true;
            LoadingBar.Visibility = Visibility.Collapsed;
            return;
        }

        AppLog.Info("auth", "webview2-navigation-ok");
        SetStatus("等待你在页面中完成登录…");
        LoadingBar.Visibility = Visibility.Collapsed;
    }

    private void Finish(string status)
    {

        _vm.IsBusy = false;
        MicrosoftLoginButton.IsEnabled = true;
        MicrosoftLoginButton.Content = "用浏览器登录";
        LoadingBar.Visibility = Visibility.Collapsed;
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _vm.Status = text;
    }

    private void ShowOverlay(string title, string message)
    {
        OverlayTitle.Text = title;
        OverlayText.Text = message;
        Overlay.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
