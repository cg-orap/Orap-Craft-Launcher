using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Orion.App.Mvvm;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;
using Orion.Core.Instances;
using Orion.Core.Java;
using Orion.Core.Launch;

namespace Orion.App.ViewModels;

/// <summary>Home page: account, current instance and the launch button.</summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private bool _isLaunching;
    private string _launchStatus = "准备就绪";
    private string _javaStatus = "正在检测 Java…";
    private string _instanceStatus = "";
    private string _offlineName = "";
    private string _microsoftCallback = "";
    private string _microsoftLoginUrl = "";
    private string _microsoftStatus = "";
    private string _microsoftCodeHint = "";
    private GameSession? _session;

    /// <summary>
    /// OAuth authorization codes are single use and short lived. Remembering the ones we already
    /// sent lets us refuse a pointless retry (which always fails with "code has expired") and
    /// tell the user to log in again instead.
    /// </summary>
    private readonly HashSet<string> _consumedCodes = new(StringComparer.Ordinal);

    /// <summary>Polls the clipboard while a login is in progress so the code is used immediately.</summary>
    private DispatcherTimer? _clipboardWatch;
    private DateTimeOffset _clipboardWatchDeadline;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => !IsLaunching, ex => LaunchStatus = "启动失败: " + ex.Message);
        StopCommand = new RelayCommand(StopGame, () => IsLaunching);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddOfflineAccountCommand = new AsyncRelayCommand(AddOfflineAccountAsync, () => OfflineAccounts.IsValidName(OfflineName));
        BeginMicrosoftLoginCommand = new RelayCommand(BeginMicrosoftLogin);
        CompleteMicrosoftLoginCommand = new AsyncRelayCommand(
            CompleteMicrosoftLoginAsync,
            () => MicrosoftAuthService.TryExtractCode(MicrosoftCallback) is not null);
        CopyLoginLinkCommand = new RelayCommand(CopyLoginLink, () => MicrosoftLoginUrl.Length > 0);
        PasteCallbackCommand = new RelayCommand(PasteCallbackFromClipboard);

        // Deliberately no CanExecute predicate: the availability depends on clipboard content,
        // which we cannot observe, so a disabled button would stay disabled forever. The command
        // explains the problem itself when there is nothing usable in the clipboard.
        LoginFromClipboardCommand = new AsyncRelayCommand(LoginFromClipboardAsync);
        ClearCallbackCommand = new RelayCommand(ClearCallback);
        RefreshTokenCommand = new AsyncRelayCommand(RefreshTokenAsync, () => _main.CurrentAccount?.Type == AccountType.Microsoft);
        OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
        OpenInstanceSettingsCommand = new RelayCommand(() => _main.Navigate("instances"));
    }

    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddOfflineAccountCommand { get; }
    public ICommand BeginMicrosoftLoginCommand { get; }
    public ICommand CompleteMicrosoftLoginCommand { get; }
    public ICommand CopyLoginLinkCommand { get; }
    public ICommand PasteCallbackCommand { get; }
    public ICommand LoginFromClipboardCommand { get; }
    public ICommand ClearCallbackCommand { get; }
    public ICommand RefreshTokenCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenInstanceSettingsCommand { get; }

    public Instance? SelectedInstance => _main.CurrentInstance;

    public string InstanceTitle => _main.CurrentInstance?.Name ?? "尚未选择实例";

    public string InstanceSubtitle => _main.CurrentInstance is null
        ? "前往「实例管理」创建或导入一个实例"
        : $"{_main.CurrentInstance.Subtitle} · {_main.CurrentInstance.GameDirectory}";

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!SetProperty(ref _isLaunching, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
            ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)LaunchCommand).RaiseCanExecuteChanged();
        }
    }

    public string LaunchButtonText => IsLaunching ? "运行中…" : "启动游戏";

    public string LaunchStatus
    {
        get => _launchStatus;
        set => SetProperty(ref _launchStatus, value);
    }

    public string JavaStatus
    {
        get => _javaStatus;
        set => SetProperty(ref _javaStatus, value);
    }

    public string InstanceStatus
    {
        get => _instanceStatus;
        set => SetProperty(ref _instanceStatus, value);
    }

    public string OfflineName
    {
        get => _offlineName;
        set
        {
            if (!SetProperty(ref _offlineName, value)) return;
            ((AsyncRelayCommand)AddOfflineAccountCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// The authorization link last generated by "在浏览器中登录". Kept visible in the UI so it can
    /// be copied into a different browser when the default one refuses to open.
    /// </summary>
    public string MicrosoftLoginUrl
    {
        get => _microsoftLoginUrl;
        private set
        {
            if (!SetProperty(ref _microsoftLoginUrl, value)) return;
            OnPropertyChanged(nameof(HasMicrosoftLoginUrl));
            ((RelayCommand)CopyLoginLinkCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasMicrosoftLoginUrl => MicrosoftLoginUrl.Length > 0;

    public string MicrosoftCallback
    {
        get => _microsoftCallback;
        set
        {
            if (!SetProperty(ref _microsoftCallback, value)) return;
            ((AsyncRelayCommand)CompleteMicrosoftLoginCommand).RaiseCanExecuteChanged();
            UpdateCodeHint();
        }
    }

    /// <summary>Live feedback about what the pasted text contains, so the user can tell the paste worked.</summary>
    public string MicrosoftCodeHint
    {
        get => _microsoftCodeHint;
        private set => SetProperty(ref _microsoftCodeHint, value);
    }

    public string MicrosoftStatus
    {
        get => _microsoftStatus;
        private set => SetProperty(ref _microsoftStatus, value);
    }

    public ObservableList<string> GameLog { get; } = [];

    public async Task RefreshAsync()
    {
        var instance = _main.CurrentInstance;
        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(nameof(InstanceTitle));
        OnPropertyChanged(nameof(InstanceSubtitle));

        if (instance is null)
        {
            JavaStatus = "未选择实例";
            InstanceStatus = "请先创建实例";
            return;
        }

        try
        {
            var resolved = await _main.Context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);

            var runtimes = _main.JavaRuntimes.Count > 0
                ? _main.JavaRuntimes.ToList()
                : (await _main.Context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

            var java = SelectJava(instance, resolved.RequiredJavaMajorVersion, runtimes);
            JavaStatus = java is null
                ? $"未找到 Java {resolved.RequiredJavaMajorVersion}，请到「设置」中指定"
                : $"{java.DisplayName} · {java.Home}";

            if (java is not null && java.MajorVersion != resolved.RequiredJavaMajorVersion)
                JavaStatus += $"（该版本推荐 Java {resolved.RequiredJavaMajorVersion}）";

            var installed = File.Exists(_main.Context.Paths.VersionJarPath(resolved.ClientJarVersionId));
            InstanceStatus = installed
                ? $"已安装 · 继承链 {string.Join(" ← ", resolved.Chain)}"
                : "未安装（首次启动会自动下载）";
        }
        catch (Exception ex) when (ex is Core.Game.VersionNotFoundException or HttpRequestException or IOException)
        {
            JavaStatus = "无法解析版本";
            InstanceStatus = ex.Message;
        }
    }

    private static JavaRuntime? SelectJava(Instance instance, int requiredMajor, IReadOnlyList<JavaRuntime> runtimes)
    {
        if (!string.IsNullOrWhiteSpace(instance.JavaPath) && File.Exists(instance.JavaPath))
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(instance.JavaPath));
            var match = runtimes.FirstOrDefault(r => string.Equals(r.Home, home, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return JavaDetector.SelectFor(requiredMajor, runtimes);
    }

    private async Task LaunchAsync()
    {
        var instance = _main.CurrentInstance;
        if (instance is null)
        {
            LaunchStatus = "请先在「实例管理」中创建或选择一个实例。";
            _main.Navigate("instances");
            return;
        }

        var account = _main.CurrentAccount;
        if (account is null)
        {
            LaunchStatus = "请先添加一个账户（离线账户即可开始游戏）。";
            return;
        }

        if (account.Type == AccountType.Microsoft && account.IsExpired)
        {
            LaunchStatus = "正在刷新微软登录令牌…";
            await RefreshTokenAsync().ConfigureAwait(true);
            account = _main.CurrentAccount;
            if (account is null || account.IsExpired)
            {
                LaunchStatus = "微软账户令牌已失效，请重新登录。";
                return;
            }
        }

        var activity = _main.Activities.Begin($"启动 {instance.Name}", instance.Subtitle);
        IsLaunching = true;
        LaunchStatus = "正在准备启动…";
        GameLog.Clear();

        try
        {
            var context = _main.Context;
            var resolved = await context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);

            var runtimes = _main.JavaRuntimes.Count > 0
                ? _main.JavaRuntimes.ToList()
                : (await context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

            var java = SelectJava(instance, resolved.RequiredJavaMajorVersion, runtimes);
            if (java is null)
            {
                LaunchStatus = $"未找到 Java {resolved.RequiredJavaMajorVersion}。请到「设置」页指定 Java 路径。";
                _main.Activities.Finish(activity, ActivityState.Failed, LaunchStatus);
                _main.Navigate("settings");
                return;
            }

            if (!File.Exists(context.Paths.VersionJarPath(resolved.ClientJarVersionId)))
            {
                LaunchStatus = "客户端未安装，正在自动安装…";
                _main.Activities.Report(activity, 0.05, "安装游戏文件…", "");

                var progress = new Progress<Core.Install.InstallProgress>(p =>
                {
                    _main.Activities.Report(activity, p.Fraction * 0.6, p.Message,
                        p.Download is null ? "" : Core.Net.SpeedMeter.Humanize(p.Download.BytesPerSecond));
                    LaunchStatus = p.Message;
                });

                var install = await context.InstallService.InstallAsync(instance.VersionId, new Core.Install.InstallOptions(), progress).ConfigureAwait(true);
                if (!install.Success)
                {
                    LaunchStatus = "安装失败: " + (install.Error ?? "未知错误");
                    _main.Activities.Finish(activity, ActivityState.Failed, install.Error);
                    return;
                }

                resolved = await context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
            }

            var request = new LaunchRequest
            {
                Version = resolved,
                Account = account,
                Java = java,
                GameDirectory = instance.GameDirectory,
                MemoryMb = instance.MemoryMb ?? context.Settings.MemoryMb,
                MinMemoryMb = instance.MinMemoryMb ?? context.Settings.MinMemoryMb,
                ExtraJvmArguments = instance.JvmArguments ?? context.Settings.JvmArguments,
                ExtraGameArguments = instance.GameArguments,
                WindowWidth = instance.WindowWidth,
                WindowHeight = instance.WindowHeight,
                Fullscreen = instance.Fullscreen,
                LauncherVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            };

            _main.Activities.Report(activity, 0.7, "启动 JVM…", "");

            var logProgress = new Progress<string>(line =>
            {
                LaunchStatus = line;
                _main.LogsPage.AppendSystem(line);
            });

            _session = await context.Launcher.LaunchAsync(request, logProgress).ConfigureAwait(true);
            _session.OutputReceived += OnGameOutput;

            InstanceStatus = $"运行中 · PID {_session.ProcessId}";
            LaunchStatus = $"游戏已启动 (PID {_session.ProcessId})";
            _main.Activities.Report(activity, 1, "游戏运行中", "");
            _main.Activities.Finish(activity, ActivityState.Completed);

            _main.Navigate("logs");

            var exitCode = await _session.WaitForExitAsync().ConfigureAwait(true);
            var killed = _session.KilledByLauncher;
            var lines = _session.RecentLines;
            var logPath = _session.LogFilePath;

            await _session.DisposeAsync().ConfigureAwait(true);
            _session.OutputReceived -= OnGameOutput;
            _session = null;
            IsLaunching = false;

            instance.LastPlayedAt = DateTimeOffset.Now;
            await context.Instances.SaveAsync(instance).ConfigureAwait(true);

            if (killed)
            {
                LaunchStatus = "游戏已被启动器结束。";
                InstanceStatus = "已停止";
                _main.LogsPage.AppendSystem("游戏进程已由启动器结束。");
                return;
            }

            var analysis = await CrashAnalyzer.AnalyzeAsync(instance.GameDirectory, exitCode, lines).ConfigureAwait(true);
            _main.LogsPage.SetCrashAnalysis(analysis, logPath);

            if (exitCode == 0)
            {
                LaunchStatus = "游戏已正常退出。";
                InstanceStatus = "已退出";
            }
            else
            {
                LaunchStatus = $"游戏异常退出 (代码 {exitCode}) — {analysis.Summary}";
                InstanceStatus = "崩溃";
            }
        }
        catch (OperationCanceledException)
        {
            LaunchStatus = "启动已取消。";
            _main.Activities.Finish(activity, ActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            LaunchStatus = "启动失败: " + ex.Message;
            AppLog.Error("ui", "launch-failed", ex);
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            if (_session is null) IsLaunching = false;
        }
    }

    private void OnGameOutput(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(() =>
        {
            GameLog.Add(line);
            while (GameLog.Count > 2000) GameLog.RemoveAt(0);
            _main.LogsPage.AppendGame(line);
        });
    }

    private void StopGame()
    {
        if (_session is null) return;

        LaunchStatus = "正在结束游戏进程…";
        _session.Kill();
    }

    private async Task AddOfflineAccountAsync()
    {
        var name = OfflineName.Trim();
        if (!OfflineAccounts.IsValidName(name))
        {
            LaunchStatus = "离线用户名需为 3-16 位字母、数字或下划线。";
            return;
        }

        var accounts = (await _main.Context.AccountStore.LoadAsync().ConfigureAwait(true)).ToList();
        if (accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            LaunchStatus = "该离线账户已存在。";
            return;
        }

        var account = OfflineAccounts.Create(name);
        accounts.Add(account);
        await _main.Context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);
        await _main.ReloadAccountsAsync().ConfigureAwait(true);
        _main.CurrentAccount = _main.Accounts.FirstOrDefault(a => a.Id == account.Id);

        OfflineName = "";
        LaunchStatus = $"已添加离线账户 {account.Username}。";
        _main.LogsPage.AppendSystem($"添加离线账户 {account.Username} (UUID {account.Uuid})");
    }

    /// <summary>
    /// Generates the authorization link and tries to open the system browser.
    ///
    /// The link is always kept in the UI (and on the clipboard on demand), because the default
    /// browser may be missing, blocked or simply focused elsewhere - the user must always have a
    /// way to continue the login in any browser they like.
    /// </summary>
    private void BeginMicrosoftLogin()
    {
        try
        {
            MicrosoftLoginUrl = _main.Context.Auth.BuildAuthorizationUri().ToString();
        }
        catch (Exception ex)
        {
            MicrosoftStatus = "无法生成登录链接: " + ex.Message;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(MicrosoftLoginUrl) { UseShellExecute = true });
            MicrosoftStatus = "已在浏览器中打开微软登录页。登录完成后**自动**读取剪贴板即可——"
                              + "请把浏览器地址栏里的整段链接复制下来（或在回调页复制授权码），启动器会立即尝试登录。";
        }
        catch (Exception ex)
        {
            // Keep the link available: the user can copy it into any browser manually.
            MicrosoftStatus = $"无法自动打开浏览器（{ex.Message}）。\n"
                              + "请点击“复制登录链接”，粘贴到浏览器地址栏打开；登录完成后，再把回调链接复制回来。";
            AppLog.Warn("auth", "browser-launch-failed", ("error", ex.Message));
        }

        // Authorization codes live for a few minutes only: watch the clipboard so the code is
        // used the moment the user copies it, instead of waiting for another click.
        StartClipboardWatch();
        UpdateCodeHint();
    }

    /// <summary>Polls the clipboard (on the UI thread, which owns the STA clipboard) after a login starts.</summary>
    private void StartClipboardWatch()
    {
        StopClipboardWatch();

        _clipboardWatchDeadline = DateTimeOffset.UtcNow.AddMinutes(4);
        _clipboardWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clipboardWatch.Tick += OnClipboardWatchTick;
        _clipboardWatch.Start();
    }

    private void StopClipboardWatch()
    {
        if (_clipboardWatch is null) return;

        _clipboardWatch.Stop();
        _clipboardWatch.Tick -= OnClipboardWatchTick;
        _clipboardWatch = null;
    }

    private void OnClipboardWatchTick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow > _clipboardWatchDeadline)
        {
            StopClipboardWatch();
            return;
        }

        // While the token exchange runs we must not start a second one.
        if (IsMicrosoftLoginBusy) return;

        var text = TryGetClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var trimmed = text.Trim();

        // Ignore our own "copy login link" content and plain error callbacks.
        if (string.Equals(trimmed, MicrosoftLoginUrl, StringComparison.Ordinal)) return;
        if (MicrosoftAuthService.TryGetAuthorizationError(trimmed) is not null) return;

        var code = MicrosoftAuthService.TryExtractCode(trimmed);
        if (code is null || _consumedCodes.Contains(code)) return;

        if (!string.Equals(MicrosoftCallback, trimmed, StringComparison.Ordinal))
            MicrosoftCallback = trimmed;

        MicrosoftStatus = "已从剪贴板检测到登录回调，正在自动完成登录…";
        StopClipboardWatch();

        if (CompleteMicrosoftLoginCommand.CanExecute(null)) CompleteMicrosoftLoginCommand.Execute(null);
    }

    private void CopyLoginLink()
    {
        if (MicrosoftLoginUrl.Length == 0) return;

        if (TrySetClipboard(MicrosoftLoginUrl))
        {
            MicrosoftStatus = "登录链接已复制到剪贴板，可粘贴到任意浏览器的地址栏打开。";
        }
        else
        {
            MicrosoftStatus = "无法写入剪贴板，请手动选中上方链接后按 Ctrl+C 复制。";
        }
    }

    /// <summary>Reads the callback URL straight from the clipboard, so pasting never depends on keyboard focus.</summary>
    private void PasteCallbackFromClipboard()
    {
        var text = TryGetClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            MicrosoftStatus = "剪贴板为空，或其它程序正占用剪贴板。请复制浏览器地址栏的链接后重试。";
            return;
        }

        MicrosoftCallback = text.Trim();
        MicrosoftStatus = MicrosoftAuthService.TryExtractCode(MicrosoftCallback) is not null
            ? "已从剪贴板读取回调链接，点击“完成登录”继续（授权码仅几分钟有效，请尽快完成）。"
            : "已读取剪贴板内容，但没有识别到授权码，请确认复制的是登录后的完整回调链接。";
    }

    /// <summary>One click: read the clipboard, then immediately exchange the code.</summary>
    private async Task LoginFromClipboardAsync()
    {
        var text = TryGetClipboardText();

        if (!string.IsNullOrWhiteSpace(text) && MicrosoftAuthService.TryExtractCode(text) is not null)
        {
            MicrosoftCallback = text.Trim();
        }
        else if (MicrosoftAuthService.TryExtractCode(MicrosoftCallback) is null)
        {
            MicrosoftStatus = "剪贴板里没有可用的登录回调。请先在浏览器完成登录，再复制地址栏里的整段链接。";
            return;
        }

        StopClipboardWatch();
        await CompleteMicrosoftLoginAsync().ConfigureAwait(true);
    }

    private bool IsMicrosoftLoginBusy => CompleteMicrosoftLoginCommand is AsyncRelayCommand { IsRunning: true };

    private void ClearCallback()
    {
        MicrosoftCallback = "";
        MicrosoftStatus = "";
    }

    /// <summary>Explains, as the user types or pastes, whether the input can be used.</summary>
    private void UpdateCodeHint()
    {
        if (string.IsNullOrWhiteSpace(MicrosoftCallback))
        {
            MicrosoftCodeHint = "";
            return;
        }

        var error = MicrosoftAuthService.TryGetAuthorizationError(MicrosoftCallback);
        if (error is not null)
        {
            MicrosoftCodeHint = $"该链接是登录失败/取消的回调（{error}）。请重新点击“在浏览器中登录”。";
            return;
        }

        var code = MicrosoftAuthService.TryExtractCode(MicrosoftCallback);
        if (code is not null)
        {
            var preview = code.Length <= 14 ? code : code[..14] + "…";
            MicrosoftCodeHint = _consumedCodes.Contains(code)
                ? "该授权码已经使用过或已过期（授权码只能使用一次），请重新登录。"
                : $"已识别授权码：{preview}（共 {code.Length} 个字符）。授权码仅几分钟有效，请立即完成登录。";
            return;
        }

        MicrosoftCodeHint = "尚未识别到授权码。请粘贴登录后地址栏里的完整链接（形如 https://login.live.com/oauth20_desktop.srf?code=…）。";
    }

    private string? TryGetClipboardText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException)
            {
                // The clipboard is opened exclusively by another process for a moment; retry.
                Thread.Sleep(40);
            }
        }

        return null;
    }

    private static bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }

    private async Task CompleteMicrosoftLoginAsync()
    {
        var authorizationError = MicrosoftAuthService.TryGetAuthorizationError(MicrosoftCallback);
        if (authorizationError is not null)
        {
            MicrosoftStatus = $"微软返回了登录失败信息（{authorizationError}）。请重新点击“在浏览器中登录”。";
            return;
        }

        var code = MicrosoftAuthService.TryExtractCode(MicrosoftCallback);
        if (code is null)
        {
            MicrosoftStatus = "无法从输入中识别授权码。请确认粘贴的是登录后浏览器地址栏里的完整链接。";
            return;
        }

        // An OAuth code works exactly once. Resending a used or expired one would only produce
        // "invalid_grant ... The code has expired", so refuse early with a clear instruction.
        if (!_consumedCodes.Add(code))
        {
            MicrosoftStatus = "这个授权码已经尝试过了。授权码只能使用一次，请重新点击“在浏览器中登录”。";
            MicrosoftCallback = "";
            UpdateCodeHint();
            return;
        }

        MicrosoftStatus = "正在与微软账户服务交换令牌…";

        try
        {
            var account = await _main.Context.Auth.AuthenticateWithCodeAsync(code).ConfigureAwait(true);

            var accounts = (await _main.Context.AccountStore.LoadAsync().ConfigureAwait(true))
                .Where(a => !string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            accounts.Add(account);
            await _main.Context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);

            await _main.ReloadAccountsAsync().ConfigureAwait(true);
            _main.CurrentAccount = _main.Accounts.FirstOrDefault(a => string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

            StopClipboardWatch();
            MicrosoftCallback = "";
            MicrosoftLoginUrl = "";
            MicrosoftStatus = $"登录成功: {account.Username}";
            MicrosoftCodeHint = "";
            LaunchStatus = $"微软账户 {account.Username} 已登录。";
            _main.LogsPage.AppendSystem($"微软账户 {account.Username} 登录成功");
        }
        catch (MicrosoftAuthException ex)
        {
            AppLog.Warn("auth", "microsoft-login-rejected",
                ("code", ex.ErrorCode ?? "unknown"),
                ("description", ex.ErrorDescription ?? "none"));

            MicrosoftStatus = MicrosoftAuthMessages.Describe(ex);

            // When the code itself was rejected it can never be reused; clear it so the next
            // attempt starts from a fresh login instead of failing again.
            if (ex.IsAuthorizationCodeRejected)
            {
                StopClipboardWatch();
                MicrosoftCallback = "";
                MicrosoftLoginUrl = "";
                MicrosoftCodeHint = "";
            }
        }
        catch (Exception ex)
        {
            // Network problems are retryable, but the code is still single use: keep it visible
            // and tell the user to log in again rather than promise a retry.
            MicrosoftStatus = "登录过程中出现错误: " + ex.Message
                              + "\n如果授权码还没有被微软处理过，可以立即重试；否则请重新登录。";
            AppLog.Error("ui", "microsoft-login-failed", ex);
        }
    }

    private async Task RefreshTokenAsync()
    {
        var account = _main.CurrentAccount;
        if (account is null || account.Type != AccountType.Microsoft) return;

        try
        {
            LaunchStatus = "正在刷新令牌…";
            var refreshed = await _main.Context.Auth.RefreshAsync(account).ConfigureAwait(true);

            var accounts = (await _main.Context.AccountStore.LoadAsync().ConfigureAwait(true)).ToList();
            var index = accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) accounts[index] = refreshed;
            else accounts.Add(refreshed);

            await _main.Context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);
            await _main.ReloadAccountsAsync().ConfigureAwait(true);
            _main.CurrentAccount = _main.Accounts.FirstOrDefault(a => string.Equals(a.Id, refreshed.Id, StringComparison.OrdinalIgnoreCase));

            LaunchStatus = $"令牌已刷新: {refreshed.Username}";
            _main.LogsPage.AppendSystem($"微软令牌已刷新 ({refreshed.Username})");
        }
        catch (MicrosoftAuthException ex)
        {
            LaunchStatus = ex.Message;
        }
    }

    private void OpenGameFolder()
    {
        var directory = _main.CurrentInstance?.GameDirectory;
        if (directory is null || !Directory.Exists(directory))
        {
            LaunchStatus = "游戏目录尚不存在。";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
