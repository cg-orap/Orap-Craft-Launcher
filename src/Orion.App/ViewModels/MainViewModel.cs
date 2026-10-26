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
    private bool _sidebarCollapsed;
    private ObservableObject? _currentPage;
    private string _navKey = "home";
    private Account? _currentAccount;
    private ImageSource? _currentAccountAvatar;
    private ImageSource? _currentAccountBody;
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
        VersionPickerPage = new VersionPickerViewModel(this);
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
            new NavItem("home", "首页", "\uE80F", "游戏"),
            new NavItem("version-picker", "版本选择", "\uE8B7", "游戏"),
            new NavItem("instances", "版本设置", "\uE713", "游戏"),
            new NavItem("catalog", "下载中心", "\uE896", "资源")
            {
                SubItems =
                {
                    new NavSubItem("versions", "原版游戏"),
                    new NavSubItem("modpack", "整合包"),
                    new NavSubItem("mod", "模组"),
                    new NavSubItem("shader", "光影包"),
                    new NavSubItem("resourcepack", "材质包"),
                },
            },
            new NavItem("mods", "Mod 管理", "\uE7B8", "资源"),
            new NavItem("downloads", "下载管理", "\uE81C", "任务"),
            new NavItem("settings", "设置", "\uE713", "系统"),
            new NavItem("logs", "日志", "\uE8A5", "系统"),
        ]);

        NavView = System.Windows.Data.CollectionViewSource.GetDefaultView(NavItems);
        NavView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Group"));

        OpenLoginCommand = new RelayCommand(OpenLogin);
        OpenCatalogKindCommand = new RelayCommand(OpenCatalogKind);

        _selectedNavItem = NavItems[0];
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarCollapsed = !IsSidebarCollapsed);

        _currentPage = Home;
    }

    public LauncherContext Context { get; }

    public ActivityCenter Activities { get; } = new();

    public ObservableList<Account> Accounts { get; } = [];
    public ObservableList<Instance> Instances { get; } = [];
    public ObservableList<JavaRuntime> JavaRuntimes { get; } = [];

    /// <summary>二级侧栏（实例列表）是否收起为 44px 窄条。</summary>
    public bool IsSidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (!SetProperty(ref _sidebarCollapsed, value)) return;
            OnPropertyChanged(nameof(SidebarToggleGlyph));
        }
    }

    /// <summary>窄条上显示"展开"箭头，展开时显示"收起"箭头。</summary>
    public string SidebarToggleGlyph => IsSidebarCollapsed ? "\uE76C" : "\uE76B";

    public ICommand ToggleSidebarCommand { get; }
    public HomeViewModel Home { get; }
    public VersionsViewModel VersionsPage { get; }
    public InstancesViewModel InstancesPage { get; }
    public CatalogViewModel CatalogPage { get; }
    public VersionPickerViewModel VersionPickerPage { get; }
    public ModsViewModel ModsPage { get; }
    public DownloadsViewModel DownloadsPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public LogsViewModel LogsPage { get; }

    public ICommand NavigateCommand { get; }

    public ObservableList<NavItem> NavItems { get; } = [];

    public System.ComponentModel.ICollectionView NavView { get; }

    public ICommand OpenLoginCommand { get; }
    public ICommand OpenCatalogKindCommand { get; }

    public NavItem? SelectedNavItem
    {
        get => _selectedNavItem;
        set
        {
            if (value is null || ReferenceEquals(_selectedNavItem, value)) return;
            _selectedNavItem = value;
            OnPropertyChanged();

            // 图标导航没有子项列表：点「下载中心」直接进页面，分类由页面顶部页签切换。
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

    /// <summary>Full-body 2D figure of the current account's skin.</summary>
    public ImageSource? CurrentAccountBody
    {
        get => _currentAccountBody;
        private set
        {
            if (!SetProperty(ref _currentAccountBody, value)) return;
            OnPropertyChanged(nameof(HasCurrentAccountBody));
        }
    }

    public bool HasCurrentAccountBody => _currentAccountBody is not null;

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

            CurrentAccountBody = await Services.SkinAvatars
                .LoadAsyncBodySafe(Context.Http, account.SkinUrl, account.EffectiveUuid, Context.Paths.Root)
                .ConfigureAwait(true);
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
            Home.RaisePropertyChanged(nameof(HomeViewModel.InstanceTitle));
            Home.RaisePropertyChanged(nameof(HomeViewModel.InstanceSubtitle));
            Home.RaisePropertyChanged(nameof(HomeViewModel.LaunchVersionLine));
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
    /// <summary>Jump straight into one of the download centre's resource tabs.</summary>
    private void OpenCatalogKind(object? parameter)
    {
        if (parameter is not NavSubItem sub) return;

        var kindKey = string.Equals(sub.Key, "versions", StringComparison.OrdinalIgnoreCase) ? "versions" : sub.Key;

        var kind = CatalogPage.Kinds.FirstOrDefault(k =>
            string.Equals(k.Key, kindKey, StringComparison.OrdinalIgnoreCase));

        if (kind is not null) CatalogPage.Kind = kind;
        Navigate("catalog");
    }

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

    /// <summary>Cancels whatever catalogue/install operation is currently running.</summary>
    public void CancelRunningWork() => CatalogPage.CancelOperation();

    /// <summary>
    /// Jumps to 版本设置 with the freshly installed version selected and its name pre-filled, so the
    /// user can rename it right after the install (installs otherwise keep the pack/file name).
    /// </summary>
    public async Task OpenInstanceForRenameAsync(string? instanceId, string? suggestedName = null)
    {
        await ReloadInstancesAsync().ConfigureAwait(true);

        var target = Instances.FirstOrDefault(i =>
            string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));

        if (target is null && !string.IsNullOrWhiteSpace(suggestedName))
        {
            target = Instances.FirstOrDefault(i =>
                string.Equals(i.Name, suggestedName, StringComparison.OrdinalIgnoreCase));
        }

        if (target is not null)
        {
            InstancesPage.SelectedInstance = target;
            InstancesPage.DisplayName = target.Name;
        }

        Navigate("instances");
    }

    private void NavigateCore(string key)
    {
        // Sub-pages such as "version-picker" are not entries of the rail: only move the highlight
        // when the key really is a rail entry, otherwise the page would silently stay on 首页.
        var item = NavItems.FirstOrDefault(n => n.Key == key);
        if (item is not null && !ReferenceEquals(_selectedNavItem, item))
        {
            _selectedNavItem = item;
            OnPropertyChanged(nameof(SelectedNavItem));
        }

        NavKey = item?.Key ?? key;
        CurrentPage = key switch
        {
            "versions" => CatalogPage,   // legacy route: versions now live in the download centre
            "catalog" => CatalogPage,
            "version-picker" => VersionPickerPage,
            "instances" => InstancesPage,
            "mods" => ModsPage,
            "downloads" => DownloadsPage,
            "settings" => SettingsPage,
            "logs" => LogsPage,
            _ => Home,
        };

        if (key == "catalog") _ = CatalogPage.InitializeAsync();
        if (key == "version-picker") VersionPickerPage.Refresh();
        if (key == "instances") _ = InstancesPage.LoadAsync();
        if (key == "mods") _ = ModsPage.LoadModsAsync();
        if (key == "home") _ = Home.RefreshAsync();
    }

    /// <summary>Loads accounts, instances, Java runtimes and version metadata into the shell.</summary>
    public async Task InitializeAsync()
    {
        // The splash must never hinge on anything slow or fragile: everything that touches the disk
        // or the network is best effort, and IsReady is always released in finally so the shell can
        // never sit on 正在初始化… again.
        try
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
        }
        catch (Exception ex)
        {
            AppLog.Warn("startup", "initialize-failed", ("error", ex.Message));
            LogsPage.AppendSystem("初始化部分失败：" + ex.Message);
        }
        finally
        {
            IsReady = true;
            AppLog.Info("ui", "ready");
        }

        // Version metadata needs the network; load it behind the splash instead of blocking it.
        _ = LoadMetadataAsync();
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            await Home.RefreshAsync().ConfigureAwait(true);
            await VersionsPage.LoadVersionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("startup", "metadata-failed", ("error", ex.Message));
        }
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
public sealed class NavItem(string key, string title, string glyph = "", string group = "") : Mvvm.ObservableObject
{
    private bool _expanded;

    public string Key { get; } = key;
    public string Title { get; } = title;

    /// <summary>True while this entry's sub-menu is unfolded.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => SetProperty(ref _expanded, value);
    }

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets glyph shown before the label.</summary>
    public string Glyph { get; } = glyph;

    public string Group { get; } = group;

    /// <summary>Second level entries revealed while this item is selected (下载中心 has four).</summary>
    public List<NavSubItem> SubItems { get; } = [];
}

/// <summary>A nested navigation entry such as 整合包 / 模组 / 光影 / 材质包.</summary>
public sealed class NavSubItem(string key, string title)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
}
