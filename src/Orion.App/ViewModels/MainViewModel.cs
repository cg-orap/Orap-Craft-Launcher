using System.Windows;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Orion.App.Mvvm;
using Orion.Core;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;
using Orion.Core.Instances;
using Orion.Core.Java;

namespace Orion.App.ViewModels;

public enum ActivityState
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>One install / download / launch job shown on the download manager page.</summary>
public sealed class ActivityItem : ObservableObject
{
    private string _title = "";
    private string _detail = "";
    private double _fraction;
    private string _speed = "";
    private ActivityState _state = ActivityState.Running;
    private string? _error;

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public double Fraction { get => _fraction; set => SetProperty(ref _fraction, value); }
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
    public string? Error { get => _error; set => SetProperty(ref _error, value); }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public string StartedDisplay => StartedAt.ToString("HH:mm:ss");

    public ActivityState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsRunning => State == ActivityState.Running;

    public string StateText => State switch
    {
        ActivityState.Running => "进行中",
        ActivityState.Completed => "已完成",
        ActivityState.Failed => "失败",
        ActivityState.Cancelled => "已取消",
        _ => "",
    };
}

/// <summary>Shared job list + status line, so every page reports progress the same way.</summary>
public sealed class ActivityCenter : ObservableObject
{
    private string _status = "就绪";
    private bool _isBusy;

    public ObservableList<ActivityItem> Items { get; } = [];

    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public ActivityItem Begin(string title, string detail = "")
    {
        var item = new ActivityItem { Title = title, Detail = detail };
        RunOnUi(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > 60) Items.RemoveAt(Items.Count - 1);
        });
        Status = title;
        IsBusy = true;
        return item;
    }

    public void Finish(ActivityItem item, ActivityState state, string? error = null)
    {
        RunOnUi(() =>
        {
            item.State = state;
            item.Error = error;
            if (state != ActivityState.Running) item.Speed = "";
        });

        IsBusy = Items.Any(i => i.State == ActivityState.Running);
        Status = state switch
        {
            ActivityState.Completed => $"{item.Title} — 完成",
            ActivityState.Failed => $"{item.Title} — 失败",
            ActivityState.Cancelled => $"{item.Title} — 已取消",
            _ => Status,
        };
    }

    public void Report(ActivityItem item, double fraction, string detail, string speed)
    {
        RunOnUi(() =>
        {
            item.Fraction = Math.Clamp(fraction, 0, 1);
            item.Detail = detail;
            item.Speed = speed;
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}

/// <summary>Shell view model: owns the launcher context, the shared state and the navigation.</summary>
public sealed class MainViewModel : ObservableObject
{
    private ObservableObject? _currentPage;
    private string _navKey = "home";
    private Account? _currentAccount;
    private ImageSource? _currentAccountAvatar;
    private Instance? _currentInstance;
    private string _versionStatus = "";
    private NavItem? _selectedNavItem;
    private bool _isReady;

    public MainViewModel(LauncherContext context)
    {
        Context = context;

        Home = new HomeViewModel(this);
        VersionsPage = new VersionsViewModel(this);
        InstancesPage = new InstancesViewModel(this);
        CatalogPage = new CatalogViewModel(this);
        ModsPage = new ModsViewModel(this);
        DownloadsPage = new DownloadsViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        LogsPage = new LogsViewModel(this);

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key) Navigate(key);
        });

        NavItems.AddRange(
        [
            new NavItem("home", "首页", "\uE80F"),
            new NavItem("versions", "游戏下载", "\uE896"),
            new NavItem("catalog", "资源下载", "\uE719"),
            new NavItem("instances", "版本管理", "\uE8F1"),
            new NavItem("mods", "Mod 管理", "\uE7B8"),
            new NavItem("downloads", "下载管理", "\uE81C"),
            new NavItem("settings", "设置", "\uE713"),
            new NavItem("logs", "日志", "\uE8A5"),
        ]);

        OpenLoginCommand = new RelayCommand(OpenLogin);

        _selectedNavItem = NavItems[0];
        _currentPage = Home;
    }

    public LauncherContext Context { get; }

    public ActivityCenter Activities { get; } = new();

    public ObservableList<Account> Accounts { get; } = [];
    public ObservableList<Instance> Instances { get; } = [];
    public ObservableList<JavaRuntime> JavaRuntimes { get; } = [];

    public HomeViewModel Home { get; }
    public VersionsViewModel VersionsPage { get; }
    public InstancesViewModel InstancesPage { get; }
    public CatalogViewModel CatalogPage { get; }
    public ModsViewModel ModsPage { get; }
    public DownloadsViewModel DownloadsPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public LogsViewModel LogsPage { get; }

    public ICommand NavigateCommand { get; }

    public ObservableList<NavItem> NavItems { get; } = [];

    public ICommand OpenLoginCommand { get; }

    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (value is null || ReferenceEquals(_selectedNavItem, value)) return;
            _selectedNavItem = value;
            OnPropertyChanged();
            NavigateCore(value.Key);
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
    }

    public ObservableObject? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string NavKey
    {
        get => _navKey;
        private set
        {
            if (!SetProperty(ref _navKey, value)) return;
            OnPropertyChanged(nameof(IsHomeSelected));
            OnPropertyChanged(nameof(IsVersionsSelected));
            OnPropertyChanged(nameof(IsInstancesSelected));
            OnPropertyChanged(nameof(IsModsSelected));
            OnPropertyChanged(nameof(IsDownloadsSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(IsLogsSelected));
        }
    }

    public bool IsHomeSelected => NavKey == "home";
    public bool IsVersionsSelected => NavKey == "versions";
    public bool IsInstancesSelected => NavKey == "instances";
    public bool IsModsSelected => NavKey == "mods";
    public bool IsDownloadsSelected => NavKey == "downloads";
    public bool IsSettingsSelected => NavKey == "settings";
    public bool IsLogsSelected => NavKey == "logs";

    public Account? CurrentAccount
    {
        get => _currentAccount;
        set
        {
            if (!SetProperty(ref _currentAccount, value)) return;
            OnPropertyChanged(nameof(AccountDisplay));
            OnPropertyChanged(nameof(AccountDetail));
            Context.Settings.SelectedAccountId = value?.Id;
            _ = Context.SettingsStore.SaveAsync(Context.Settings);
            _ = LoadAvatarAsync();
        }
    }

    /// <summary>Skin head of the current account (null → the sidebar shows an initial badge).</summary>
    public ImageSource? CurrentAccountAvatar
    {
        get => _currentAccountAvatar;
        private set
        {
            if (!SetProperty(ref _currentAccountAvatar, value)) return;
            OnPropertyChanged(nameof(HasCurrentAccountAvatar));
        }
    }

    public bool HasCurrentAccountAvatar => _currentAccountAvatar is not null;

    private async Task LoadAvatarAsync()
    {
        var account = _currentAccount;

        if (account is null || account.Type != AccountType.Microsoft)
        {
            CurrentAccountAvatar = null;
            return;
        }

        try
        {
            // Accounts saved before skin avatars existed have no texture URL yet; ask the profile
            // API once (only while the token is still valid) and remember the result.
            if (string.IsNullOrWhiteSpace(account.SkinUrl) && !account.IsExpired)
            {
                var refreshed = await Context.Auth.RefreshProfileAsync(account).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(refreshed.SkinUrl))
                {
                    account.SkinUrl = refreshed.SkinUrl;
                    account.SkinModel = refreshed.SkinModel ?? account.SkinModel;
                    if (!string.IsNullOrWhiteSpace(refreshed.Username)) account.Username = refreshed.Username;

                    var accounts = (await Context.AccountStore.LoadAsync().ConfigureAwait(true)).ToList();
                    var index = accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        accounts[index] = account;
                        await Context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);
                    }

                    OnPropertyChanged(nameof(AccountDisplay));
                    AppLog.Info("ui", "avatar-skin-url-resolved", ("name", account.Username));
                }
            }

            var avatar = await Services.SkinAvatars
                .LoadAsync(Context.Http, account.SkinUrl, account.EffectiveUuid, Context.Paths.Root)
                .ConfigureAwait(true);

            // The account may have changed while the download ran.
            if (!ReferenceEquals(account, _currentAccount)) return;

            CurrentAccountAvatar = avatar;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ui", "avatar-load-failed", ("error", ex.Message));
            CurrentAccountAvatar = null;
        }
    }

    public Instance? CurrentInstance
    {
        get => _currentInstance;
        set
        {
            if (!SetProperty(ref _currentInstance, value)) return;
            OnPropertyChanged(nameof(InstanceDisplay));
            OnPropertyChanged(nameof(InstanceDetail));
            Home.RaisePropertyChanged(nameof(HomeViewModel.SelectedInstance));
            Home.RaisePropertyChanged(nameof(HomeViewModel.SelectedLaunchInstance));
            ModsPage.RaisePropertyChanged(nameof(ModsViewModel.SelectedInstance));

            Context.Settings.LastInstanceId = value?.Id;
            _ = Context.SettingsStore.SaveAsync(Context.Settings);
            if (value is not null) _ = ModsPage.LoadModsAsync();
        }
    }

    public string AccountDisplay => CurrentAccount?.Username ?? "未登录";
    public string AccountDetail => CurrentAccount switch
    {
        null => "点击登录",
        { Type: AccountType.Microsoft } a => a.IsExpired ? "微软账户 · 需要刷新令牌" : "微软正版账户",
        _ => "离线账户（仅单机 / 局域网）",
    };

    /// <summary>
    /// Opens the dedicated login window (the only place accounts are managed) and reloads the
    /// account list when it closes.
    /// </summary>
    public void OpenLogin()
    {
        try
        {
            var window = new Views.LoginWindow(
                new LoginViewModel(Context),
                Path.Combine(Context.Paths.CacheDir, "webview2"))
            {
                Owner = Application.Current?.MainWindow,
            };

            AppLog.Info("ui", "login-window-open");
            window.ShowDialog();
            AppLog.Info("ui", "login-window-closed");
        }
        catch (Exception ex)
        {
            AppLog.Error("ui", "login-window-failed", ex);
        }
        finally
        {
            _ = ReloadAccountsAsync();
        }
    }

    public string InstanceDisplay => CurrentInstance?.Name ?? "未选择版本";
    public string InstanceDetail => CurrentInstance is null
        ? "在版本页创建或导入一个版本"
        : $"{CurrentInstance.Subtitle} · 内存 {CurrentInstance.EffectiveMemory} MB";

    public string VersionStatus
    {
        get => _versionStatus;
        set => SetProperty(ref _versionStatus, value);
    }

    public void Navigate(string key) => NavigateCore(key);

    private void NavigateCore(string key)
    {
        var item = NavItems.FirstOrDefault(n => n.Key == key) ?? NavItems[0];
        if (!ReferenceEquals(_selectedNavItem, item))
        {
            _selectedNavItem = item;
            OnPropertyChanged(nameof(SelectedNavItem));
        }

        NavKey = item.Key;
        CurrentPage = item.Key switch
        {
            "versions" => VersionsPage,
            "catalog" => CatalogPage,
            "instances" => InstancesPage,
            "mods" => ModsPage,
            "downloads" => DownloadsPage,
            "settings" => SettingsPage,
            "logs" => LogsPage,
            _ => Home,
        };

        if (item.Key == "catalog") _ = CatalogPage.InitializeAsync();
        if (item.Key == "instances") _ = InstancesPage.LoadAsync();
        if (item.Key == "mods") _ = ModsPage.LoadModsAsync();
        if (item.Key == "home") _ = Home.RefreshAsync();
    }

    /// <summary>Loads accounts, instances, Java runtimes and version metadata into the shell.</summary>
    public async Task InitializeAsync()
    {
        await Context.InitializeAsync().ConfigureAwait(true);

        await ReloadAccountsAsync().ConfigureAwait(true);
        await ReloadInstancesAsync().ConfigureAwait(true);

        var runtimes = await Context.JavaDetector.DiscoverAsync().ConfigureAwait(true);
        JavaRuntimes.Replace(runtimes);

        VersionStatus = $"数据目录 {Context.Paths.Root}";
        SettingsPage.LoadFromSettings();

        AppLog.LineWritten += OnLogLine;
        LogsPage.AppendSystem($"已就绪 · 数据目录 {Context.Paths.Root}");

        await Home.RefreshAsync().ConfigureAwait(true);
        await VersionsPage.LoadVersionsAsync().ConfigureAwait(true);

        IsReady = true;
    }

    public async Task ReloadAccountsAsync()
    {
        var accounts = await Context.AccountStore.LoadAsync().ConfigureAwait(true);
        Accounts.Replace(accounts);

        var selectedId = Context.Settings.SelectedAccountId;
        CurrentAccount = accounts.FirstOrDefault(a => string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                         ?? accounts.FirstOrDefault();
    }

    public async Task ReloadInstancesAsync()
    {
        var instances = Context.Instances.LoadAll();
        Instances.Replace(instances);

        var lastId = Context.Settings.LastInstanceId;

        CurrentInstance = instances.FirstOrDefault(i => string.Equals(i.Id, lastId, StringComparison.OrdinalIgnoreCase))
                          ?? instances.FirstOrDefault();

        // Must be raised *after* CurrentInstance is set: the home page picker reads it.
        Home.RaisePropertyChanged(nameof(HomeViewModel.SelectedLaunchInstance));


        foreach (var root in Context.Importer.VersionRootsOf(instances))
        {
            if (!Context.VersionRepository.ExtraVersionRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                Context.VersionRepository.ExtraVersionRoots.Add(root);
        }

        OnPropertyChanged(nameof(HasInstances));
    }

    public bool HasInstances => Instances.Count > 0;

    private void OnLogLine(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.Ordinal) || line.Contains("[WARN ]", StringComparison.Ordinal))
            LogsPage.AppendLauncher(line);
    }
}

/// <summary>One entry of the left navigation rail.</summary>
public sealed class NavItem(string key, string title, string glyph = "")
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph shown before the label.</summary>
    public string Glyph { get; } = glyph;
}
