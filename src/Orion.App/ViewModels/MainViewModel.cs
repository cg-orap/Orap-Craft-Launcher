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
    public double Fraction
    {
        get => _fraction;
        set
        {
            if (!SetProperty(ref _fraction, value)) return;
            OnPropertyChanged(nameof(PercentText));
        }
    }

    /// <summary>进度百分比文本（下载管理行里显示具体数值，进度条只看大概）。</summary>
    public string PercentText => _fraction <= 0 ? "" : _fraction >= 1 ? "100%" : $"{_fraction * 100:0.0}%";
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }

    /// <summary>
    /// 实时速度文本：优先用任务自己上报的，没有就退到全局字节计量（TransferMeter）。
    /// 下载管理行里显示的就是这个——之前这里一直是空的，所以看不到速度。
    /// </summary>
    public string SpeedText
    {
        get
        {
            if (!IsRunning) return "";

            var measured = Orion.Core.Net.TransferMeter.FormatSpeed();
            if (!string.IsNullOrWhiteSpace(measured)) return measured;

            // 没有字节在流动（例如正在装加载器）时也占个位，别让这一列看起来是空的
            return string.IsNullOrWhiteSpace(_speed) ? "" : _speed;
        }
    }

    /// <summary>刷新速度显示（由 ActivityCenter 的计时器统一驱动）。</summary>
    public void RefreshSpeed() => OnPropertyChanged(nameof(SpeedText));
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
            OnPropertyChanged(nameof(SpeedText));
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

    /// <summary>
    /// 每 500ms 刷新"进行中"任务的速度文本。
    /// 之前没有这个计时器：SpeedText 从不重新求值，界面上的速度就一直是空的。
    /// </summary>
    public ActivityCenter()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };

        timer.Tick += (_, _) =>
        {
            foreach (var item in Items)
            {
                if (item.IsRunning) item.RefreshSpeed();
            }
        };

        timer.Start();
    }

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

            // 完成就补到 100%：以前各阶段只报到 0.95（"还要留点余量"），
            // 于是安装完条子停在 95%，看起来像没做完。
            if (state == ActivityState.Completed) item.Fraction = 1;
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

    /// <summary>
    /// 创建页面 ViewModel 并记录耗时。
    /// 启动时 `new MainViewModel` 曾经要 900 ms —— 先量出来是哪个页面贵，再决定要不要懒加载。
    /// </summary>
    private static T Time<T>(string name, Func<T> factory) where T : class
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var value = factory();
        var ms = clock.ElapsedMilliseconds;

        if (ms >= 15)
        {
            Orion.Core.Diagnostics.AppLog.Info("startup", "slow-vm", ("vm", name), ("ms", ms));
        }

        return value;
    }

    public MainViewModel(LauncherContext context)
    {
        Context = context;

        Home = Time("HomeViewModel", () => new HomeViewModel(this));
        VersionsPage = Time("VersionsViewModel", () => new VersionsViewModel(this));
        InstancesPage = Time("InstancesViewModel", () => new InstancesViewModel(this));
        CatalogPage = Time("CatalogViewModel", () => new CatalogViewModel(this));
        VersionPickerPage = Time("VersionPickerViewModel", () => new VersionPickerViewModel(this));
        ModsPage = Time("ModsViewModel", () => new ModsViewModel(this));
        ContentPage = Time("ContentViewModel", () => new ContentViewModel(this));
        SpacePage = Time("SpaceViewModel", () => new SpaceViewModel(this));
        SavesPage = Time("SavesViewModel", () => new SavesViewModel(this));
        LibraryPage = Time("LibraryViewModel", () => new LibraryViewModel(this));
        DownloadsPage = Time("DownloadsViewModel", () => new DownloadsViewModel(this));
        SettingsPage = Time("SettingsViewModel", () => new SettingsViewModel(this));
        LogsPage = Time("LogsViewModel", () => new LogsViewModel(this));
        BackupPage = Time("BackupViewModel", () => new BackupViewModel(this));
        ToolboxPage = Time("ToolboxViewModel", () => new ToolboxViewModel(this));

        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key) Navigate(key);
        });

        NavItems.AddRange(
        [
            new NavItem("home", "首页", "\uE80F", "游戏"),
                new NavItem("instances", "版本管理", "\uE81E", "游戏"),   // 层叠图标：和"设置"的齿轮区分
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
            new NavItem("library", "内容管理", "\uE838", "资源"),   // 资源 / 存档 / 空间 三合一
            new NavItem("downloads", "下载管理", "\uE81C", "任务"),
            new NavItem("toolbox", "工具箱", "\uE90F", "系统"),
        new NavItem("backup", "备份", "\uE8F1", "系统"),
        new NavItem("settings", "设置", "\uE713", "系统"),
            new NavItem("logs", "日志", "\uE8A5", "系统"),
        ]);

        InstanceView = System.Windows.Data.CollectionViewSource.GetDefaultView(Instances);
        InstanceView.Filter = item => MatchesInstanceSearch(item as Instance);
        Instances.CollectionChanged += (_, _) => InstanceView.Refresh();

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

    private string _instanceSearch = "";

    /// <summary>侧栏实例搜索词。空 = 全部。</summary>
    public string InstanceSearch
    {
        get => _instanceSearch;
        set
        {
            if (!SetProperty(ref _instanceSearch, value ?? "")) return;
            InstanceView.Refresh();
        }
    }

    /// <summary>侧栏列表用的过滤视图（按名称 / 版本 / 加载器模糊匹配）。</summary>
    public System.ComponentModel.ICollectionView InstanceView { get; }

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

    public BackupViewModel BackupPage { get; }

    private string _backupUsageText = "";
    private double _backupUsagePercent;
    private bool _backupUsageNearLimit;

    /// <summary>侧边栏「备份占用」右侧那行，例如 "1.1 GB / 8 GB"。</summary>
    public string BackupUsageText
    {
        get => _backupUsageText;
        private set => SetProperty(ref _backupUsageText, value);
    }

    /// <summary>进度条百分比（0-100）。</summary>
    public double BackupUsagePercent
    {
        get => _backupUsagePercent;
        private set => SetProperty(ref _backupUsagePercent, value);
    }

    /// <summary>接近配额时高亮。</summary>
    public bool BackupUsageNearLimit
    {
        get => _backupUsageNearLimit;
        private set => SetProperty(ref _backupUsageNearLimit, value);
    }

    /// <summary>重新统计所有实例的快照占用（新建/删除备份、切换数据目录后调用）。</summary>
    public void RefreshBackupUsage()
    {
        var quotaMb = Context.Settings.BackupQuotaMb;
        var directories = Instances.Select(i => i.GameDirectory).ToList();

        // 递归数文件可能几百毫秒：放线程池算，算完再回 UI 赋值。
        _ = Task.Run(() =>
        {
            try
            {
                var usage = Core.Instances.BackupUsage.MeasureForInstances(directories, quotaMb);

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BackupUsageText = usage.Text;
                    BackupUsagePercent = usage.Percent;
                    BackupUsageNearLimit = usage.IsNearLimit;
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("startup", "backup-usage-failed", ("error", ex.Message));
            }
        });
    }
    public ToolboxViewModel ToolboxPage { get; }
    public HomeViewModel Home { get; }
    public VersionsViewModel VersionsPage { get; }
    public InstancesViewModel InstancesPage { get; }
    public CatalogViewModel CatalogPage { get; }
    public VersionPickerViewModel VersionPickerPage { get; }
    public ModsViewModel ModsPage { get; }

    public ContentViewModel ContentPage { get; }

    public SpaceViewModel SpacePage { get; }

    public SavesViewModel SavesPage { get; }

    public LibraryViewModel LibraryPage { get; }
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

    /// <summary>当前生效的下载目录（用户自定义优先，否则 &lt;数据根&gt;\downloads）。</summary>
    public string EffectiveDownloadsDir =>
        Core.Game.DownloadPaths.Resolve(Context.Paths, Context.Settings.DownloadDirectory);

    /// <summary>下载目录是否就是默认目录（界面显示"（默认）"用）。</summary>
    public bool IsDefaultDownloadDir =>
        Core.Game.DownloadPaths.IsDefault(Context.Paths, Context.Settings.DownloadDirectory);

    /// <summary>写入下载目录设置并立即生效（null/空白 = 恢复默认）。</summary>
    public void SetDownloadDirectory(string? directory)
    {
        var normalized = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        Context.Settings.DownloadDirectory = normalized;
        _ = Context.SettingsStore.SaveAsync(Context.Settings);

        OnPropertyChanged(nameof(EffectiveDownloadsDir));
        OnPropertyChanged(nameof(IsDefaultDownloadDir));
    }

    /// <summary>在资源管理器里打开下载目录（不存在就先建出来，避免"打开没反应"）。</summary>
    public void OpenDownloadFolder()
    {
        try
        {
            var directory = EffectiveDownloadsDir;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("download", "open-folder-failed", ("error", ex.Message));
        }
    }

    /// <summary>
    /// 下载前询问保存位置：返回 false 表示用户取消，不要继续下载。
    /// 默认目录 / 选择其他目录 / 取消；选择的自定义目录会被记住（下次仍会询问，可在设置里关掉）。
    /// </summary>
    /// <summary>最近一次"只下载到指定目录"的选择（null = 走默认安装流程）。</summary>
    public string? PendingDownloadOnlyDirectory { get; private set; }

    /// <summary>
    /// 下载位置确认。返回 true 表示继续；
    /// 如果用户选了「只下载到…」，<see cref="PendingDownloadOnlyDirectory"/> 会是那个目录。
    /// </summary>
    public bool ConfirmDownloadLocation(string title, string? fileName = null)
    {
        PendingDownloadOnlyDirectory = null;

        if (!Context.Settings.AskDownloadLocation) return true;

        var defaultDir = Core.Game.DownloadPaths.Default(Context.Paths);
        var current = EffectiveDownloadsDir;

        var choice = Dialogs.AskDownloadLocation(title, fileName ?? title, defaultDir, current);
        if (choice is null) return false;

        var (directory, downloadOnly) = choice.Value;
        if (!downloadOnly) return true;

        // 用户明确要求"只下载到别处"：只记住这一次的目标目录，
        // **不改**设置里的默认下载目录（默认目录只应该在设置页里改）。
        PendingDownloadOnlyDirectory = directory;
        return true;
    }

    /// <summary>用完「只下载到…」的选择后清掉，避免影响下一次安装。</summary>
    public void ClearDownloadOnlyChoice() => PendingDownloadOnlyDirectory = null;

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
            Home.RaisePropertyChanged(nameof(HomeViewModel.SelectedLaunchInstance));
            Home.RaisePropertyChanged(nameof(HomeViewModel.InstanceTitle));
            Home.RaisePropertyChanged(nameof(HomeViewModel.InstanceSubtitle));
            Home.RaisePropertyChanged(nameof(HomeViewModel.LaunchVersionLine));
            ModsPage?.SyncInstance();
            InstancesPage?.SyncSelection();
            CatalogPage?.SyncInstance();

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
    /// <summary>实例是否匹配当前搜索词（名称 / 版本 / 加载器，大小写不敏感）。</summary>
    private bool MatchesInstanceSearch(Instance? instance)
    {
        if (instance is null) return false;

        var query = _instanceSearch.Trim();
        if (query.Length == 0) return true;

        return instance.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (instance.Subtitle ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
               || instance.GameVersion.Contains(query, StringComparison.OrdinalIgnoreCase)
               || instance.Loader.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void CancelRunningWork() => CatalogPage.CancelOperation();

    /// <summary>
    /// Jumps to 版本管理 with the freshly installed version selected and its name pre-filled, so the
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
        // 备份总览每次进入都重新扫一遍磁盘（实例与快照都可能在别处变化）。
        if (key == "backup") BackupPage.Refresh();
        if (key == "toolbox") ToolboxPage.Refresh();

        // 进"版本管理"时如果还没选版本，自动选第一个，避免看到一个空详情页。
        if (key == "instances" && InstancesPage.SelectedInstance is null)
        {
            InstancesPage.SelectedInstance = Instances.FirstOrDefault();
        }

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
            "instances" => InstancesPage,
            "mods" => ModsPage,
            "library" => LibraryPage,
            "content" => LibraryPage,
            "space" => LibraryPage,
            "saves" => LibraryPage,
            "downloads" => DownloadsPage,
            "settings" => SettingsPage,
            "logs" => LogsPage,
            "toolbox" => ToolboxPage,
            "backup" => BackupPage,
            _ => Home,
        };

        if (key == "catalog") _ = CatalogPage.InitializeAsync();
        // 版本选择已并入「版本管理」：侧栏点版本即切换当前版本。
        if (key == "instances") _ = InstancesPage.LoadAsync();
        if (key == "mods") _ = ModsPage.LoadModsAsync();
        if (key is "library" or "content" or "space" or "saves")
        {
            // 旧路由（--page content|saves|space）继续可用：顺带切到对应页签
            LibraryPage.TabIndex = key switch { "saves" => 1, "space" => 2, _ => LibraryPage.TabIndex };
            if (key == "library") LibraryPage.TabIndex = 0;
            _ = LibraryPage.RefreshCurrentAsync();
        }
        if (key == "home") _ = Home.RefreshAsync();
    }

    /// <summary>Loads accounts, instances, Java runtimes and version metadata into the shell.</summary>
    private double _bootProgress;
    private string _bootPhase = "正在启动…";

    /// <summary>启动动画进度（0..1），由真实的初始化阶段推动。</summary>
    public double BootProgress
    {
        get => _bootProgress;
        private set => SetProperty(ref _bootProgress, value);
    }

    /// <summary>启动动画的当前阶段文字。</summary>
    public string BootPhase
    {
        get => _bootPhase;
        private set => SetProperty(ref _bootPhase, value);
    }

    /// <summary>更新启动动画：进度 + 阶段文字，并让界面至少显示一会儿（避免"闪一下就没了"）。</summary>
    private async Task BootStepAsync(double progress, string phase, int minimumMilliseconds = 220)
    {
        BootProgress = progress;
        BootPhase = phase;
        Orion.Core.Diagnostics.AppLog.Info("startup", "boot-step", ("phase", phase), ("at", Environment.TickCount64));

        if (minimumMilliseconds > 0) await Task.Delay(minimumMilliseconds).ConfigureAwait(true);
    }

    public async Task InitializeAsync()
    {
        // The splash must never hinge on anything slow or fragile: everything that touches the disk
        // or the network is best effort, and IsReady is always released in finally so the shell can
        // never sit on 正在初始化… again.
        try
        {
            await BootStepAsync(0.08, "读取启动器配置…", 0).ConfigureAwait(true);

            // InitializeAsync 里是同步的 LoadAll（逐个读 instance.json），
            // 直接 await 会占着 UI 线程，启动动画就卡在那了。
            await Task.Run(() => Context.InitializeAsync()).ConfigureAwait(true);

            await BootStepAsync(0.3, "载入账户…").ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);

            await BootStepAsync(0.52, "扫描版本实例…").ConfigureAwait(true);
            await ReloadInstancesAsync().ConfigureAwait(true);

            await BootStepAsync(0.74, "查找 Java 运行时…", 0).ConfigureAwait(true);
            var runtimes = await Context.JavaDetector.DiscoverAsync().ConfigureAwait(true);
            JavaRuntimes.Replace(runtimes);
            await BootStepAsync(0.92, "准备界面…").ConfigureAwait(true);

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
            BootProgress = 1;
            BootPhase = "就绪";

            // 让进度条走完再收遮罩，否则会"啪"地消失。
            // ORAP_BOOT_HOLD_MS 是给截图/演示用的：让启动动画多停一会儿。
            var hold = 180;
            if (int.TryParse(Environment.GetEnvironmentVariable("ORAP_BOOT_HOLD_MS"), out var extra) && extra > 0)
            {
                hold = Math.Min(15000, hold + extra);
            }

            await Task.Delay(hold).ConfigureAwait(true);
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
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 读盘放到线程池：以前在 UI 线程上 await，磁盘慢的时候启动动画会整个卡住。
        var accounts = await Task.Run(() => Context.AccountStore.LoadAsync()).ConfigureAwait(true);
        var loadMs = clock.ElapsedMilliseconds;

        Accounts.Replace(accounts);
        var replaceMs = clock.ElapsedMilliseconds;

        var selectedId = Context.Settings.SelectedAccountId;
        CurrentAccount = accounts.FirstOrDefault(a => string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                         ?? accounts.FirstOrDefault();
        var setMs = clock.ElapsedMilliseconds;

        AppLog.Info("startup", "accounts-timing",
            ("count", accounts.Count), ("loadMs", loadMs), ("replaceMs", replaceMs), ("currentMs", setMs));
    }

    public async Task ReloadInstancesAsync()
    {
        // LoadAll 会逐个读 instance.json，全同步；放在 UI 线程会冻住启动动画。
        var instances = await Task.Run(() => Context.Instances.LoadAll()).ConfigureAwait(true);
        Instances.Replace(instances);
        RefreshBackupUsage();

        // 快照列表也要读目录，同样挪走
        _ = Task.Run(() =>
        {
            try { BackupPage.Refresh(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });

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
