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
    public Orion.Core.Mods.CurseForgePack Pack { get; } = pack;
    public Orion.Core.Mods.CurseForgeKind Kind { get; } = kind;

    public string Title => Pack.Name;
    public string Description => Pack.Summary;
    public string DownloadsText => Pack.Downloads > 0 ? $"{Pack.Downloads:N0} 次下载" : "";
    public string KindLabel => owner.KindNameOf(Kind);

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
        $"{_kind.Key}|{_category}|{SearchText.Trim()}|{page}|{_main.CurrentInstance?.GameVersion}|{_main.CurrentInstance?.Loader}";

    private void InitializeKindCommand()
    {
        // 占位：真正的赋值在构造函数内完成（见 SelectKindCommand = ...）。
    }
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
        NextPageCommand = new AsyncRelayCommand(() => SearchAsync(_page + 1), () => !_busy && HasNextPage);
        PreviousPageCommand = new AsyncRelayCommand(() => SearchAsync(_page - 1), () => !_busy && HasPreviousPage);
        RefreshCommand = new AsyncRelayCommand(() => SearchAsync(_page, forceRefresh: true), () => !_busy);
        CloseDetailCommand = new RelayCommand(CloseDetail);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => Busy);
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
            OnPropertyChanged(nameof(IsListOpen));
        }
    }

    /// <summary>
    /// The catalogue list is only shown on a resource tab with no detail open. Without the tab
    /// condition its search bar and result card covered the hosted Minecraft version page.
    /// </summary>
    public bool IsListOpen => !_detailOpen && IsCatalogueTab;

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
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public ObservableList<CatalogRow> Results { get; } = [];

    // ── 来源滑块：0 = Modrinth，1 = CurseForge ─────────────
    public IReadOnlyList<string> Sources { get; } = ["Modrinth", "CurseForge"];

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (!SetProperty(ref _sourceIndex, clamped)) return;

            OnPropertyChanged(nameof(IsModrinthSource));
            OnPropertyChanged(nameof(IsCurseForgeSource));

            // CurseForge 没有"游戏版本"这一类，切过去时自动落到模组
            if (clamped == 1 && _kind.ProjectType is null) Kind = Kinds[2];

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
            _ = SearchAsync(0);
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
            SearchCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string PageText => _totalHits == 0 ? "" : $"第 {_page + 1} / {TotalPages} 页 · 共 {_totalHits:N0} 个";

    public int TotalPages => _totalHits == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_totalHits / (double)PageSize));

    public bool HasNextPage => _page + 1 < TotalPages;
    public bool HasPreviousPage => _page > 0;

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
            await SearchCurseForgeAsync().ConfigureAwait(true);
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
                SortBy = string.IsNullOrWhiteSpace(SearchText) ? "downloads" : "relevance",
            };

            // Mods must match the selected version *and* loader; shaders and resource packs have no
            // loader category on Modrinth (filtering by one returns nothing), so only the Minecraft
            // version is applied there. Modpacks carry their own loader, so they are unfiltered.
            if (_kind.Key is "mod" or "shader" or "resourcepack" && _main.CurrentInstance is { } instance)
            {
                if (!string.IsNullOrWhiteSpace(instance.GameVersion)) query.GameVersion = instance.GameVersion;

                if (_kind.Key == "mod" && instance.Loader != Core.Instances.LoaderKind.Vanilla)
                {
                    query.Loader = instance.Loader;
                }
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

    private static double PackPhaseProgress(string line)
    {
        foreach (var phase in PackPhases)
        {
            if (!line.Contains(phase.Keyword, StringComparison.Ordinal)) continue;

            var inner = ParseRatio(line) ?? 0;
            return Math.Clamp(phase.From + ((phase.To - phase.From) * inner), 0, 1);
        }

        // Unknown line: keep whatever the caller already had (no jump backwards).
        return -1;
    }
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

    /// <summary>Fallback rule when version metadata is not available yet.</summary>
    private static int GuessRequiredJava(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return 17;

        if (major >= 26) return 25;
        if (major > 1) return 21;
        return minor >= 21 ? 21 : minor >= 18 ? 17 : 8;
    }

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

    /// <summary>CurseForge 搜索（模组/整合包/材质/光影）。</summary>
    private async Task SearchCurseForgeAsync()
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
            var packs = await _main.Context.CurseForge
                .SearchAsync(kind, SearchText, instance?.GameVersion, PageSize)
                .ConfigureAwait(true);

            CurseForgeResults.Replace(packs.Select(p => new CurseForgeRow(p, this, kind)));
            Status = packs.Count == 0
                ? "没有找到匹配的内容。"
                : $"找到 {packs.Count} 个{KindNameOf(kind)}（按热度排序）";
            CurseForgeStatus = Status;
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
                var progress = new Progress<long>(percent => Status = $"正在下载 {file.FileName}… {percent}%");
                var zip = await _main.Context.CurseForge
                    .DownloadPackAsync(row.Pack, file, cache, progress, _installCts.Token)
                    .ConfigureAwait(true);

                Status = "下载完成，开始安装（加载器 + 清单模组）…";
                var result = await _main.Context.PackArchives
                    .InstallCurseForgeAsync(zip, row.Title, output: new Progress<string>(line => Status = line),
                        ct: _installCts.Token)
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
