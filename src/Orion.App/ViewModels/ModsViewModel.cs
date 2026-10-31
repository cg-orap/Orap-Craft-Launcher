using System.Diagnostics;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Instances;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>A mod row with commands for enabling/disabling and removing it.</summary>
public sealed class ModRow : ObservableObject
{
    private readonly ModInfo _mod;
    private readonly ModsViewModel _owner;

    public ModRow(ModInfo mod, ModsViewModel owner, bool compatible)
    {
        _mod = mod;
        _owner = owner;
        IsCompatible = compatible;

        ToggleCommand = new RelayCommand(() => _owner.ToggleMod(this));
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteModAsync(this));
    }

    public ModInfo Mod => _mod;
    public bool IsCompatible { get; }

    public string Title => _mod.DisplayName;
    public string Version => _mod.Version ?? "";
    public string Loader => _mod.Loader ?? "unknown";
    public string FileName => _mod.FileName;
    public string SizeText => _mod.Size > 0 ? $"{_mod.Size / 1024.0:0.0} KB" : "";
    public string DependencyText => _mod.Dependencies.Count > 0 ? "依赖: " + string.Join(", ", _mod.Dependencies) : "";
    public bool HasDependencies => _mod.Dependencies.Count > 0;

    public bool Enabled => _mod.Enabled;

    public string ToggleLabel => _mod.Enabled ? "禁用" : "启用";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(FileName));
    }
}

/// <summary>冲突里点名的一个文件 + 针对它的「禁用」命令。</summary>
public sealed record ConflictFileAction(string File, ICommand DisableCommand);

/// <summary>一条冲突的展示行：标题 + 涉及文件 + 逐个禁用按钮。</summary>
public sealed class ConflictRow
{
    private readonly ModsViewModel _owner;

    public ConflictRow(ModConflict conflict, ModsViewModel owner)
    {
        _owner = owner;
        Conflict = conflict;
        FileActions = [.. conflict.Files.Select(file =>
            new ConflictFileAction(file,
                new AsyncRelayCommand(() => _owner.DisableConflictFileAsync(file))))];
    }

    public ModConflict Conflict { get; }

    public bool IsError => Conflict.IsBlocking;
    public string Code => Conflict.Code;
    public string Title => Conflict.Title;
    public string Detail => Conflict.Detail;
    public string SeverityLabel => IsError ? "必须处理" : "建议检查";

    /// <summary>涉及的文件名（一行显示）。</summary>
    public string Files => string.Join(" · ", Conflict.Files);

    /// <summary>每个文件一个「禁用」按钮。</summary>
    public IReadOnlyList<ConflictFileAction> FileActions { get; }
}

/// <summary>Modrinth search result row.</summary>
public sealed class ModrinthRow(ModrinthProject project, ModsViewModel owner)
{
    public ModrinthProject Project { get; } = project;
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string Author => Project.Author;
    public string Downloads => $"{Project.Downloads:N0} 次下载";
    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallFromModrinthAsync(this));
}

/// <summary>Mod management page: local mods + Modrinth search.</summary>
/// <summary>CurseForge 整合包搜索结果行。</summary>
public sealed class CurseForgePackRow(Orion.Core.Mods.CurseForgePack pack, ModsViewModel owner)
{
    public Orion.Core.Mods.CurseForgePack Pack { get; } = pack;
    public string Title => Pack.Name;
    public string Summary => Pack.Summary;
    public string Downloads => Pack.Downloads.ToString("N0") + " 次下载";
    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallCurseForgePackAsync(this), null,
        ex => owner.PackStatus = "安装失败: " + ex.Message);
}

public sealed class ModsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private string _status = "选择一个版本以管理 Mod。";
    private string _searchText = "";
    private bool _isBusy;
    private bool _isSearching;
    private Instance? _selectedInstance;

    public ModsViewModel(MainViewModel main)
    {
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync);
        InitUpdateCommands();
        ModsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = MatchesFilter;
        SetModFilterCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key && key.Length > 0) ModFilter = key.ToUpperInvariant();
        });
        _main = main;

        RescanCommand = new AsyncRelayCommand(LoadModsAsync, () => SelectedInstance is not null);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchText) && !IsSearching);
        SearchCurseForgeCommand = new AsyncRelayCommand(SearchCurseForgeAsync, () => !IsPackSearching);
        RepairDependenciesCommand = new AsyncRelayCommand(RepairDependenciesAsync, () => !IsBusy);
        ToggleConflictCardCommand = new RelayCommand(() => IsConflictCardExpanded = !IsConflictCardExpanded);
        RefreshCurseForgeKeyCommand = new RelayCommand(() => OnPropertyChanged(nameof(HasCurseForgeKey)));
        OpenFolderCommand = new RelayCommand(OpenModsFolder);
        OpenModrinthPageCommand = new RelayCommand(OpenModrinthPage);
    }

    private string _modFilter = "ALL";

    private bool MatchesFilter(object item) => item is ModRow row && _modFilter switch
    {
        "ENABLED" => row.Enabled,
        "DISABLED" => !row.Enabled,
        "CONFLICT" => ConflictFileNames.Contains(row.FileName),
        _ => true,
    };

    /// <summary>检查更新：只查询，不下载；发现可更新项后弹确认，用户同意才下载。</summary>
    public ICommand CheckUpdatesCommand { get; }

    public ObservableList<UpdateRow> Updates { get; } = [];

    public ICommand RescanCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenModrinthPageCommand { get; }

    public ObservableList<ModRow> Mods { get; } = [];

    /// <summary>当前版本里检测到的模组冲突（重复 modId、加载器不匹配、版本互斥等）。</summary>
    public ObservableList<ConflictRow> Conflicts { get; } = [];

    /// <summary>牵涉冲突的文件名，用于「有冲突」筛选。</summary>
    private readonly HashSet<string> ConflictFileNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 冲突检测结果缓存：键是实例，值是"目录指纹 + 结果"。
    /// 打开一次 300+ 模组的页面要读几千个 zip 条目，重复进页面没必要重算。
    /// </summary>
    private readonly Dictionary<string, (string Fingerprint, IReadOnlyList<ModConflict> Result)> _conflictCache = [];

    /// <summary>模组列表缓存：一次会话里重复进页面不必再把几百个 jar 读一遍。</summary>
    private readonly Dictionary<string, (string Fingerprint, IReadOnlyList<ModInfo> Result)> _modsCache = [];

    private string _modScanText = "";
    public string ModScanText
    {
        get => _modScanText;
        private set => SetProperty(ref _modScanText, value);
    }

    /// <summary>界面上最多显示几条冲突（其余折叠成一行提示，避免一次渲染上百张卡片）。</summary>
    private const int MaxVisibleConflicts = 8;

    private int _hiddenConflictCount;

    public int HiddenConflictCount
    {
        get => _hiddenConflictCount;
        private set
        {
            if (!SetProperty(ref _hiddenConflictCount, value)) return;
            OnPropertyChanged(nameof(HasHiddenConflicts));
            OnPropertyChanged(nameof(HiddenConflictText));
        }
    }

    public bool HasHiddenConflicts => HiddenConflictCount > 0;

    public string HiddenConflictText => $"还有 {HiddenConflictCount} 条未展开（同类问题已合并，导出报告可看全部）";

    /// <summary>冲突检测耗时，用于判断卡顿是不是这一步造成的。</summary>
    private string _conflictScanText = "";
    public string ConflictScanText
    {
        get => _conflictScanText;
        private set => SetProperty(ref _conflictScanText, value);
    }

    public bool HasConflicts => TotalErrorCount + TotalWarningCount > 0;

    // null = 还没手动切换过，按"有必须处理的就展开、否则收起"给默认值
    private bool? _conflictExpanded;

    /// <summary>冲突卡片是否展开：收起时只留标题行，列表不渲染（几百条时不占地方也不拖渲染）。</summary>
    public bool IsConflictCardExpanded
    {
        get => _conflictExpanded ?? TotalErrorCount > 0;
        set
        {
            _conflictExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConflictChevron));
            OnPropertyChanged(nameof(ConflictToggleHint));
        }
    }

    /// <summary>标题行右侧的箭头：展开时朝上，收起时朝下。</summary>
    public string ConflictChevron => IsConflictCardExpanded ? "\uE70E" : "\uE70D";

    public string ConflictToggleHint => IsConflictCardExpanded ? "收起冲突列表" : "展开冲突列表";

    public int ConflictErrorCount => TotalErrorCount;

    public int ConflictWarningCount => TotalWarningCount;

    public string ConflictSummary
    {
        get
        {
            if (Conflicts.Count == 0) return "";
            var parts = new List<string>();
            if (ConflictErrorCount > 0) parts.Add($"{ConflictErrorCount} 个必须处理");
            if (ConflictWarningCount > 0) parts.Add($"{ConflictWarningCount} 条建议检查");
            return string.Join("，", parts);
        }
    }

    /// <summary>列表筛选：ALL / ENABLED / DISABLED（与设计稿的页签一致）。</summary>
    public string ModFilter
    {
        get => _modFilter;
        set
        {
            if (!SetProperty(ref _modFilter, value)) return;
            ModsView?.Refresh();
            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(DisabledCount));
        }
    }

    public System.ComponentModel.ICollectionView ModsView { get; }

    public ICommand SetModFilterCommand { get; }

    /// <summary>模组列表筛选（分段控件用）。</summary>
    public IReadOnlyList<string> ModFilters { get; } = ["全部", "已启用", "已禁用", "有冲突"];

    public int FilterIndex
    {
        get => _modFilter switch { "ENABLED" => 1, "DISABLED" => 2, "CONFLICT" => 3, _ => 0 };
        set
        {
            var key = Math.Clamp(value, 0, 3) switch
            {
                1 => "ENABLED",
                2 => "DISABLED",
                3 => "CONFLICT",
                _ => "ALL",
            };
            SetModFilterCommand.Execute(key);
            OnPropertyChanged();
        }
    }

    public int DisabledCount => Mods.Count(m => !m.Enabled);
    public ObservableList<ModrinthRow> SearchResults { get; } = [];

    // ── CurseForge 整合包 ────────────────────────────────
    public ObservableList<CurseForgePackRow> CurseForgePacks { get; } = [];

    private string _curseForgeSearchText = "";
    private bool _isPackSearching;
    private string _packStatus = "";

    public string CurseForgeSearchText
    {
        get => _curseForgeSearchText;
        set => SetProperty(ref _curseForgeSearchText, value);
    }

    public bool IsPackSearching
    {
        get => _isPackSearching;
        set => SetProperty(ref _isPackSearching, value);
    }

    public string PackStatus
    {
        get => _packStatus;
        set => SetProperty(ref _packStatus, value);
    }

    public bool HasCurseForgeKey => _main.Context.CurseForge.HasKey;

    public ICommand SearchCurseForgeCommand { get; private set; } = null!;

    /// <summary>检查当前实例缺少的必需前置并自动补齐。</summary>
    public ICommand RepairDependenciesCommand { get; private set; } = null!;

    /// <summary>展开/收起模组冲突卡片。</summary>
    public ICommand ToggleConflictCardCommand { get; private set; } = null!;
    public ICommand RefreshCurseForgeKeyCommand { get; private set; } = null!;

    public Instance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            var instance = value ?? _main.CurrentInstance;
            if (!SetProperty(ref _selectedInstance, instance)) return;

            OnPropertyChanged(nameof(InstanceLabel));
            ((AsyncRelayCommand)RescanCommand).RaiseCanExecuteChanged();
            _ = LoadModsAsync();
        }
    }

    /// <summary>页面顶部下拉用：直接在本页切换版本。</summary>
    public ObservableList<Instance> Instances => _main.Instances;

    /// <summary>跟随全局当前版本（侧栏/主页切换时同步本页）。</summary>
    public void SyncInstance()
    {
        if (_main.CurrentInstance is { } instance && !ReferenceEquals(SelectedInstance, instance))
        {
            SelectedInstance = instance;
        }
    }

    public string InstanceLabel => SelectedInstance is null
        ? "未选择版本"
        : $"{SelectedInstance.Name} · {SelectedInstance.DisplayLoader} · MC {SelectedInstance.GameVersion}";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();
        }
    }

    public int EnabledCount => Mods.Count(m => m.Enabled);
    public int IncompatibleCount => Mods.Count(m => !m.IsCompatible);

    public async Task LoadModsAsync()
    {
        var instance = SelectedInstance ?? _main.CurrentInstance;
        if (instance is null)
        {
            Mods.Clear();
            Status = "请先创建或选择一个版本。";
            return;
        }

        SelectedInstance = instance;
        IsBusy = true;

        try
        {
            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            var fingerprint = await Task.Run(() => Fingerprint(modsDirectory)).ConfigureAwait(true);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<ModInfo> mods;

            if (_modsCache.TryGetValue(instance.Id, out var cachedMods) && cachedMods.Fingerprint == fingerprint)
            {
                mods = cachedMods.Result;
                ModScanText = "列表：已缓存";
            }
            else
            {
                mods = await ModScanner.ScanAsync(instance.GameDirectory).ConfigureAwait(true);
                _modsCache[instance.Id] = (fingerprint, mods);
                stopwatch.Stop();
                ModScanText = $"列表：{stopwatch.Elapsed.TotalSeconds:0.0} 秒";
            }

            Mods.Replace(mods.Select(m => new ModRow(m, this, ModScanner.IsCompatibleWith(m, instance.Loader))));

            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(IncompatibleCount));

            await RefreshConflictsAsync(instance).ConfigureAwait(true);

            Status = Mods.Count == 0
                ? "该版本还没有安装 Mod。"
                : $"共 {Mods.Count} 个 Mod，{EnabledCount} 个启用" +
                  (IncompatibleCount > 0 ? $"，{IncompatibleCount} 个与当前加载器不匹配" : "") +
                  $"（{ModScanText}；{ConflictScanText}）";
        }
        catch (Exception ex)
        {
            Status = "扫描 Mod 失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 冲突检测：纯离线读 jar，比启动一次快得多。失败不影响模组列表（只是不显示冲突）。
    /// </summary>
    public async Task RefreshConflictsAsync(Instance instance)
    {
        try
        {
            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            var loader = instance.Loader;
            var gameVersion = instance.GameVersion;
            var instanceId = instance.Id;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var fingerprint = await Task.Run(() => Fingerprint(modsDirectory)).ConfigureAwait(true);

            IReadOnlyList<ModConflict> found;
            var cached = _conflictCache.TryGetValue(instanceId, out var hit)
                         && hit.Fingerprint == fingerprint;

            if (cached)
            {
                found = hit.Result;
            }
            else
            {
                found = await Task.Run(() =>
                    ModConflictScanner.Scan(modsDirectory, loader, gameVersion)).ConfigureAwait(true);

                _conflictCache[instanceId] = (fingerprint, found);
            }

            stopwatch.Stop();
            ConflictScanText = cached
                ? "冲突检测：已缓存"
                : $"冲突检测：{stopwatch.Elapsed.TotalSeconds:0.00} 秒（{fingerprint}）";

            ConflictFileNames.Clear();
            foreach (var conflict in found)
            {
                foreach (var file in conflict.Files) ConflictFileNames.Add(file);
            }

            // 界面只渲染前 N 条：几百条卡片既看不清也拖慢滚动
            ApplyConflictDisplay(found);

            OnPropertyChanged(nameof(IsConflictCardExpanded));
            OnPropertyChanged(nameof(ConflictChevron));
            OnPropertyChanged(nameof(ConflictToggleHint));
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(ConflictErrorCount));
            OnPropertyChanged(nameof(ConflictWarningCount));
            OnPropertyChanged(nameof(ConflictSummary));
            ModsView?.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "冲突检测失败: " + ex.Message;
        }
    }

    /// <summary>错误全部显示，警告只显示前 N 条，剩下的折叠成一行。</summary>
    private void ApplyConflictDisplay(IReadOnlyList<ModConflict> found)
    {
        var errors = found.Where(c => c.IsBlocking).ToList();
        var warnings = found.Where(c => !c.IsBlocking).ToList();

        var visible = errors.Concat(warnings.Take(Math.Max(0, MaxVisibleConflicts - errors.Count))).ToList();
        var hidden = warnings.Count - warnings.Take(Math.Max(0, MaxVisibleConflicts - errors.Count)).Count();

        Conflicts.Replace(visible.Select(c => new ConflictRow(c, this)));
        HiddenConflictCount = Math.Max(0, hidden);
        TotalErrorCount = errors.Count;
        TotalWarningCount = warnings.Count;
    }

    private int _totalErrorCount;
    private int _totalWarningCount;

    private int TotalErrorCount
    {
        get => _totalErrorCount;
        set => SetProperty(ref _totalErrorCount, value);
    }

    private int TotalWarningCount
    {
        get => _totalWarningCount;
        set => SetProperty(ref _totalWarningCount, value);
    }

    /// <summary>mods 目录指纹：文件数 + 最新写入时间。变了才重算冲突。</summary>
    private static string Fingerprint(string modsDirectory)
    {
        try
        {
            if (!Directory.Exists(modsDirectory)) return "empty";

            var count = 0;
            var newest = 0L;

            foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar"))
            {
                count++;
                var ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (ticks > newest) newest = ticks;
            }

            return $"{count} 个 jar / 最新 {new DateTime(newest, DateTimeKind.Utc).ToLocalTime():MM-dd HH:mm}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>把冲突里点名的一个文件禁用（改名 .disabled），失败时回写状态。</summary>
    public async Task DisableConflictFileAsync(string fileName)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var path = Path.Combine(instance.GameDirectory, "mods", fileName);
        try
        {
            if (!File.Exists(path))
            {
                Status = $"文件不存在：{fileName}";
                return;
            }

            File.Move(path, path + ".disabled");
            Status = $"已禁用 {fileName}（重命名为 .disabled，可随时改回来）";
            await LoadModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "禁用失败: " + ex.Message;
        }
    }

    public void ToggleMod(ModRow row)
    {
        try
        {
            ModScanner.SetEnabled(row.Mod, !row.Mod.Enabled);
            row.Refresh();
            OnPropertyChanged(nameof(EnabledCount));
            Status = $"已{(row.Mod.Enabled ? "启用" : "禁用")} {row.Title}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "无法修改 Mod: " + ex.Message;
        }
    }

    /// <summary>Moves the mod into the launcher's trash directory instead of deleting it.</summary>
    public async Task DeleteModAsync(ModRow row)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var trashRoot = Path.Combine(_main.Context.Paths.Root, "trash", "mods");
        Directory.CreateDirectory(trashRoot);

        try
        {
            var destination = Path.Combine(trashRoot, $"{Path.GetFileNameWithoutExtension(row.FileName)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(row.FileName)}");
            File.Move(row.Mod.FilePath, destination);

            Status = $"已将 {row.Title} 移动到回收目录（{destination}）。";
            await LoadModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "删除失败: " + ex.Message;
        }
    }

    private async Task SearchAsync()
    {
        var instance = SelectedInstance;
        if (string.IsNullOrWhiteSpace(SearchText)) return;

        IsSearching = true;
        Status = "正在搜索 Modrinth…";

        try
        {
            var result = await _main.Context.Modrinth.SearchAsync(new ModrinthSearchQuery
            {
                Query = SearchText.Trim(),
                GameVersion = instance?.GameVersion,
                Loader = instance is null || instance.Loader == LoaderKind.Vanilla ? null : instance.Loader,
                Limit = 20,
            }).ConfigureAwait(true);

            SearchResults.Replace(result.Hits.Select(h => new ModrinthRow(h, this)));
            Status = result.Hits.Count == 0
                ? "没有找到匹配的 Mod。"
                : $"找到 {result.TotalHits:N0} 个结果（显示前 {result.Hits.Count} 个）";
        }
        catch (Exception ex)
        {
            Status = "搜索失败: " + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>搜索 CurseForge 整合包（需要设置里填过 API Key）。</summary>
    private async Task SearchCurseForgeAsync()
    {
        OnPropertyChanged(nameof(HasCurseForgeKey));

        if (!_main.Context.CurseForge.HasKey)
        {
            PackStatus = "还没有 CurseForge API Key：设置 → 网络与下载 → CurseForge 里填一个。";
            return;
        }

        IsPackSearching = true;
        PackStatus = "正在搜索 CurseForge 整合包…";

        try
        {
            var instance = SelectedInstance ?? _main.CurrentInstance;
            var packs = await _main.Context.CurseForge.SearchModpacksAsync(
                CurseForgeSearchText, instance?.GameVersion).ConfigureAwait(true);

            CurseForgePacks.Replace(packs.Select(p => new CurseForgePackRow(p, this)));
            PackStatus = packs.Count == 0
                ? "没有找到匹配的整合包。"
                : $"找到 {packs.Count} 个整合包（按热度排序）";
        }
        catch (Exception ex)
        {
            PackStatus = "搜索失败: " + ex.Message;
        }
        finally
        {
            IsPackSearching = false;
        }
    }

    /// <summary>扫描当前实例的 mods，把缺的必需前置用 CurseForge 自动补上。</summary>
    public async Task RepairDependenciesAsync()
    {
        var instance = SelectedInstance ?? _main.CurrentInstance;
        if (instance is null)
        {
            Status = "请先选择一个版本。";
            return;
        }

        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        var scan = Orion.Core.Mods.ModDependencyScanner.Scan(modsDirectory);

        if (scan.Missing.Count == 0)
        {
            Status = $"检查完成：{scan.ModCount} 个模组，未发现缺失前置。";
            return;
        }

        IsBusy = true;
        var activity = _main.Activities.Begin($"补齐前置（{instance.Name}）", $"{scan.Missing.Count} 个缺失");
        Status = $"发现 {scan.Missing.Count} 个缺失前置，正在自动补齐…";

        try
        {
            var result = await _main.Context.DependencyRepair.RepairAsync(
                instance.GameDirectory,
                scan.Missing,
                instance.GameVersion,
                instance.Loader == LoaderKind.Vanilla ? null : Orion.Core.Mods.CurseForgeClient.LoaderTypeOf(instance.Loader),
                Path.Combine(instance.GameDirectory, ".orap-cache"),
                new Progress<string>(line => Status = line)).ConfigureAwait(true);

            Status = result.Remaining.Count == 0
                ? $"已补齐 {result.Installed.Count} 个前置，依赖全部满足。"
                : $"补齐 {result.Installed.Count} 个，仍有 {result.Remaining.Count} 个缺失：" +
                  string.Join('、', result.Remaining.Select(r => r.ModId).Take(6));

            _main.Activities.Finish(activity,
                result.Remaining.Count == 0 ? ActivityState.Completed : ActivityState.Failed, Status);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "补齐失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>整合包安装器（Forge/NeoForge）需要的 Java：按 MC 版本挑一个可用运行时。</summary>
    private async Task<Core.Java.JavaRuntime?> PickInstallerJavaAsync(string? minecraftVersion)
    {
        var runtimes = _main.JavaRuntimes.Count > 0
            ? _main.JavaRuntimes.ToList()
            : (await _main.Context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

        var required = CatalogViewModel.RequiredJavaFor(minecraftVersion);
        var java = Core.Java.JavaDetector.SelectFor(required, runtimes);
        if (java is not null) return java;

        var detected = string.Join("、", runtimes.Select(r => "Java " + r.MajorVersion).Distinct());
        PackStatus = $"安装这个加载器需要 Java {required}，但只检测到 {(detected.Length == 0 ? "无" : detected)}。"
                     + "请在「设置 → Java」指定路径后重试。";
        return null;
    }

    /// <summary>下载整合包 zip 并交给整合包安装器（新建实例、装加载器、按清单补模组）。</summary>
    public async Task InstallCurseForgePackAsync(CurseForgePackRow row)
    {
        IsBusy = true;
        var activity = _main.Activities.Begin($"下载整合包 {row.Title}", "CurseForge");
        PackStatus = $"正在解析 {row.Title} 的可用文件…";

        try
        {
            var instance = SelectedInstance ?? _main.CurrentInstance;
            LoaderKind? loader = instance is null || instance.Loader == LoaderKind.Vanilla ? null : instance.Loader;
            var loaderType = loader is null ? (int?)null : Orion.Core.Mods.CurseForgeClient.LoaderTypeOf(loader.Value);

            var file = await _main.Context.CurseForge.GetBestFileAsync(row.Pack, instance?.GameVersion, loaderType)
                .ConfigureAwait(true);

            if (file is null)
            {
                PackStatus = "这个整合包没有可下载的文件（Key 可能已失效）。";
                _main.Activities.Finish(activity, ActivityState.Failed, "没有可用文件");
                return;
            }

            PackStatus = $"正在下载 {file.FileName}…";
            var cache = Path.Combine(_main.Context.Paths.Root, "cache", "curseforge");
            var progress = new Progress<long>(percent => PackStatus = $"正在下载 {file.FileName}… {percent}%");
            var zip = await _main.Context.CurseForge.DownloadPackAsync(row.Pack, file, cache, progress)
                .ConfigureAwait(true);

            // Forge/NeoForge 安装器需要对应版本的 Java（缺了就会报"需要 Java 运行时来运行安装器"）
            var info = Orion.Core.Mods.PackArchiveInstaller.Inspect(zip);
            var installerJava = await PickInstallerJavaAsync(info.MinecraftVersion).ConfigureAwait(true);
            if (installerJava is null && info.Loader != LoaderKind.Vanilla)
            {
                PackStatus = "加载器安装需要对应版本的 Java，请在「设置 → Java」指定后重试。";
                _main.Activities.Finish(activity, ActivityState.Failed, PackStatus);
                return;
            }

            PackStatus = "下载完成，开始安装（加载器 + 清单模组）…";
            var result = await _main.Context.PackArchives
                .InstallCurseForgeAsync(zip, row.Title, installerJava,
                    new Progress<string>(line =>
                    {
                        PackStatus = line;
                        var fraction = CatalogViewModel.PackPhaseProgressFor(line);
                        if (fraction >= 0) _main.Activities.Report(activity, 0.2 + fraction * 0.8, line, "");
                    }))
                .ConfigureAwait(true);

            if (result.Success)
            {
                PackStatus = $"已安装 {row.Title}" + (result.Warnings.Count > 0
                    ? $"（{result.Warnings.Count} 条提示）"
                    : "");
                _main.Activities.Finish(activity, ActivityState.Completed);
                _main.LogsPage.AppendSystem(PackStatus);
                await _main.ReloadInstancesAsync().ConfigureAwait(true);
            }
            else
            {
                PackStatus = "安装失败: " + (result.Error ?? "未知错误");
                _main.Activities.Finish(activity, ActivityState.Failed, result.Error);
            }
        }
        catch (Exception ex)
        {
            PackStatus = "安装失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallFromModrinthAsync(ModrinthRow row)
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            Status = "请先选择一个版本。";
            return;
        }

        IsBusy = true;
        var activity = _main.Activities.Begin($"安装 Mod {row.Title}", instance.Name);
        Status = $"正在获取 {row.Title} 的版本…";

        try
        {
            var versions = await _main.Context.Modrinth.GetVersionsAsync(
                row.Project.Identifier,
                instance.GameVersion,
                instance.Loader == LoaderKind.Vanilla ? null : instance.Loader).ConfigureAwait(true);

            var version = versions.FirstOrDefault(v => v.VersionType == "release") ?? versions.FirstOrDefault();
            if (version is null)
            {
                Status = $"没有适用于 MC {instance.GameVersion} / {instance.DisplayLoader} 的版本。";
                _main.Activities.Finish(activity, ActivityState.Failed, "无兼容版本");
                return;
            }

            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            var path = await _main.Context.Modrinth.InstallAsync(version, modsDirectory).ConfigureAwait(true);

            // 记进内容清单，之后"检查更新"才能按版本比对（而不是靠文件名猜）。
            try
            {
                var manifest = LoadManifest(instance);
                manifest.Upsert(new ContentEntry
                {
                    ProjectId = version.ProjectId.Length > 0 ? version.ProjectId : row.Project.ProjectId,
                    VersionId = version.Id,
                    FileName = Path.GetFileName(path),
                    Sha1 = version.PrimaryFile?.Sha1,
                    Title = row.Title,
                    VersionNumber = version.VersionNumber,
                    Source = "modrinth",
                    UpdatedAt = DateTimeOffset.Now,
                });
                await manifest.SaveAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _main.LogsPage.AppendSystem("内容清单写入失败: " + ex.Message);
            }

            Status = $"已安装 {row.Title} {version.VersionNumber} → {Path.GetFileName(path)}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);

            await LoadModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = "安装失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenModsFolder()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var directory = Path.Combine(instance.GameDirectory, "mods");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void OpenModrinthPage()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        Process.Start(new ProcessStartInfo($"https://modrinth.com/mods?q={Uri.EscapeDataString(SearchText)}") { UseShellExecute = true });
    }
    /// <summary>内容清单文件：instances/<id>/content.json。</summary>
    private ContentManifest LoadManifest(Instance instance)
        => ContentManifest.Load(_main.Context.Paths.InstanceDir(instance.Id));

    /// <summary>更新预览的一行：可勾选，默认全选。</summary>
    public sealed class UpdateRow(ModUpdateCandidate candidate) : ObservableObject
    {
        private bool _isSelected = true;

        public ModUpdateCandidate Candidate { get; } = candidate;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Title => Candidate.Title;

        public string VersionLine => $"{Candidate.CurrentVersionNumber} → {Candidate.LatestVersionNumber}";

        public string FileLine => $"{Candidate.CurrentFileName} → {Candidate.LatestFileName}";
    }

    /// <summary>检查更新：只查询，把结果填进页内面板，等用户勾选并确认后才下载。</summary>
    private async Task CheckUpdatesAsync()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            Status = "请先选择一个版本。";
            return;
        }

        var activity = _main.Activities.Begin("检查模组更新", InstanceLabel);
        Status = "正在检查更新…";
        Updates.Clear();
        OnPropertyChanged(nameof(HasUpdates));

        try
        {
            var manifest = LoadManifest(instance);
            var checker = new ModUpdateChecker(new ModrinthVersionSource(_main.Context.Modrinth));
            var progress = new Progress<string>(message => _main.Activities.Report(activity, 0, message, ""));

            var candidates = await checker.CheckAsync(
                manifest,
                instance.GameVersion,
                instance.DisplayLoader,
                progress).ConfigureAwait(true);

            Updates.Replace(candidates.Select(c => new UpdateRow(c)));
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(SelectedUpdateCount));
            _main.Activities.Finish(activity, ActivityState.Completed);

            Status = candidates.Count == 0
                ? "已是最新版本，没有可更新的内容。"
                : $"发现 {candidates.Count} 个可更新项：勾选后点「更新所选」才会开始下载。";
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            _main.Activities.Finish(activity, ActivityState.Failed);
            Status = "检查更新失败: " + ex.Message;
            _main.LogsPage.AppendSystem(Status);
        }
    }

    /// <summary>仅用于演示/截图：填充两条不会下载的假候选。</summary>
    public void SeedDemoUpdates()
    {
        ModUpdateCandidate Fake(string title, string from, string to, string file)
        {
            var version = new ModrinthVersion
            {
                Id = "demo-" + to,
                ProjectId = "demo-" + title,
                VersionNumber = to,
                Files = [new ModrinthFile { Filename = file, Url = "https://example.invalid/" + file, Primary = true }],
            };

            return new ModUpdateCandidate("demo-" + title, title, "demo", from, file.Replace(to, from), "demo-" + to, to, file, null, null, [], version);
        }

        Updates.Replace(
        [
            new UpdateRow(Fake("Sodium", "0.5.11", "0.6.0", "sodium-0.6.0.jar")),
            new UpdateRow(Fake("FerriteCore", "6.0.1", "6.0.2", "ferritecore-6.0.2.jar")),
        ]);

        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(SelectedUpdateCount));
        Status = "（演示）发现 2 个可更新项：勾选后点「更新所选」才会开始下载。";
    }

    public bool HasUpdates => Updates.Count > 0;

    public int SelectedUpdateCount => Updates.Count(u => u.IsSelected);

    public ICommand SelectAllUpdatesCommand { get; private set; } = null!;
    public ICommand UpdateSelectedCommand { get; private set; } = null!;
    public ICommand DismissUpdatesCommand { get; private set; } = null!;

    private void InitUpdateCommands()
    {
        SelectAllUpdatesCommand = new RelayCommand(_ =>
        {
            var select = SelectedUpdateCount < Updates.Count;
            foreach (var row in Updates) row.IsSelected = select;
            OnPropertyChanged(nameof(SelectedUpdateCount));
        });

        DismissUpdatesCommand = new RelayCommand(_ =>
        {
            Updates.Clear();
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(SelectedUpdateCount));
            Status = "已收起更新列表，未下载任何文件。";
        });

        UpdateSelectedCommand = new AsyncRelayCommand(ApplySelectedUpdatesAsync, () => SelectedUpdateCount > 0);
    }

    /// <summary>只在用户确认后调用：备份旧文件 → 下载 → 删旧文件 → 更新清单。</summary>
    private async Task ApplySelectedUpdatesAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var rows = Updates.Where(u => u.IsSelected).Select(u => u.Candidate).ToList();
        if (rows.Count == 0)
        {
            Status = "没有勾选任何项目。";
            return;
        }

        var manifest = LoadManifest(instance);
        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);


        var activity = _main.Activities.Begin("更新模组", $"{rows.Count} 项");
        var done = 0;

        foreach (var candidate in rows)
        {
            try
            {
                var oldPath = Path.Combine(modsDirectory, candidate.CurrentFileName);

                var newPath = await _main.Context.Modrinth.InstallAsync(candidate.Latest, modsDirectory).ConfigureAwait(true);
                if (File.Exists(oldPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(oldPath);
                }

                manifest.Upsert(new ContentEntry
                {
                    ProjectId = candidate.ProjectId,
                    VersionId = candidate.LatestVersionId,
                    FileName = Path.GetFileName(newPath),
                    Sha1 = candidate.Sha1,
                    Title = candidate.Title,
                    VersionNumber = candidate.LatestVersionNumber,
                    Source = "modrinth",
                    UpdatedAt = DateTimeOffset.Now,
                });

                done++;
                _main.Activities.Report(activity, done * 100.0 / rows.Count, $"已更新 {candidate.Title}", "");
            }
            catch (Exception ex)
            {
                _main.LogsPage.AppendSystem($"更新 {candidate.Title} 失败: {ex.Message}");
            }
        }

        await manifest.SaveAsync().ConfigureAwait(true);
        _main.Activities.Finish(activity, done == rows.Count ? ActivityState.Completed : ActivityState.Failed);

        Status = $"已更新 {done}/{rows.Count} 个模组（旧文件不再单独备份，避免占用空间）";
        _main.LogsPage.AppendSystem(Status);

        Updates.Clear();
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(SelectedUpdateCount));
        await LoadModsAsync().ConfigureAwait(true);
    }
}
