using Orion.Core.Net;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orion.App.Mvvm;
using Orion.Core.Diagnostics;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>One catalogue tab: Minecraft versions plus the four Modrinth project kinds.</summary>
public sealed class CatalogKindOption(string key, string name, string? projectType) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;

    private bool _isCurrent;

    /// <summary>当前选中的分类，供页签高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>Modrinth <c>project_type</c>; null for the built-in "Minecraft versions" tab.</summary>
    public string? ProjectType { get; } = projectType;

    public override string ToString() => Name;
}

/// <summary>A Modrinth search hit shown in the catalogue list.</summary>
public sealed class CatalogRow(ModrinthProject project, CatalogViewModel owner, CatalogKindOption kind) : ObservableObject
{
    private ImageSource? _icon;

    public ModrinthProject Project { get; } = project;

    public CatalogKindOption Kind { get; } = kind;

    public string Title => string.IsNullOrWhiteSpace(Project.Title) ? Project.Slug : Project.Title;

    public string Description => Project.Description;

    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "未知作者" : Project.Author;

    public string DownloadsText => Project.Downloads > 0 ? $"{Project.Downloads:N0} 次下载" : "";

    /// <summary>统一列表模板用：作者。</summary>
    public string AuthorText => Author;

    /// <summary>统一列表模板用：无更新时间时留空。</summary>
    public string UpdatedText => "";

    public string KindLabel => Kind.Name;

    public string CategoryText => Project.Categories.Count == 0
        ? ""
        : string.Join(" · ", Project.Categories.Take(3));

    /// <summary>Project icon from Modrinth (downloaded and cached in memory).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallLatestAsync(this));

    public ICommand ShowDetailsCommand => new AsyncRelayCommand(() => owner.ShowDetailsAsync(this));
}

/// <summary>下载中心里的一条 CurseForge 结果。</summary>
public sealed class CurseForgeRow(Orion.Core.Mods.CurseForgePack pack, CatalogViewModel owner,
    Orion.Core.Mods.CurseForgeKind kind) : ObservableObject
{
    private System.Windows.Media.ImageSource? _icon;

    public Orion.Core.Mods.CurseForgePack Pack { get; } = pack;
    public Orion.Core.Mods.CurseForgeKind Kind { get; } = kind;

    /// <summary>项目头像（从 CurseForge logo 下载，失败就留占位）。</summary>
    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public ICommand ShowDetailsCommand => new AsyncRelayCommand(() => owner.OpenCurseForgeDetailAsync(this));

    public string Title => Pack.Name;
    public string Description => Pack.Summary;
    public string DownloadsText => Pack.Downloads > 0 ? $"{Pack.Downloads:N0} 次下载" : "";
    public string KindLabel => owner.KindNameOf(Kind);

    /// <summary>统一列表模板用（CurseForge 搜索接口不返回作者，留空）。</summary>
    public string AuthorText => "";

    /// <summary>统一列表模板用：最后更新时间。</summary>
    public string UpdatedText => Pack.Updated is { } updated
        ? $"更新 {updated.ToLocalTime():yyyy-MM-dd}"
        : "";

    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallCurseForgeItemAsync(this), null,
        ex => owner.Status = "安装失败: " + ex.Message);
}

/// <summary>
/// 下载中心: Minecraft versions (hosted view) plus Modrinth modpacks, mods, shaders and resource packs.
///
/// Features: paging, category filter, project icons, a detail window with the full description and a
/// version picker (so older/newer releases can be installed), and drag-and-drop of a pack file.
/// </summary>
public sealed class CatalogViewModel : ObservableObject
{
    public const int PageSize = 20;

    private readonly MainViewModel _main;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private CatalogKindOption _kind;
    private string _searchText = "";
    private string _status = "";
    private bool _busy;
    private int _page;
    private int _totalHits;
    private string? _category;
    private string _targetInfo = "";
    private bool _dropActive;
    private CatalogDetailViewModel? _detail;
    private int _sourceIndex;
    private string _curseForgeStatus = "";
    private int _cfPage;
    private int _cfTotalHits;
    private bool _detailOpen;

    // A search for the previous tab must never block the next one, and revisiting a tab should not
    // hit the network again: cancel the in-flight request and reuse cached hits.
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _installCts;
    private ActivityItem? _installActivity;
    private int _searchSeq;
    private readonly Dictionary<string, (IReadOnlyList<ModrinthProject> Hits, int TotalHits)> _resultCache =
        new(StringComparer.OrdinalIgnoreCase);

    private string CacheKey(int page) =>
        $"{_kind.Key}|{_category}|{SearchText.Trim()}|{page}|{_main.CurrentInstance?.GameVersion}|{_main.CurrentInstance?.Loader}|scope{ScopeIndex}";

    private void InitializeKindCommand()
    {
        // 占位：真正的赋值在构造函数内完成（见 SelectKindCommand = ...）。
    }
    private readonly System.Windows.Threading.DispatcherTimer _downloadWatch = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    public CatalogViewModel(MainViewModel main)
    {
        _main = main;

        Kinds =
        [
            new("versions", "游戏版本", null),
            new("modpack", "整合包", "modpack"),
            new("mod", "模组", "mod"),
            new("shader", "光影", "shader"),
            new("resourcepack", "材质包", "resourcepack"),
        ];

        _kind = Kinds[0];

        SearchCommand = new AsyncRelayCommand(() => SearchAsync(0), () => !_busy);
        OpenDownloadFolderCommand = new RelayCommand(() => _main.OpenDownloadFolder());
        NextPageCommand = new AsyncRelayCommand(
            () => SearchAsync((IsCurseForgeSource ? _cfPage : _page) + 1), () => !_busy && HasNextPage);
        PreviousPageCommand = new AsyncRelayCommand(
            () => SearchAsync((IsCurseForgeSource ? _cfPage : _page) - 1), () => !_busy && HasPreviousPage);
        RefreshCommand = new AsyncRelayCommand(() => SearchAsync(_page, forceRefresh: true), () => !_busy);
        ToggleFilterCommand = new RelayCommand(() => IsFilterExpanded = !IsFilterExpanded);
        CloseDetailCommand = new RelayCommand(CloseDetail);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => Busy);
        _downloadWatch.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(DownloadSpeedText));
            OnPropertyChanged(nameof(IsDownloading));
        };
        _downloadWatch.Start();
    }
    public RelayCommand CancelOperationCommand { get; }

    /// <summary>Stops the running install; the downloader reacts to the cancellation token.</summary>
    public void CancelOperation()
    {
        _installCts?.Cancel();
        Status = "正在取消当前任务…";
        AppLog.Info("catalog", "install-cancelled-by-user");
    }

    public RelayCommand CloseDetailCommand { get; }

    /// <summary>Detail of the selected project; shown inside the download centre.</summary>
    public CatalogDetailViewModel? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsDetailOpen
    {
        get => _detailOpen;
        private set
        {
            if (!SetProperty(ref _detailOpen, value)) return;
            OnPropertyChanged(nameof(IsListChromeVisible));
            OnPropertyChanged(nameof(IsListOpen));
            OnPropertyChanged(nameof(IsListOpen));
        }
    }

    /// <summary>
    /// The catalogue list is only shown on a resource tab with no detail open. Without the tab
    /// condition its search bar and result card covered the hosted Minecraft version page.
    /// </summary>
    public bool IsListOpen => !_detailOpen && !_cfDetailOpen && IsCatalogueTab;

    /// <summary>详情页是独立子界面：打开后隐藏列表头部（标题 + 筛选栏）。</summary>
    public bool IsListChromeVisible => !_detailOpen && !_cfDetailOpen;

    /// <summary>筛选栏是否展开：折叠后只留一行摘要，把高度让给结果列表。</summary>
    private bool _isFilterExpanded = true;

    public bool IsFilterExpanded
    {
        get => _isFilterExpanded;
        set
        {
            if (!SetProperty(ref _isFilterExpanded, value)) return;
            OnPropertyChanged(nameof(FilterToggleLabel));
            OnPropertyChanged(nameof(FilterChevron));
        }
    }

    public string FilterToggleLabel => IsFilterExpanded ? "收起筛选" : "展开筛选";

    public string FilterChevron => IsFilterExpanded ? "\uE70E" : "\uE70D";

    public ICommand ToggleFilterCommand { get; }

    /// <summary>折叠时显示的一行摘要，避免收起后不知道当前过滤条件。</summary>
    public string FilterSummary
    {
        get
        {
            var sort = SortOptions[Math.Clamp(SortIndex, 0, SortOptions.Count - 1)];
            var loader = LoaderOptions[Math.Clamp(LoaderIndex, 0, LoaderOptions.Count - 1)];
            var category = string.IsNullOrWhiteSpace(SelectedCategory) ? "全部分类" : SelectedCategory!;

            return string.Join(" · ", new[]
            {
                IsCurseForgeSource ? "CurseForge" : "Modrinth",
                IsVersionFiltered && !string.IsNullOrWhiteSpace(_main.CurrentInstance?.GameVersion)
                    ? $"MC {_main.CurrentInstance!.GameVersion}"
                    : "不限版本",
                sort,
                loader,
                category,
            });
        }
    }

    /// <summary>Returns from the detail view to the list.</summary>
    public void CloseDetail()
    {
        IsDetailOpen = false;
        Detail = null;
        Status = string.Empty;
    }

    public IReadOnlyList<CatalogKindOption> Kinds { get; }

    /// <summary>页面顶部只展示资源分类（不含"游戏版本"）。</summary>
    /// <summary>页面顶部只展示资源分类（不含"游戏版本"）。</summary>
    public IReadOnlyList<CatalogKindOption> ResourceKinds => Kinds.Where(k => k.ProjectType is not null).ToList();

    /// <summary>页面顶部的分类页签用：点一下就切到该分类并加载第一页。</summary>
    /// <summary>分类在 Kinds 里的下标（分段控件用）。</summary>
    public int KindIndex
    {
        get
        {
            for (var i = 0; i < Kinds.Count; i++)
            {
                if (ReferenceEquals(Kinds[i], _kind)) return i;
            }
            return 0;
        }
        set
        {
            if (value >= 0 && value < Kinds.Count) Kind = Kinds[value];
            OnPropertyChanged();
        }
    }

    public ICommand SelectKindCommand => new RelayCommand(parameter =>
    {
        if (parameter is string key)
        {
            var target = Kinds.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
            if (target is not null) Kind = target;
        }
        else if (parameter is CatalogKindOption option)
        {
            Kind = option;
        }
    });

    public IReadOnlyList<string> Categories => _kind.Key switch
    {
        "modpack" => ["", "adventure", "combat", "optimization", "technology", "magic", "quests"],
        "mod" => ["", "optimization", "technology", "magic", "storage", "decoration", "library", "adventure", "mobs"],
        "resourcepack" => ["", "combat", "themed", "tweaks", "fonts"],
        _ => [""],
    };

    public AsyncRelayCommand SearchCommand { get; }

    /// <summary>下载中心顶部的「打开下载目录」。</summary>
    public ICommand OpenDownloadFolderCommand { get; }

    /// <summary>
    /// 当前下载任务的实时速度/百分比（下载中心顶部显示）。
    /// 直接读 Activities 里正在跑的那一项，避免再开一套进度通道。
    /// </summary>
    public string DownloadSpeedText
    {
        get
        {
            var running = _main.Activities.Items.FirstOrDefault(a => a.State == ActivityState.Running);
            if (running is null) return "";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(running.Speed)) parts.Add(running.Speed);
            if (!string.IsNullOrWhiteSpace(running.PercentText)) parts.Add(running.PercentText);
            if (parts.Count == 0) return "";

            return "正在下载：" + string.Join(" · ", parts);
        }
    }

    public bool IsDownloading => _main.Activities.Items.Any(a => a.State == ActivityState.Running);

    /// <summary>下载前询问保存位置（详情页等子视图通过宿主转发）。</summary>
    public bool ConfirmDownload(string title) => _main.ConfirmDownloadLocation(title);
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableList<CatalogRow> Results { get; } = [];

    /// <summary>详情页需要读 CurseForge/Instances，这里开一个小口子。</summary>
    public Orion.Core.LauncherContext Context => _main.Context;

    // ── 来源滑块：0 = Modrinth，1 = CurseForge ─────────────
    public IReadOnlyList<string> Sources { get; } = ["Modrinth", "CurseForge"];

    /// <summary>搜索范围：0 = 按当前实例的 MC 版本过滤，1 = 不限版本。</summary>
    public IReadOnlyList<string> SearchScopes { get; } = ["按当前版本", "不限版本"];

    /// <summary>排序方式（两个来源各自映射到自己的排序字段）。</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["相关度", "下载量", "最新更新"];

    private int _sortIndex;

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (!SetProperty(ref _sortIndex, Math.Clamp(value, 0, 2))) return;
            OnPropertyChanged(nameof(FilterSummary));
            _ = RerunSearchAsync();
        }
    }

    /// <summary>加载器筛选：不限 / Forge / NeoForge / Fabric / Quilt。</summary>
    public IReadOnlyList<string> LoaderOptions { get; } = ["不限加载器", "Forge", "NeoForge", "Fabric", "Quilt"];

    private int _loaderIndex;

    public int LoaderIndex
    {
        get => _loaderIndex;
        set
        {
            if (!SetProperty(ref _loaderIndex, Math.Clamp(value, 0, LoaderOptions.Count - 1))) return;
            OnPropertyChanged(nameof(FilterSummary));
            _ = RerunSearchAsync();
        }
    }

    /// <summary>当前来源/类型对应的 CurseForge 排序字段。</summary>
    private int CurseForgeSortField => SortIndex switch { 1 => 6, 2 => 3, _ => 2 };

    /// <summary>当前选中的加载器（Modrinth 用 LoaderKind，CurseForge 用 modLoaderType）。</summary>
    private Core.Instances.LoaderKind? SelectedLoader => LoaderIndex switch
    {
        1 => Core.Instances.LoaderKind.Forge,
        2 => Core.Instances.LoaderKind.NeoForge,
        3 => Core.Instances.LoaderKind.Fabric,
        4 => Core.Instances.LoaderKind.Quilt,
        _ => null,
    };

    private int? SelectedLoaderType => SelectedLoader is { } loader
        ? Orion.Core.Mods.CurseForgeClient.LoaderTypeOf(loader)
        : null;

    /// <summary>CurseForge 的分类是数字 id：按类型拉一次分类表，把选中项的名字映射成 id。</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _cfCategoryIds = new(StringComparer.OrdinalIgnoreCase);

    private async Task<int?> ResolveCurseForgeCategoryIdAsync(Orion.Core.Mods.CurseForgeKind kind)
    {
        if (string.IsNullOrWhiteSpace(SelectedCategory)) return null;

        var key = kind.ToString();
        if (!_cfCategoryIds.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (id, name) in await _main.Context.CurseForge.GetCategoriesAsync(kind).ConfigureAwait(true))
                {
                    map[name] = id;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }

            _cfCategoryIds[key] = map;
        }

        return map.TryGetValue(SelectedCategory, out var categoryId) ? categoryId : null;
    }

    /// <summary>换了排序/筛选就回到第一页重搜（只重搜当前来源）。</summary>
    private async Task RerunSearchAsync()
    {
        try
        {
            if (IsCurseForgeSource) await SearchCurseForgeAsync(0).ConfigureAwait(true);
            else await SearchAsync(0, forceRefresh: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = "重新搜索失败: " + ex.Message;
        }
    }


    private int _scopeIndex;

    public int ScopeIndex
    {
        get => _scopeIndex;
        set
        {
            if (!SetProperty(ref _scopeIndex, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(IsVersionFiltered));
            OnPropertyChanged(nameof(ScopeHint));
            OnPropertyChanged(nameof(FilterSummary));
            _ = RefreshForScopeChangeAsync();
        }
    }

    public bool IsVersionFiltered => ScopeIndex == 0;

    /// <summary>把当前过滤条件写清楚，避免"搜不到"时不知道是被版本过滤掉了。</summary>
    public string ScopeHint
    {
        get
        {
            var instance = _main.CurrentInstance;
            if (!IsVersionFiltered)
            {
                return "不限版本：会列出所有 Minecraft 版本的内容，安装时再按文件兼容性挑版本。";
            }

            return instance is null
                ? "当前没有选中版本，搜索不按版本过滤。"
                : $"只列出支持 {instance.GameVersion} 的内容；机械动力这类没有 {instance.GameVersion} 版本的模组不会出现，可切到「不限版本」。";
        }
    }

    /// <summary>
    /// 侧栏切换当前实例时调用：范围提示要跟着变；如果当前是"按当前版本"过滤，
    /// 结果也应换到新版本的（否则列表还停在旧版本的搜索结果上）。
    /// </summary>
    public void SyncInstance()
    {
        OnPropertyChanged(nameof(ScopeHint));
        OnPropertyChanged(nameof(FilterSummary));

        if (IsCatalogueTab && IsVersionFiltered && !IsVersionsTab && !IsDetailOpen)
        {
            _ = SearchAsync(0);
        }
    }

    /// <summary>切换搜索范围后重跑当前搜索（有搜索词时按新条件搜，否则刷新列表）。</summary>
    private async Task RefreshForScopeChangeAsync()
    {
        try
        {
            if (IsModrinthSource)
            {
                await SearchAsync(0).ConfigureAwait(true);
            }
            else
            {
                await SearchCurseForgeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Status = "按新范围重新搜索失败: " + ex.Message;
        }
    }

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref _sourceIndex, clamped)) return;

            OnPropertyChanged(nameof(IsModrinthSource));
            OnPropertyChanged(nameof(IsCurseForgeSource));
            OnPropertyChanged(nameof(FilterSummary));

            // CurseForge 没有"游戏版本"这一类，切过去时自动落到模组
            if (clamped == 1 && _kind.ProjectType is null) Kind = Kinds[2];

            _cfPage = 0;
            _ = SearchAsync(0);
        }
    }

    public bool IsModrinthSource => _sourceIndex == 0;

    public bool IsCurseForgeSource => _sourceIndex == 1;

    public ObservableList<CurseForgeRow> CurseForgeResults { get; } = [];

    public string CurseForgeStatus
    {
        get => _curseForgeStatus;
        set => SetProperty(ref _curseForgeStatus, value);
    }

    // ── CurseForge 详情 ────────────────────────────────────
    private CurseForgeDetailViewModel? _cfDetail;
    private bool _cfDetailOpen;

    public CurseForgeDetailViewModel? CurseForgeDetail
    {
        get => _cfDetail;
        private set => SetProperty(ref _cfDetail, value);
    }

    public bool IsCurseForgeDetailOpen
    {
        get => _cfDetailOpen;
        private set
        {
            if (!SetProperty(ref _cfDetailOpen, value)) return;
            OnPropertyChanged(nameof(IsListChromeVisible));
            OnPropertyChanged(nameof(IsListOpen));
        }
    }

    public ICommand CloseCurseForgeDetailCommand => new RelayCommand(() =>
    {
        IsCurseForgeDetailOpen = false;
        CurseForgeDetail = null;
    });

    public string KindNameOf(Orion.Core.Mods.CurseForgeKind kind) => kind switch
    {
        Orion.Core.Mods.CurseForgeKind.Modpack => "整合包",
        Orion.Core.Mods.CurseForgeKind.ResourcePack => "材质包",
        Orion.Core.Mods.CurseForgeKind.Shader => "光影",
        _ => "模组",
    };

    /// <summary>当前分类对应的 CurseForge 类型。</summary>
    private Orion.Core.Mods.CurseForgeKind CurrentCurseForgeKind => _kind.Key switch
    {
        "modpack" => Orion.Core.Mods.CurseForgeKind.Modpack,
        "resourcepack" => Orion.Core.Mods.CurseForgeKind.ResourcePack,
        "shader" => Orion.Core.Mods.CurseForgeKind.Shader,
        _ => Orion.Core.Mods.CurseForgeKind.Mod,
    };

    /// <summary>The Minecraft version list page is hosted inside the download centre.</summary>
    public VersionsViewModel VersionsPage => _main.VersionsPage;

    public CatalogKindOption Kind
    {
        get => _kind;
        set
        {
            if (value is null || ReferenceEquals(_kind, value)) return;

            _kind = value;
            _category = null;
            CloseDetail();
            foreach (var option in Kinds) option.IsCurrent = ReferenceEquals(option, value);

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVersionsTab));
            OnPropertyChanged(nameof(IsCatalogueTab));
            OnPropertyChanged(nameof(IsListOpen));
            OnPropertyChanged(nameof(Categories));
            OnPropertyChanged(nameof(KindHint));
            OnPropertyChanged(nameof(SelectedCategory));

            Results.Clear();
            _page = 0;
            _totalHits = 0;
            RaisePaging();
            UpdateTargetInfo();

            if (IsCatalogueTab) _ = SearchAsync(0);
        }
    }

    public bool IsVersionsTab => _kind.ProjectType is null;
    public bool IsCatalogueTab => _kind.ProjectType is not null;

    public string KindHint => _kind.Key switch
    {
        "versions" => "下载 Minecraft 原版与 Mod 加载器，安装后会创建独立的版本目录。",
        "modpack" => "整合包会安装为一个新的版本（含它自带的加载器与模组），不会影响现有版本。",
        "mod" => "模组会安装到当前选中版本的 mods 目录；结果已按该版本的 Minecraft 版本与加载器过滤。",
        "shader" => "光影会安装到当前选中版本的 shaderpacks 目录（需要 Iris / OptiFine 之类的光影支持）。",
        "resourcepack" => "材质包会安装到当前选中版本的 resourcepacks 目录。",
        _ => "",
    };

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string? SelectedCategory
    {
        get => _category;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (!SetProperty(ref _category, normalized)) return;
            OnPropertyChanged(nameof(FilterSummary));
            _ = RerunSearchAsync();
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(ShowSkeleton));
            SearchCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>首次搜索还没结果时显示骨架屏。</summary>
    public bool ShowSkeleton => Busy && Results.Count == 0 && CurseForgeResults.Count == 0;

    public string PageText => _totalHits == 0 && _cfTotalHits == 0
        ? ""
        : IsCurseForgeSource
            ? $"第 {_cfPage + 1} / {CfTotalPages} 页 · 共 {_cfTotalHits:N0} 个"
            : $"第 {_page + 1} / {TotalPages} 页 · 共 {_totalHits:N0} 个";

    public int TotalPages => _totalHits == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_totalHits / (double)PageSize));

    /// <summary>CurseForge 侧的总页数（按 totalCount 算）。</summary>
    public int CfTotalPages => _cfTotalHits == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_cfTotalHits / (double)PageSize));

    public bool HasNextPage => IsCurseForgeSource ? _cfPage + 1 < CfTotalPages : _page + 1 < TotalPages;

    public bool HasPreviousPage => IsCurseForgeSource ? _cfPage > 0 : _page > 0;

    public string TargetInfo
    {
        get => _targetInfo;
        private set => SetProperty(ref _targetInfo, value);
    }

    /// <summary>True while a pack file is dragged over the window.</summary>
    public bool DropActive
    {
        get => _dropActive;
        set => SetProperty(ref _dropActive, value);
    }

    public async Task InitializeAsync()
    {
        UpdateTargetInfo();

        if (IsCatalogueTab && Results.Count == 0) await SearchAsync(0).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs a Modrinth search for the active tab and page.
    ///
    /// Previous behaviour returned early while another search was running, so switching tabs during
    /// a request left the new tab permanently empty. Requests are now cancellable, and a tab that was
    /// visited before renders from cache immediately (then refreshes in the background on demand).
    /// </summary>
    public async Task SearchAsync(int page, bool forceRefresh = false)
    {
        if (IsVersionsTab) return;

        if (IsCurseForgeSource)
        {
            await SearchCurseForgeAsync(page).ConfigureAwait(true);
            return;
        }

        page = Math.Clamp(page, 0, Math.Max(0, TotalPages - 1));
        UpdateTargetInfo();

        var cacheKey = CacheKey(page);

        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out var cached))
        {
            ApplyHits(cached.Hits, cached.TotalHits, page, fromCache: true);
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var seq = ++_searchSeq;

        Busy = true;
        Results.Clear();
        Status = $"正在搜索{_kind.Name}…";

        try
        {
            var query = new ModrinthSearchQuery
            {
                Query = SearchText.Trim(),
                ProjectType = _kind.ProjectType!,
                Category = _category,
                Limit = PageSize,
                Offset = page * PageSize,
                SortBy = SortIndex switch
                {
                    1 => "downloads",
                    2 => "updated",
                    _ => string.IsNullOrWhiteSpace(SearchText) ? "downloads" : "relevance",
                },
            };

            // Mods must match the selected version *and* loader; shaders and resource packs have no
            // loader category on Modrinth (filtering by one returns nothing), so only the Minecraft
            // version is applied there. Modpacks carry their own loader, so they are unfiltered.
            if (IsVersionFiltered && _kind.Key is "mod" or "shader" or "resourcepack"
                && _main.CurrentInstance is { } instance)
            {
                if (!string.IsNullOrWhiteSpace(instance.GameVersion)) query.GameVersion = instance.GameVersion;

                if (_kind.Key == "mod" && instance.Loader != Core.Instances.LoaderKind.Vanilla)
                {
                    query.Loader = instance.Loader;
                }
            }

            // 显式选择的加载器优先于实例推断
            if (SelectedLoader is { } explicitLoader)
            {
                query.Loader = explicitLoader;
            }

            var result = await _main.Context.Modrinth.SearchAsync(query, token).ConfigureAwait(true);

            // A newer search started while this one was in flight: drop the stale answer.
            if (seq != _searchSeq) return;

            _resultCache[cacheKey] = (result.Hits, (int)result.TotalHits);
            ApplyHits(result.Hits, (int)result.TotalHits, page, fromCache: false);

            await LoadIconsAsync(Results).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer tab/search; nothing to report.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (seq != _searchSeq) return;

            Status = "搜索失败：" + ex.Message;
            AppLog.Warn("catalog", "search-failed", ("error", ex.Message));
        }
        finally
        {
            if (seq == _searchSeq) Busy = false;
        }
    }

    /// <summary>Populates the list from a result set (fresh or cached).</summary>
    private void ApplyHits(IReadOnlyList<ModrinthProject> hits, int totalHits, int page, bool fromCache)
    {
        _page = page;
        _totalHits = totalHits;
        RaisePaging();

        Results.Clear();
        foreach (var project in hits)
        {
            var row = new CatalogRow(project, this, _kind);

            // Rows are rebuilt from cache on every revisit; without this the avatars vanished.
            if (!string.IsNullOrWhiteSpace(project.IconUrl) && _iconCache.TryGetValue(project.IconUrl, out var cached))
            {
                row.Icon = cached;
            }

            Results.Add(row);
        }

        Status = Results.Count == 0
            ? "没有找到结果，换个关键词或分类试试。"
            : $"共 {_totalHits:N0} 个结果，本页显示 {Results.Count} 个（每页 {PageSize} 个）"
              + (fromCache ? "（缓存）" : "");
    }

    /// <summary>Downloads and caches project icons; failures simply keep the placeholder.</summary>
    private async Task LoadIconsAsync(IEnumerable<CatalogRow> rows)
    {
        // Icons load concurrently behind the already visible list, so browsing never waits on them.
        using var gate = new SemaphoreSlim(6);
        var tasks = rows.ToList().Select(async row =>
        {
            var url = row.Project.IconUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (_iconCache.TryGetValue(url, out var cached))
            {
                row.Icon = cached;
                return;
            }

            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var bytes = await _main.Context.Http.GetByteArrayAsync(url).ConfigureAwait(true);
                var icon = DecodeIcon(bytes);
                _iconCache[url] = icon;
                row.Icon = icon;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or NotSupportedException)
            {
                _iconCache[url] = null;
                AppLog.Debug("catalog", "icon-failed", ("error", ex.Message));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private static ImageSource? DecodeIcon(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RaisePaging()
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasPreviousPage));
        NextPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
    }

    private void UpdateTargetInfo()
    {
        var instance = _main.CurrentInstance;

        TargetInfo = _kind.Key switch
        {
            "versions" => "",
            "modpack" => "整合包不依赖当前版本，会安装为新的版本。",
            _ when instance is null => "当前未选择版本，请先在「版本管理」中创建或选择一个版本。",
            _ => $"目标版本：{instance.Name}（{instance.GameVersion} · {instance.DisplayLoader}）",
        };
    }

    /// <summary>Opens the in-window detail view: full description plus every published version.</summary>
    public async Task ShowDetailsAsync(CatalogRow row)
    {
        try
        {
            Busy = true;
            Status = $"正在读取 {row.Title} 的详情…";

            var project = await _main.Context.Modrinth.GetProjectAsync(row.Project.Identifier).ConfigureAwait(true)
                          ?? row.Project;

            var versions = await _main.Context.Modrinth.GetVersionsAsync(project.Identifier).ConfigureAwait(true);

            Status = string.Empty;

            // Navigation stays inside the launcher window (only the login flow is a separate window).
            Detail = new CatalogDetailViewModel(_main, this, project, row.Kind, versions, row.Icon);
            IsDetailOpen = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Status = "无法读取详情：" + ex.Message;
            AppLog.Warn("catalog", "details-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Installs the newest matching file of a project (quick install button).</summary>
    public async Task InstallLatestAsync(CatalogRow row)
    {
        Busy = true;

        try
        {
            Status = $"正在获取 {row.Title} 的版本列表…";

            var instance = _main.CurrentInstance;
            var versions = await _main.Context.Modrinth
                .GetVersionsAsync(
                    row.Project.Identifier,
                    row.Kind.Key == "modpack" ? null : instance?.GameVersion,
                    row.Kind.Key == "modpack" ? null : instance?.Loader)
                .ConfigureAwait(true);

            var version = versions.FirstOrDefault(v => v.PrimaryFile is not null);
            if (version is null)
            {
                Status = "没有适用于当前版本的文件，请打开详情页选择其他版本。";
                return;
            }

            await InstallVersionAsync(row.Project, row.Kind, version, row.Title).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Status = "安装失败：" + ex.Message;
            AppLog.Warn("catalog", "install-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Installs one concrete file: modpacks become a new version, everything else goes into the
    /// matching directory of the current version.
    /// </summary>
    public async Task InstallVersionAsync(
        ModrinthProject project,
        CatalogKindOption kind,
        ModrinthVersion version,
        string displayName,
        CancellationToken ct = default)
    {
        if (kind.Key == "modpack")
        {
            await InstallModpackAsync(version, displayName, ct).ConfigureAwait(true);
            return;
        }

        if (!_main.ConfirmDownloadLocation(displayName)) return;

        if (_main.CurrentInstance is not { } instance)
        {
            Status = "请先在「版本管理」中创建或选择一个版本。";
            return;
        }

        var directory = kind.Key switch
        {
            "shader" => Path.Combine(instance.GameDirectory, "shaderpacks"),
            "resourcepack" => Path.Combine(instance.GameDirectory, "resourcepacks"),
            _ => Path.Combine(instance.GameDirectory, "mods"),
        };

        Status = $"正在下载 {version.PrimaryFile?.Filename ?? version.Name}…";
        var installed = await _main.Context.Modrinth.InstallAsync(version, directory, ct).ConfigureAwait(true);

        Status = $"已安装到{kind.Name}目录：{Path.GetFileName(installed)}（{instance.Name}）";
        _main.LogsPage.AppendSystem($"已安装{kind.Name} {displayName} → {installed}");

        if (kind.Key == "mod") await _main.ModsPage.LoadModsAsync().ConfigureAwait(true);
    }

    private async Task InstallModpackAsync(ModrinthVersion version, string displayName, CancellationToken ct)
    {
        if (version.PrimaryFile is null)
        {
            Status = "该版本没有可下载的文件。";
            return;
        }

        Status = $"正在下载 {version.PrimaryFile.Filename}…";

        var temp = Path.Combine(_main.Context.Paths.CacheDir, "packs");
        Directory.CreateDirectory(temp);
        var target = Path.Combine(temp, version.PrimaryFile.Filename);

        if (!File.Exists(target))
        {
            // A 30-100 MB archive used to be fetched invisibly before any activity existed: the
            // catalogue showed 正在安装 while 下载管理 stayed empty. It now goes through the engine, so
            // it gets an activity, a resumable transfer, SHA-1 verification and a real speed readout.
            var archiveActivity = _main.Activities.Begin($"下载整合包 {version.PrimaryFile.Filename}", version.PrimaryFile.Url);

            var archiveProgress = new Progress<DownloadProgress>(p =>
                _main.Activities.Report(archiveActivity,
            p.TotalBytes > 0 ? Math.Clamp((double)p.DownloadedBytes / p.TotalBytes, 0, 1) : 0,
            $"下载整合包 {p.CompletedFiles}/{Math.Max(1, p.TotalFiles)} 个文件",
            $"{p.BytesPerSecond / 1024:0} KB/s"));

            var archiveDownload = await _main.Context.DownloadEngine.DownloadAsync(
            [
                new DownloadRequest
                {
                    TargetPath = target,
                    Urls = [new Uri(version.PrimaryFile.Url)],
                    Sha1 = version.PrimaryFile.Sha1,
                    Size = version.PrimaryFile.Size,
                    Verify = DownloadVerifyPolicy.Always,
                    Label = version.PrimaryFile.Filename,
                },
            ], archiveProgress, ct).ConfigureAwait(true);

            _main.Activities.Finish(archiveActivity,
                archiveDownload.Success ? ActivityState.Completed : ActivityState.Failed,
                archiveDownload.Success ? null : (archiveDownload.Failures.FirstOrDefault()?.Message ?? "整合包下载失败。"));

            if (!archiveDownload.Success)
            {
                Status = "整合包下载失败：" + (archiveDownload.Failures.FirstOrDefault()?.Message ?? "未知错误");
                return;
            }
        }

        await InstallPackFileAsync(target, displayName, ct).ConfigureAwait(true);
    }

    /// <summary>Extracts "65/70" style counters so the task bar can advance during downloads.</summary>
    /// <summary>
    /// Overall progress across the pack pipeline. Each stage reports its own counters, so the bands
    /// below turn them into one monotonic 0..1 number for the task bar:
    /// vanilla client (its own file counts) -> installer jar -> installer dependencies -> Forge run ->
    /// mod downloads -> done.
    /// </summary>
    private static readonly (string Keyword, double From, double To)[] PackPhases =
    [
        ("正在安装原版",       0.00, 0.55),
        ("下载整合包文件",     0.00, 0.70),
        ("正在下载",           0.55, 0.62),
        ("正在预下载安装器依赖", 0.62, 0.78),
        ("正在安装 Fabric",    0.70, 0.90),
        ("正在安装 Quilt",     0.70, 0.90),
        ("正在运行",           0.78, 0.90),
        ("下载模组",           0.90, 0.99),
        ("整合包已安装",       1.00, 1.00),
    ];

    private static double PackPhaseProgress(string line) => Core.Mods.PackInstallProgress.FromLine(line);
    /// <summary>Human label for the archive kind shown before the install starts.</summary>
    private static string DescribePackKind(Core.Mods.PackArchiveKind kind) => kind switch
    {
        Core.Mods.PackArchiveKind.Modrinth => " Modrinth 整合包",
        Core.Mods.PackArchiveKind.CurseForge => " CurseForge 整合包",
        Core.Mods.PackArchiveKind.MinecraftZip => " .minecraft 整合包",
        _ => "整合包（按 Modrinth 处理）",
    };
    /// <summary>A Forge install runs the loader installer with java, so it needs a matching runtime.</summary>
    private async Task<Core.Java.JavaRuntime?> PickInstallerJavaAsync(string? minecraftVersion)
    {
        var runtimes = _main.JavaRuntimes.Count > 0
            ? _main.JavaRuntimes.ToList()
            : (await _main.Context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

        var required = minecraftVersion is null ? 17 : GuessRequiredJava(minecraftVersion);
        var java = Core.Java.JavaDetector.SelectFor(required, runtimes);
        if (java is not null) return java;

        var detected = string.Join("、", runtimes.Select(r => "Java " + r.MajorVersion).Distinct());
        Status = $"安装这个加载器需要 Java {required}，但只检测到 {(detected.Length == 0 ? "无" : detected)}。"
               + "请安装对应 JDK，或在「设置 → Java」指定路径后重试。";
        AppLog.Warn("catalog", "installer-java-missing", ("required", required), ("detected", detected));
        return null;
    }

    /// <summary>整合包/加载器安装需要的 Java 主版本（规则在 Core，便于单测）。</summary>
    public static int RequiredJavaFor(string? minecraftVersion) =>
        Core.Java.JavaVersionRules.RequiredMajorFor(minecraftVersion);

    /// <summary>把安装器输出行换算成 0-1 的进度（规则在 Core）。</summary>
    public static double PackPhaseProgressFor(string line) =>
        Core.Mods.PackInstallProgress.FromLine(line);

    /// <summary>Fallback rule when version metadata is not available yet.</summary>
    private static int GuessRequiredJava(string minecraftVersion) =>
        Core.Java.JavaVersionRules.RequiredMajorFor(minecraftVersion);

    private static double? ParseRatio(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*/\s*(\d+)");
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value, out var done)) return null;
        if (!double.TryParse(match.Groups[2].Value, out var total) || total <= 0) return null;

        return Math.Clamp(done / total, 0, 1);
    }
    /// <summary>Installs a pack file from disk (<c>.mrpack</c> or a Modrinth <c>.zip</c> export).</summary>
    public async Task InstallPackFileAsync(string path, string? name, CancellationToken ct = default)
    {
        // 下载前询问保存位置（用户可在设置里关掉）。取消就什么都不做。
        if (!_main.ConfirmDownloadLocation(name ?? Path.GetFileName(path))) return;

        var activity = _main.Activities.Begin($"安装整合包 {name ?? Path.GetFileName(path)}", path);

        Busy = true;
        Status = "正在安装整合包（会创建新版本）…";

        // The pack pipeline used to run with CancellationToken.None, so 取消任务 could not stop it.
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _installCts.Token);

        try
        {
            // Report into the activity as well: the download manager used to show only the pack path
            // because the progress callback only touched the catalogue status line.
            var lastFraction = 0.0;
            var progress = new Progress<string>(line =>
            {
                Status = line;

                var ratio = PackPhaseProgress(line);
                if (ratio >= 0 && ratio >= lastFraction) lastFraction = ratio;

                var speed = System.Text.RegularExpressions.Regex.Match(line, @"[\d.]+\s*[KMG]?B/s").Value;
                _main.Activities.Report(activity, lastFraction, line, speed);
            });

            // One entry point for every dropped/opened archive: sniff the format first instead of
            // assuming Modrinth. A .zip was previously handed to the .mrpack installer, which failed
            // with "整合包缺少合法的 modrinth.index.json" for CurseForge and domestic packs alike.
            var info = Core.Mods.PackArchiveInstaller.Inspect(path);
            Status = $"识别到{DescribePackKind(info.Kind)}：{info.Name}";

            // 整合包里的 Forge/NeoForge/Fabric 安装器需要 Java：先解析一次，两种格式共用。
            var installerJava = info.Kind == Core.Mods.PackArchiveKind.MinecraftZip
                ? null
                : await PickInstallerJavaAsync(info.MinecraftVersion).ConfigureAwait(true);

            if (installerJava is null && info.Kind != Core.Mods.PackArchiveKind.MinecraftZip)
            {
                // PickInstallerJavaAsync 已经把"需要 Java N"的原因写进 Status。
                _main.Activities.Finish(activity, ActivityState.Failed, Status);
                _main.LogsPage.AppendSystem(Status);
                return;
            }

            var result = info.Kind switch
            {
                Core.Mods.PackArchiveKind.CurseForge => await _main.Context.PackArchives
                    .InstallCurseForgeAsync(path, name ?? info.Name, installerJava, progress, linked.Token)
                    .ConfigureAwait(true),

                Core.Mods.PackArchiveKind.MinecraftZip => await _main.Context.PackArchives
                    .InstallMinecraftZipAsync(path, name ?? info.Name, null, info.MinecraftVersion, linked.Token)
                    .ConfigureAwait(true),

                // Modrinth .mrpack (and anything that declares a modrinth index).
                _ => await _main.Context.ModPacks
                    .InstallAsync(path, name, installerJava, progress, linked.Token)
                    .ConfigureAwait(true),
            };

            _main.Activities.Finish(activity,
                result.Success ? ActivityState.Completed : ActivityState.Failed, result.Error);

            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            Status = result.Success
                ? $"整合包已安装：{name ?? Path.GetFileName(path)}（版本 {result.VersionId}，{result.FilesDownloaded} 个文件）"
                : "整合包安装失败：" + (result.Error ?? "未知错误");

            _main.LogsPage.AppendSystem(Status);

            // 安装报告里如果还有"前置没补上"，直接弹出来并引导到模组页——
            // 否则用户只会看到游戏启动时那片红字 "Error loading mods"。
            var missingNote = result.Warnings?.FirstOrDefault(w => w.Contains("前置", StringComparison.Ordinal)
                                                                   || w.Contains("缺失", StringComparison.Ordinal));
            if (result.Success && missingNote is not null)
            {
                var details = string.Join("\n", result.Warnings!.Take(14));
                var go = System.Windows.MessageBox.Show(
                    details + "\n\n现在启动游戏很可能会停在「Error loading mods」界面。\n" +
                    "要打开「Mod 管理」用『按日志补齐』再补一次吗？",
                    "前置没补齐",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (go == System.Windows.MessageBoxResult.Yes) _main.Navigate("mods");
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已取消安装：" + (name ?? Path.GetFileName(path));
            _main.Activities.Finish(activity, ActivityState.Cancelled);
            AppLog.Info("catalog", "pack-install-cancelled");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException
                                       or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = "整合包安装失败：" + ex.Message;
            AppLog.Warn("catalog", "pack-install-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    /// <summary>
    /// Handles a dropped archive: Modrinth .mrpack, CurseForge zip (manifest.json) or a plain
    /// Minecraft zip (domestic launcher export). Unknown archives are reported, never guessed at.
    /// </summary>
    public async Task HandleDroppedFileAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".mrpack" or ".zip"))
        {
            Status = "只支持整合包压缩包（.mrpack / CurseForge .zip / .minecraft .zip）。";
            return;
        }

        var info = PackArchiveInstaller.Inspect(path);
        AppLog.Info("catalog", "pack-detected", ("kind", info.Kind.ToString()), ("name", info.Name));

        switch (info.Kind)
        {
            case PackArchiveKind.Modrinth:
                await InstallPackFileAsync(path, info.Name).ConfigureAwait(true);
                return;

            case PackArchiveKind.CurseForge:
                await InstallCurseForgeAsync(path, info).ConfigureAwait(true);
                return;

            case PackArchiveKind.MinecraftZip:
                await InstallMinecraftZipAsync(path, info).ConfigureAwait(true);
                return;

            default:
                Status = "无法识别该压缩包：" + (info.Note ?? "缺少 manifest.json / modrinth.index.json / .minecraft");
                return;
        }
    }

    /// <summary>并发下载 CurseForge 头像，复用 Modrinth 那套内存缓存。</summary>
    private async Task LoadCurseForgeIconsAsync(IReadOnlyList<CurseForgeRow> rows)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = rows.Select(async row =>
        {
            var url = row.Pack.LogoUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            if (_iconCache.TryGetValue(url, out var cached))
            {
                row.Icon = cached;
                return;
            }

            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var bytes = await _main.Context.Http.GetByteArrayAsync(url).ConfigureAwait(true);
                var icon = DecodeIcon(bytes);
                _iconCache[url] = icon;
                row.Icon = icon;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                           or NotSupportedException)
            {
                _iconCache[url] = null;
                AppLog.Debug("catalog", "cf-icon-failed", ("error", ex.Message));
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    /// <summary>打开 CurseForge 详情（描述 + 历史版本）。</summary>
    public async Task OpenCurseForgeDetailAsync(CurseForgeRow row)
    {
        var instance = _main.CurrentInstance;

        Busy = true;
        try
        {
            var icon = row.Icon;
            if (icon is null && !string.IsNullOrWhiteSpace(row.Pack.LogoUrl)
                && _iconCache.TryGetValue(row.Pack.LogoUrl, out var cached))
            {
                icon = cached;
            }

            var detail = new CurseForgeDetailViewModel(row, this, instance, icon);
            CurseForgeDetail = detail;
            IsCurseForgeDetailOpen = true;

            await detail.LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>安装 CurseForge 的某个具体文件（历史版本）。</summary>
    public async Task InstallCurseForgeVersionAsync(CurseForgeRow row, Orion.Core.Mods.CurseForgeFile file)
    {
        var instance = _main.CurrentInstance;
        var kind = row.Kind;

        if (kind != Orion.Core.Mods.CurseForgeKind.Modpack && instance is null)
        {
            Status = "请先选择一个版本，再安装" + KindNameOf(kind) + "。";
            return;
        }

        Busy = true;
        var activity = _main.Activities.Begin($"安装 {row.Title}", file.FileName);

        try
        {
            if (kind == Orion.Core.Mods.CurseForgeKind.Modpack)
            {
                var cache = Path.Combine(_main.Context.Paths.Root, "cache", "curseforge");
                Status = $"正在下载 {file.FileName}…";
                var downloadProgress = new Progress<long>(percent =>
                {
                    Status = $"正在下载 {file.FileName}… {percent}%";
                    _main.Activities.Report(activity, percent / 100.0 * 0.25, $"下载整合包 {percent}%", "");
                });

                var zip = await _main.Context.CurseForge
                    .DownloadPackAsync(row.Pack, file, cache, downloadProgress)
                    .ConfigureAwait(true);

                var info = Orion.Core.Mods.PackArchiveInstaller.Inspect(zip);
                var installerJava = await PickInstallerJavaAsync(info.MinecraftVersion).ConfigureAwait(true);
                if (installerJava is null && info.Loader != Orion.Core.Instances.LoaderKind.Vanilla)
                {
                    _main.Activities.Finish(activity, ActivityState.Failed, Status);
                    return;
                }

                Status = "下载完成，开始安装…";
                var installProgress = new Progress<string>(line =>
                {
                    Status = line;
                    var fraction = PackPhaseProgress(line);
                    if (fraction >= 0) _main.Activities.Report(activity, 0.25 + (fraction * 0.75), line, "");
                });

                var result = await _main.Context.PackArchives
                    .InstallCurseForgeAsync(zip, row.Title, installerJava, installProgress)
                    .ConfigureAwait(true);

                Status = result.Success ? $"已安装整合包 {row.Title}" : "安装失败: " + (result.Error ?? "未知错误");
                _main.Activities.Finish(activity,
                    result.Success ? ActivityState.Completed : ActivityState.Failed, result.Error);
                if (result.Success) await _main.ReloadInstancesAsync().ConfigureAwait(true);
                return;
            }

            var folder = Orion.Core.Mods.CurseForgeClient.InstallFolderOf(kind)!;
            var target = Path.Combine(instance!.GameDirectory, folder);
            Status = $"正在下载 {file.FileName}…";
            var saved = await _main.Context.CurseForge
                .DownloadFileAsync(file, target, new Progress<long>(p => Status = $"正在下载 {file.FileName}… {p}%"))
                .ConfigureAwait(true);

            Status = $"已安装 {row.Title} → {folder}/{Path.GetFileName(saved)}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "安装失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>CurseForge 搜索（模组/整合包/材质/光影）。</summary>
    private async Task SearchCurseForgeAsync(int page = 0)
    {
        var kind = CurrentCurseForgeKind;

        if (!_main.Context.CurseForge.HasKey)
        {
            CurseForgeStatus = "还没有 CurseForge API Key：设置 → 网络与下载 → CurseForge 里填一个。";
            return;
        }

        Busy = true;
        CurseForgeResults.Clear();
        CurseForgeStatus = $"正在搜索 CurseForge {KindNameOf(kind)}…";

        try
        {
            var instance = _main.CurrentInstance;
            var versionFilter = IsVersionFiltered ? instance?.GameVersion : null;

            var categoryId = await ResolveCurseForgeCategoryIdAsync(kind).ConfigureAwait(true);

            var result = await _main.Context.CurseForge
                .SearchPageAsync(kind, SearchText, versionFilter, page, PageSize,
                    ct: default, sortField: CurseForgeSortField,
                    categoryId: categoryId, modLoaderType: SelectedLoaderType)
                .ConfigureAwait(true);

            var packs = result.Packs;
            _cfPage = page;
            _cfTotalHits = result.TotalCount;
            RaisePaging();

            var rows = packs.Select(p => new CurseForgeRow(p, this, kind)).ToList();
            CurseForgeResults.Replace(rows);
            Status = packs.Count == 0
                ? "没有找到匹配的内容。"
                : $"找到 {_cfTotalHits:N0} 个{KindNameOf(kind)}（按热度排序）";
            CurseForgeStatus = Status;

            await LoadCurseForgeIconsAsync(rows).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CurseForgeStatus = "搜索失败: " + ex.Message;
            Status = CurseForgeStatus;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 安装一条 CurseForge 结果：整合包走"新建实例"的整包流程，模组/材质/光影直接落到当前实例对应目录。
    /// </summary>
    public async Task InstallCurseForgeItemAsync(CurseForgeRow row)
    {
        var instance = _main.CurrentInstance;
        var kind = row.Kind;

        if (kind != Orion.Core.Mods.CurseForgeKind.Modpack && instance is null)
        {
            Status = "请先选择一个版本，再安装" + KindNameOf(kind) + "。";
            return;
        }

        if (!_main.ConfirmDownloadLocation(row.Title)) return;

        Busy = true;
        var activity = _main.Activities.Begin($"安装 {row.Title}", KindNameOf(kind));
        _installCts = new CancellationTokenSource();

        try
        {
            var loaderType = kind == Orion.Core.Mods.CurseForgeKind.Mod && instance is { Loader: not global::Orion.Core.Instances.LoaderKind.Vanilla }
                ? Orion.Core.Mods.CurseForgeClient.LoaderTypeOf(instance.Loader)
                : (int?)null;

            Status = $"正在解析 {row.Title} 的文件…";
            var file = await _main.Context.CurseForge
                .GetBestFileAsync(row.Pack, instance?.GameVersion, loaderType, kind, _installCts.Token)
                .ConfigureAwait(true);

            if (file is null)
            {
                Status = "这个项目没有可下载的文件（Key 可能已失效）。";
                _main.Activities.Finish(activity, ActivityState.Failed, "没有可用文件");
                return;
            }

            if (kind == Orion.Core.Mods.CurseForgeKind.Modpack)
            {
                var cache = Path.Combine(_main.Context.Paths.Root, "cache", "curseforge");
                Status = $"正在下载 {file.FileName}…";

                // 进度同时写进活动条目：任务面板上能看到百分比，而不是一直"进行中"
                var progress = new Progress<long>(percent =>
                {
                    Status = $"正在下载 {file.FileName}… {percent}%";
                    _main.Activities.Report(activity, percent / 100.0 * 0.25, $"下载整合包 {percent}%", "");
                });

                var zip = await _main.Context.CurseForge
                    .DownloadPackAsync(row.Pack, file, cache, progress, _installCts.Token)
                    .ConfigureAwait(true);

                // Forge/NeoForge 的安装器需要对应版本的 Java：之前这里少了一步，才会报
                // "需要 Java 运行时来运行安装器（MC x.y）"
                var info = Orion.Core.Mods.PackArchiveInstaller.Inspect(zip);
                var installerJava = await PickInstallerJavaAsync(info.MinecraftVersion).ConfigureAwait(true);
                if (installerJava is null && info.Loader != Orion.Core.Instances.LoaderKind.Vanilla)
                {
                    _main.Activities.Finish(activity, ActivityState.Failed, Status);
                    return;
                }

                Status = "下载完成，开始安装（加载器 + 清单模组）…";
                var installProgress = new Progress<string>(line =>
                {
                    Status = line;
                    var fraction = PackPhaseProgress(line);
                    if (fraction >= 0) _main.Activities.Report(activity, 0.25 + (fraction * 0.75), line, "");
                });

                var result = await _main.Context.PackArchives
                    .InstallCurseForgeAsync(zip, row.Title, installerJava, installProgress, _installCts.Token)
                    .ConfigureAwait(true);

                if (result.Success)
                {
                    Status = $"已安装整合包 {row.Title}";
                    _main.Activities.Finish(activity, ActivityState.Completed);
                    _main.LogsPage.AppendSystem(Status);
                    await _main.ReloadInstancesAsync().ConfigureAwait(true);
                }
                else
                {
                    Status = "安装失败: " + (result.Error ?? "未知错误");
                    _main.Activities.Finish(activity, ActivityState.Failed, result.Error);
                }

                return;
            }

            var folder = Orion.Core.Mods.CurseForgeClient.InstallFolderOf(kind)!;
            var target = Path.Combine(instance!.GameDirectory, folder);
            Status = $"正在下载 {file.FileName}…";
            var saved = await _main.Context.CurseForge
                .DownloadFileAsync(file, target,
                    new Progress<long>(percent => Status = $"正在下载 {file.FileName}… {percent}%"),
                    _installCts.Token)
                .ConfigureAwait(true);

            Status = $"已安装 {row.Title} → {folder}/{Path.GetFileName(saved)}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消。";
            _main.Activities.Finish(activity, ActivityState.Failed, "已取消");
        }
        catch (Exception ex)
        {
            Status = "安装失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            Busy = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private async Task InstallCurseForgeAsync(string path, PackArchiveInfo info)
    {
        var activity = _main.Activities.Begin($"安装 CurseForge 整合包 {info.Name}", path);
        _installActivity = activity;
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        Busy = true;
        Status = $"识别到 CurseForge 整合包：{info.Name}（MC {info.MinecraftVersion}）";

        // Progress steps keep the download manager's bar moving while files are fetched.
        var step = 0.05;

        try
        {
            var progress = new Progress<string>(line =>
            {
                Status = line;
                step = Math.Min(0.95, step + 0.03);
                _main.Activities.Report(activity, step, line, "");
            });

            var result = await _main.Context.PackArchives
                .InstallCurseForgeAsync(path, info.Name, null, progress, _installCts.Token)
                .ConfigureAwait(true);

            _main.Activities.Finish(activity, result.Success ? ActivityState.Completed : ActivityState.Failed, result.Error);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            Status = result.Success
                ? $"CurseForge 整合包已安装：{info.Name}（{result.FilesDownloaded} 个模组，{result.OverridesCopied} 个配置）"
                  + (result.Warnings.Count > 0 ? $"；有 {result.Warnings.Count} 条提示见日志" : "")
                : "CurseForge 整合包安装失败：" + (result.Error ?? "未知错误");

            foreach (var warning in result.Warnings) _main.LogsPage.AppendSystem("整合包提示: " + warning);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消安装：" + info.Name;
            _main.Activities.Finish(activity, ActivityState.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
        {
            Status = "CurseForge 整合包安装失败：" + ex.Message;
            AppLog.Warn("catalog", "curseforge-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
            _installActivity = null;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private async Task InstallMinecraftZipAsync(string path, PackArchiveInfo info)
    {
        var baseInstance = _main.CurrentInstance;

        if (info.MinecraftVersion is null && baseInstance is null)
        {
            Status = "该压缩包没有 .minecraft 目录，请先在「版本管理」选择一个版本作为基础，再拖入。";
            return;
        }

        var activity = _main.Activities.Begin($"导入整合包 {info.Name}", path);
        Busy = true;
        Status = $"识别到压缩包整合包：{info.Name}";

        try
        {
            var result = await _main.Context.PackArchives
                .InstallMinecraftZipAsync(path, info.Name, baseInstance?.VersionId, baseInstance?.GameVersion)
                .ConfigureAwait(true);

            _main.Activities.Finish(activity, result.Success ? ActivityState.Completed : ActivityState.Failed, result.Error);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            Status = result.Success
                ? $"整合包已导入：{info.Name}（{result.OverridesCopied} 个文件）"
                : "整合包导入失败：" + (result.Error ?? "未知错误");

            foreach (var warning in result.Warnings) _main.LogsPage.AppendSystem("整合包提示: " + warning);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Status = "整合包导入失败：" + ex.Message;
            AppLog.Warn("catalog", "zip-import-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }
}
