using System.IO;
using System.Windows.Input;
using Orion.Core.Instances;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orion.App.Controls;
using Orion.App.Mvvm;
using Orion.Core.Game;
using Orion.Core.World;

namespace Orion.App.ViewModels;

/// <summary>工具箱：种子地图、查看截图、游戏默认设置。</summary>
public sealed class ToolboxViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _tabIndex;
    private string _seed = "";
    private ScreenshotRow? _selectedScreenshot;
    private string _status = "";

    private bool _defaultsEnabled = true;
    private int _renderDistance = 12;
    private int _guiScale;
    private bool _autoJump;
    private int _fov = 70;
    private double _gamma = 0.5;
    private string _particles = "all";
    private bool _bobView = true;

    public ToolboxViewModel(MainViewModel main)
    {
        _main = main;

        GenerateMapCommand = new RelayCommand(GenerateMap);
        RefreshRecordsCommand = new RelayCommand(() => _ = RefreshRecordsAsync());
        ClearRecordsCommand = new RelayCommand(ClearRecords);
        LocateCommand = new RelayCommand(LocateTo);
        ScanStructuresCommand = new RelayCommand(ScanNearbyStructures);
        FindBiomeCommand = new RelayCommand(FindBiome);
        FindStructureCommand = new RelayCommand(FindStructure);
        LocateStructureTarget = StructureFilterOptions.FirstOrDefault();
        LoadBiomeOptions();
        LocateStructureCommand = new RelayCommand(param =>
        {
            if (param is not NearbyStructure row) return;
            LocateX = row.X.ToString();
            LocateZ = row.Z.ToString();
            OnPropertyChanged(nameof(LocateX));
            OnPropertyChanged(nameof(LocateZ));
            LocateRequested?.Invoke(row.X, row.Z);
            MapStatus = $"已定位到 {row.Name} · {row.X}, {row.Z}（{row.DistanceText}）";
        });
        ToggleAllStructuresCommand = new RelayCommand(() =>
        {
            var enable = !StructureFilterOptions.All(o => o.IsEnabled);
            foreach (var option in StructureFilterOptions) option.IsEnabled = enable;
            ChangeStructureFilter();
        });
        ToNetherCommand = new RelayCommand(() => ConvertCoordinates(toNether: true));
        ToOverworldCommand = new RelayCommand(() => ConvertCoordinates(toNether: false));
        foreach (var option in StructureFilterOptions) option.Changed += _ => ChangeStructureFilter();
        LocateSpawnCommand = new RelayCommand(LocateSpawn);
        OpenChunkbaseCommand = new RelayCommand(OpenChunkbase);
        OpenMislandCommand = new RelayCommand(OpenMisland);
        CopySeedCommand = new RelayCommand(CopySeed);
        RefreshScreenshotsCommand = new RelayCommand(() => RefreshScreenshots());
        OpenScreenshotsFolderCommand = new RelayCommand(OpenScreenshotsFolder);
        RevealScreenshotCommand = new RelayCommand(RevealScreenshot);
        SaveDefaultsCommand = new RelayCommand(SaveDefaults);
        ApplyDefaultsCommand = new RelayCommand(ApplyDefaultsToEveryInstance);

        LoadFromSettings();
    }

    // ── 页签 ────────────────────────────────────────────────
    public IReadOnlyList<string> Tabs { get; } = ["种子地图", "查看截图", "游戏默认设置", "记录"];

    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, Math.Clamp(value, 0, Tabs.Count - 1))) return;
            OnPropertyChanged(nameof(IsSeedTab));
            OnPropertyChanged(nameof(IsScreenshotsTab));
            OnPropertyChanged(nameof(IsDefaultsTab));
            OnPropertyChanged(nameof(IsRecordsTab));

            if (IsScreenshotsTab) RefreshScreenshots();
            if (IsRecordsTab) _ = RefreshRecordsAsync();
        }
    }

    public bool IsSeedTab => _tabIndex == 0;
    public bool IsScreenshotsTab => _tabIndex == 1;
    public bool IsDefaultsTab => _tabIndex == 2;
    public bool IsRecordsTab => _tabIndex == 3;

    public string InstanceLabel => _main.CurrentInstance is { } instance
        ? $"{instance.Name} · MC {instance.GameVersion}"
        : "未选择版本";

    // ── 记录（各版本启动次数 / 游玩时长）──────────────────────
    public ObservableList<PlayRecordRow> Records { get; } = [];

    private string _recordsSummary = "";
    private bool _recordsLoaded;

    public string RecordsSummary
    {
        get => _recordsSummary;
        private set => SetProperty(ref _recordsSummary, value);
    }

    public bool HasRecords => Records.Count > 0;

    public string RecordsEmptyHint =>
        Records.Count > 0 ? "" : "还没有记录。启动任意版本后，这里会显示启动次数与游玩时长。";


    /// <summary>重新读一遍统计文件并刷新列表（启动/退出后也会被调用）。</summary>
    public Task RefreshRecordsAsync()
    {
        try
        {
            var stats = _main.Context.PlayStats.All();
            var byId = stats.ToDictionary(s => s.InstanceId, s => s, StringComparer.OrdinalIgnoreCase);

            var rows = new List<PlayRecordRow>();

            // 先按实例列表排（能显示没玩过的版本），再补上已删除实例的历史
            foreach (var instance in _main.Instances)
            {
                byId.TryGetValue(instance.Id, out var stat);
                rows.Add(PlayRecordRow.From(instance, stat));
            }

            foreach (var stat in stats)
            {
                if (_main.Instances.Any(i => string.Equals(i.Id, stat.InstanceId, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(PlayRecordRow.From(null, stat));
            }

            rows = [.. rows.OrderByDescending(r => r.LastPlayed ?? DateTime.MinValue)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

            Records.Replace(rows);
            _recordsLoaded = true;

            var total = TimeSpan.FromSeconds(stats.Sum(s => s.TotalSeconds));
            var launches = stats.Sum(s => s.Launches);
            RecordsSummary = stats.Count == 0
                ? "暂无记录"
                : $"共 {launches} 次启动 · 累计 {InstancePlayStats.FormatDuration(total)} · {stats.Count} 个版本有记录";

            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(RecordsEmptyHint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordsSummary = "读取记录失败：" + ex.Message;
        }

        return Task.CompletedTask;
    }

        // ── 种子地图 ────────────────────────────────────────────
    public string Seed
    {
        get => _seed;
        set
        {
            if (!SetProperty(ref _seed, value ?? "")) return;
            OnPropertyChanged(nameof(SeedHint));
        }
    }

    /// <summary>种子可以为数字，也可以是任意文本（游戏会把它折算成数字）。</summary>
    public string SeedHint => string.IsNullOrWhiteSpace(Seed)
        ? "填一个种子，或留空直接打开地图页自己填。"
        : $"将用种子「{Seed.Trim()}」打开在线地图。";

    public string SeedVersion => _main.CurrentInstance?.GameVersion ?? "1.21";

    // 离线地图：种子解析结果 + 悬停状态
    private long _seedValue;
    private string _mapStatus = "拖动平移，滚轮缩放；绿色格子 = 史莱姆区块。";
    private string _seedError = "";

    public long SeedValue => _seedValue;

    public string SeedError
    {
        get => _seedError;
        private set => SetProperty(ref _seedError, value);
    }

    public string MapStatus
    {
        get => _mapStatus;
        set => SetProperty(ref _mapStatus, value);
    }

    private string _zoomHint = "";

    /// <summary>当前缩放档位（由视图调用 SetZoomHint 更新）。</summary>
    public string ZoomHint
    {
        get => _zoomHint;
        private set => SetProperty(ref _zoomHint, value);
    }

    /// <summary>视图在缩放后回填档位文字（含"已到上限/下限"）。</summary>
    public void SetZoomHint(string text) => ZoomHint = text;

    public string Legend => "彩色：生物群系（简化模型）· 绿色方格：史莱姆区块（精确）";

    private bool _showSlimeOverlay = true;
    private bool _showGridOverlay = true;

    public bool ShowSlimeOverlay
    {
        get => _showSlimeOverlay;
        set => SetProperty(ref _showSlimeOverlay, value);
    }

    public bool ShowGridOverlay
    {
        get => _showGridOverlay;
        set => SetProperty(ref _showGridOverlay, value);
    }

    private bool _showTextures = true;

    /// <summary>是否给群系图加贴图质感（关掉是纯色块，渲染更快）。</summary>
    public bool ShowTextures
    {
        get => _showTextures;
        set
        {
            if (!SetProperty(ref _showTextures, value)) return;
            TextureChanged?.Invoke(value);
        }
    }

    public event Action<bool>? TextureChanged;

    /// <summary>当前用的是哪个引擎（cubiomes 原生 / 简化模型）。</summary>
    public string MapEngineLabel => CubiomesNative.Available
        ? $"群系引擎：cubiomes（支持 {CubiomesNative.Versions[0]} 及更早）"
        : "群系引擎：简化模型（未加载 OrapCubiomes.dll）";

    public string MapModelNotice =>
        CubiomesNative.Available
            ? "群系由 cubiomes 按原版算法生成（与 Chunkbase / Amidst 同源）；史莱姆区块同样是精确值。"
            : "群系由 5 通道噪声近似生成（可复现，但与游戏内实际地形不保证一致）；史莱姆区块是精确值。";


    /// <summary>维度：0 主世界 / 1 下界 / 2 末地。</summary>
    public IReadOnlyList<string> Dimensions { get; } = ["主世界", "下界", "末地"];

    private int _dimensionIndex;

    public int DimensionIndex
    {
        get => _dimensionIndex;
        set
        {
            if (!SetProperty(ref _dimensionIndex, Math.Clamp(value, 0, 2))) return;
            OnPropertyChanged(nameof(DimensionHint));
            DimensionChanged?.Invoke(_dimensionIndex);
        }
    }

    public string DimensionHint => _dimensionIndex switch
    {
        1 => "下界：坐标与主世界 1:8，要塞/村庄等主世界结构不会显示。",
        2 => "末地：显示末地城与末地折跃门。",
        _ => "主世界：村庄、神殿、要塞等都在这一层。",
    };

    public event Action<int>? DimensionChanged;

    private bool _showStructures = true;

    /// <summary>结构筛选：只勾选的类型会画在地图上（空 = 全部）。</summary>
    public ObservableList<StructureFilterOption> StructureFilterOptions { get; } =
    [
        // 主世界
        new("village", "村庄"),
        new("outpost", "掠夺者前哨站"),
        new("monument", "海底神殿"),
        new("mansion", "林地府邸"),
        new("desert_pyramid", "沙漠神殿"),
        new("jungle_temple", "丛林神庙"),
        new("swamp_hut", "女巫小屋"),
        new("igloo", "雪屋"),
        new("ocean_ruin", "海底废墟"),
        new("shipwreck", "沉船"),
        new("treasure", "埋藏的宝藏"),
        new("ruined_portal", "废弃传送门"),
        new("ancient_city", "远古城市"),
        new("trail_ruins", "古迹废墟"),
        new("trial_chambers", "试炼密室"),
        new("stronghold", "要塞"),
        // 下界
        new("fortress", "下界要塞"),
        new("bastion", "堡垒遗迹"),
        new("ruined_portal_nether", "下界废弃传送门"),
        // 末地
        new("end_city", "末地城"),
        new("end_gateway", "末地折跃门"),
    ];

    public ICommand ToggleAllStructuresCommand { get; }

    public void ChangeStructureFilter() => StructureFilterChanged?.Invoke(AllowedStructureKeys());

    public event Action<IEnumerable<string>?>? StructureFilterChanged;

    private IEnumerable<string>? AllowedStructureKeys()
    {
        var enabled = StructureFilterOptions.Where(o => o.IsEnabled).ToList();
        if (enabled.Count == StructureFilterOptions.Count) return null;   // 全选 = 不过滤
        if (enabled.Count == 0) return ["__none__"];                      // 全不选 = 什么都不画
        return enabled.SelectMany(o => o.Key).ToList();
    }

    public bool ShowStructures
    {
        get => _showStructures;
        set => SetProperty(ref _showStructures, value);
    }

    public ICommand LocateSpawnCommand { get; }


    /// <summary>主世界 ↔ 下界坐标换算（1:8）。</summary>
    public ICommand ToNetherCommand { get; }

    public ICommand ToOverworldCommand { get; }

    /// <summary>出生点定位请求（画布由 View 驱动）。</summary>
    public event Action? SpawnRequested;

    public string SpawnText { get; private set; } = "";

    /// <summary>坐标定位：X / Z 输入框 + 事件（画布由 View 来驱动）。</summary>
    public string LocateX { get; set; } = "0";

    /// <summary>外部（点地图上的结构）改了坐标后刷新输入框显示。</summary>
    public void NotifyLocateInputsChanged()
    {
        OnPropertyChanged(nameof(LocateX));
        OnPropertyChanged(nameof(LocateZ));
    }

    public string LocateZ { get; set; } = "0";

    public event Action<int, int>? LocateRequested;

    public ICommand LocateCommand { get; }

    public ICommand GenerateMapCommand { get; }

    public ICommand OpenChunkbaseCommand { get; }
    public ICommand OpenMislandCommand { get; }
    public ICommand CopySeedCommand { get; }

    public ICommand RefreshRecordsCommand { get; }
    public ICommand ClearRecordsCommand { get; }

    private void ClearRecords()
    {
        var answer = Dialogs.Ask(
            "清空游玩记录",
            "会删除所有版本的启动次数与游玩时长统计（playstats.json）。",
            yesText: "清空",
            noText: "取消",
            detail: "这个操作不可撤销；版本本身和存档不受影响。",
            danger: true);

        if (answer != DialogOutcome.Yes) return;

        _main.Context.PlayStats.Clear();
        _ = RefreshRecordsAsync();
        MapStatus = "已清空游玩记录。";
    }

    // ── 截图 ────────────────────────────────────────────────
    public ObservableList<ScreenshotRow> Screenshots { get; } = [];
    public bool HasScreenshots => Screenshots.Count > 0;
    public string ScreenshotsHint => HasScreenshots ? "" : "这个版本还没有截图（游戏里按 F2 截图会存到 screenshots 目录）。";

    public ScreenshotRow? SelectedScreenshot
    {
        get => _selectedScreenshot;
        set => SetProperty(ref _selectedScreenshot, value);
    }

    public ICommand RefreshScreenshotsCommand { get; }
    public ICommand OpenScreenshotsFolderCommand { get; }
    public ICommand RevealScreenshotCommand { get; }

    // ── 游戏默认设置 ────────────────────────────────────────
    public bool DefaultsEnabled
    {
        get => _defaultsEnabled;
        set { if (SetProperty(ref _defaultsEnabled, value)) OnPropertyChanged(nameof(DefaultsSummary)); }
    }

    public int RenderDistance
    {
        get => _renderDistance;
        set { if (SetProperty(ref _renderDistance, value)) OnPropertyChanged(nameof(DefaultsSummary)); }
    }

    public int GuiScale
    {
        get => _guiScale;
        set { if (SetProperty(ref _guiScale, value)) OnPropertyChanged(nameof(DefaultsSummary)); }
    }

    public bool AutoJump
    {
        get => _autoJump;
        set { if (SetProperty(ref _autoJump, value)) OnPropertyChanged(nameof(DefaultsSummary)); }
    }

    public int Fov
    {
        get => _fov;
        set { if (SetProperty(ref _fov, value)) OnPropertyChanged(nameof(DefaultsSummary)); }
    }

    public double Gamma
    {
        get => _gamma;
        set => SetProperty(ref _gamma, value);
    }

    public string Particles
    {
        get => _particles;
        set => SetProperty(ref _particles, value);
    }

    public bool BobView
    {
        get => _bobView;
        set => SetProperty(ref _bobView, value);
    }

    public IReadOnlyList<int> GuiScaleOptions { get; } = [0, 1, 2, 3, 4];
    public IReadOnlyList<string> ParticlesOptions { get; } = ["all", "decreased", "minimal"];

    /// <summary>游戏语言：值写进 options.txt 的 lang 键。</summary>
    public IReadOnlyList<GameLanguage> LangOptions { get; } =
    [
        new("zh_cn", "简体中文"),
        new("zh_tw", "繁體中文"),
        new("en_us", "English (US)"),
        new("ja_jp", "日本語"),
        new("ko_kr", "한국어"),
        new("ru_ru", "Русский"),
        new("de_de", "Deutsch"),
        new("fr_fr", "Français"),
        new("es_es", "Español"),
        new("pt_br", "Português (BR)"),
    ];

    private string _lang = "zh_cn";

    public string Lang
    {
        get => _lang;
        set
        {
            if (!SetProperty(ref _lang, value)) return;
            OnPropertyChanged(nameof(DefaultsSummary));
            OnPropertyChanged(nameof(SelectedLanguage));
        }
    }

    /// <summary>下拉框用（ComboBox 绑定对象，显示中文名）。</summary>
    public GameLanguage? SelectedLanguage
    {
        get => LangOptions.FirstOrDefault(o => o.Code == _lang) ?? LangOptions[0];
        set
        {
            if (value is null) return;
            Lang = value.Code;
        }
    }

    public string DefaultsSummary => $"视距 {RenderDistance} · 界面尺寸 {(GuiScale == 0 ? "自动" : GuiScale.ToString())} · "
        + $"自动跳跃 {(AutoJump ? "开" : "关")} · FOV {Fov} · 语言 {(SelectedLanguage?.Name ?? Lang)}";

    public ICommand SaveDefaultsCommand { get; }
    public ICommand ApplyDefaultsCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ── 实现 ────────────────────────────────────────────────
    public void LoadFromSettings()
    {
        var template = _main.Context.Settings.DefaultGameOptions;
        _defaultsEnabled = template.Enabled;
        _renderDistance = template.RenderDistance;
        _guiScale = template.GuiScale;
        _autoJump = template.AutoJump;
        _fov = template.Fov;
        _gamma = template.Gamma;
        _particles = template.Particles;
        _bobView = template.BobView;
        _lang = template.Lang;

        GameOptionsTemplate.Default = template;

        OnPropertyChanged(nameof(DefaultsEnabled));
        OnPropertyChanged(nameof(RenderDistance));
        OnPropertyChanged(nameof(GuiScale));
        OnPropertyChanged(nameof(AutoJump));
        OnPropertyChanged(nameof(Fov));
        OnPropertyChanged(nameof(Gamma));
        OnPropertyChanged(nameof(Particles));
        OnPropertyChanged(nameof(BobView));
        OnPropertyChanged(nameof(Lang));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(DefaultsSummary));
    }

    /// <summary>页面切入时刷新（当前版本可能与上次不同）。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(InstanceLabel));
        OnPropertyChanged(nameof(SeedVersion));
        if (IsScreenshotsTab) RefreshScreenshots();
    }

    private GameOptionsTemplate BuildTemplate() => new()
    {
        Enabled = DefaultsEnabled,
        RenderDistance = RenderDistance,
        GuiScale = GuiScale,
        AutoJump = AutoJump,
        Fov = Fov,
        Gamma = Gamma,
        Particles = Particles,
        BobView = BobView,
        Lang = Lang,
    };

    private async void SaveDefaults()
    {
        var template = BuildTemplate();
        _main.Context.Settings.DefaultGameOptions = template;
        GameOptionsTemplate.Default = template;
        await _main.Context.SettingsStore.SaveAsync(_main.Context.Settings).ConfigureAwait(true);

        Status = "已保存默认设置，之后新建的版本会自动写入。";
        _main.LogsPage.AppendSystem(Status);
    }

    /// <summary>把当前模板写进所有已存在版本的 options.txt（只改这几个键）。</summary>
    private void ApplyDefaultsToEveryInstance()
    {
        var template = BuildTemplate();
        if (!template.Enabled)
        {
            Status = "默认设置当前是关闭状态，请先勾选「在新建版本时写入」。";
            return;
        }

        var applied = 0;
        var failed = 0;
        foreach (var instance in _main.Instances)
        {
            try
            {
                template.Apply(instance.GameDirectory);
                applied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _main.LogsPage.AppendSystem($"写入 {instance.Name} 的 options.txt 失败：{ex.Message}");
            }
        }

        Status = failed == 0
            ? $"已把默认设置写入 {applied} 个版本（只改视距/界面尺寸/自动跳跃等几项，其余设置不动）。"
            : $"已写入 {applied} 个版本，{failed} 个失败（看日志）。";
        _main.LogsPage.AppendSystem(Status);
    }

    /// <summary>解析种子并让画布重画（数字直接使用，文本按 Java hashCode）。</summary>
    private void GenerateMap()
    {
        if (string.IsNullOrWhiteSpace(Seed))
        {
            SeedError = "先填一个种子；留空不会画地图。";
            return;
        }

        if (!SeedMap.TryParseSeed(Seed, out var value))
        {
            SeedError = "这个种子解析不了，换一个试试。";
            return;
        }

        SeedError = "";
        _seedValue = value;
        OnPropertyChanged(nameof(SeedValue));
        MapStatus = $"种子 = {value} · 拖动平移，滚轮缩放，悬停看群系";
        _main.LogsPage.AppendSystem($"工具箱：已生成离线种子地图 {value}");
    }

    // ── 定位结构（选类型 → 找最近的一个）──────────────────────
    public ObservableList<StructureFilterOption> LocateStructureOptions => StructureFilterOptions;

    private StructureFilterOption? _locateStructureTarget;

    public StructureFilterOption? LocateStructureTarget
    {
        get => _locateStructureTarget;
        set => SetProperty(ref _locateStructureTarget, value);
    }

    private string _structureLocateStatus = "选一个结构类型，从当前位置向外找最近的一个。";

    public string StructureLocateStatus
    {
        get => _structureLocateStatus;
        private set => SetProperty(ref _structureLocateStatus, value);
    }

    public ICommand FindStructureCommand { get; }

    /// <summary>
    /// 由内向外按 512 格的环找最近的结构实例（同类型只取一个，就近跳过去）。
    /// </summary>
    private async void FindStructure()
    {
        if (!CubiomesNative.Available)
        {
            StructureLocateStatus = "原生引擎未加载，算不出结构位置。";
            return;
        }

        if (LocateStructureTarget is not { } target || string.IsNullOrWhiteSpace(target.Key.FirstOrDefault()))
        {
            StructureLocateStatus = "先在左边选一个结构类型。";
            return;
        }

        if (!SeedMap.TryParseSeed(Seed, out var seed))
        {
            StructureLocateStatus = "先填一个种子。";
            return;
        }

        var key = target.Key.FirstOrDefault() ?? "";
        var (centerX, centerZ) = MapCenterProvider?.Invoke() ?? (0, 0);
        var version = CubiomesNative.VersionIndexFor(SeedVersion);
        var dimension = _dimensionIndex;

        StructureLocateStatus = $"正在寻找最近的「{target.Label}」…";

        var found = await Task.Run(() =>
        {
            for (var radiusChunks = 32; radiusChunks <= 1024; radiusChunks *= 2)
            {
                var minChunkX = (centerX - radiusChunks * 16) >> 4;
                var maxChunkX = (centerX + radiusChunks * 16) >> 4;
                var minChunkZ = (centerZ - radiusChunks * 16) >> 4;
                var maxChunkZ = (centerZ + radiusChunks * 16) >> 4;

                if (dimension == CubiomesNative.DimEnd && key == "end_ship")
                {
                    var ships = CubiomesNative.FindEndShips(seed, version, minChunkX, minChunkZ, maxChunkX, maxChunkZ);
                    if (ships.Count > 0)
                    {
                        return ships.OrderBy(p => ((double)p.X - centerX) * (p.X - centerX) + ((double)p.Z - centerZ) * (p.Z - centerZ))
                                    .Select(p => ((int X, int Z)?)(p.X, p.Z))
                                    .FirstOrDefault();
                    }

                    continue;
                }

                var hits = CubiomesNative.FindStructures(seed, version, dimension, minChunkX, minChunkZ, maxChunkX, maxChunkZ)
                    .Where(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (hits.Count > 0)
                {
                    var best = hits.OrderBy(h => ((double)h.X - centerX) * (h.X - centerX) + ((double)h.Z - centerZ) * (h.Z - centerZ))
                                   .First();
                    return ((int X, int Z)?)(best.X, best.Z);
                }
            }

            return ((int X, int Z)?)null;
        }).ConfigureAwait(true);

        if (found is { } position)
        {
            LocateX = position.X.ToString();
            LocateZ = position.Z.ToString();
            OnPropertyChanged(nameof(LocateX));
            OnPropertyChanged(nameof(LocateZ));
            LocateRequested?.Invoke(position.X, position.Z);

            StructureLocateStatus = $"最近的「{target.Label}」：{position.X}, {position.Z}";
            MapStatus = StructureLocateStatus;
        }
        else
        {
            StructureLocateStatus = $"附近没找到「{target.Label}」，换维度或换中心再试。";
        }
    }

    // ── 定位生物群系（像 cubiomes-viewer 的 biome finder）──────
    public ObservableList<BiomeOption> BiomeOptions { get; } = [];

    private BiomeOption? _selectedBiome;
    private string _biomeSearchStatus = "选一个群系，从这里向外找一个最近的实例。";

    public BiomeOption? SelectedBiome
    {
        get => _selectedBiome;
        set => SetProperty(ref _selectedBiome, value);
    }

    public string BiomeSearchStatus
    {
        get => _biomeSearchStatus;
        private set => SetProperty(ref _biomeSearchStatus, value);
    }

    public ICommand FindBiomeCommand { get; }

    private void LoadBiomeOptions()
    {
        if (BiomeOptions.Count > 0) return;

        var version = CubiomesNative.VersionIndexFor(SeedVersion);
        var list = new List<BiomeOption>();

        // cubiomes 的群系 id 有固定范围，逐个数比较慢，但只在第一次打开时做一次
        for (var id = 0; id < 200; id++)
        {
            var name = CubiomesNative.BiomeName(version, id);
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new BiomeOption(id, BiomeCatalog.FromCubiomesName(name) ?? name));
        }

        BiomeOptions.Replace(list.OrderBy(b => b.Name, StringComparer.CurrentCulture));
        SelectedBiome = BiomeOptions.FirstOrDefault(b => b.Name.Contains("平原")) ?? BiomeOptions.FirstOrDefault();
    }

    /// <summary>
    /// 从当前中心向外按 64 格步长找最近的该群系（先粗扫再逐步收紧）。
    /// 只读原生查询，放在线程池里跑，避免卡住拖动。
    /// </summary>
    private async void FindBiome()
    {
        if (SelectedBiome is not { } target)
        {
            BiomeSearchStatus = "先在左边选一个群系。";
            return;
        }

        if (!SeedMap.TryParseSeed(Seed, out var seed))
        {
            BiomeSearchStatus = "先填一个种子。";
            return;
        }

        var (centerX, centerZ) = MapCenterProvider?.Invoke() ?? (0, 0);
        var version = CubiomesNative.VersionIndexFor(SeedVersion);
        var dimension = _dimensionIndex;
        var biomeId = target.Id;

        BiomeSearchStatus = $"正在寻找「{target.Name}」…";

        var found = await Task.Run(() =>
        {
            // 由内向外扫描：环半径从 256 到 4096 格，步长 64
            for (var radius = 256; radius <= 4096; radius *= 2)
            {
                var best = (X: 0, Z: 0, Distance: double.MaxValue);

                for (var dz = -radius; dz <= radius; dz += 64)
                {
                    for (var dx = -radius; dx <= radius; dx += 64)
                    {
                        var x = centerX + dx;
                        var z = centerZ + dz;

                        if (CubiomesNative.BiomeAt(seed, version, dimension, 4, x, 64, z) != biomeId) continue;

                        var distance = Math.Sqrt((double)dx * dx + (double)dz * dz);
                        if (distance >= best.Distance) continue;

                        best = (x, z, distance);
                    }
                }

                if (best.Distance < double.MaxValue) return ((int X, int Z)?)(best.X, best.Z);
            }

            return ((int X, int Z)?)null;
        }).ConfigureAwait(true);

        if (found is { } position)
        {
            LocateX = position.X.ToString();
            LocateZ = position.Z.ToString();
            OnPropertyChanged(nameof(LocateX));
            OnPropertyChanged(nameof(LocateZ));
            LocateRequested?.Invoke(position.X, position.Z);

            BiomeSearchStatus = $"找到「{target.Name}」：{position.X}, {position.Z}";
            MapStatus = BiomeSearchStatus;
        }
        else
        {
            BiomeSearchStatus = $"4096 格内没找到「{target.Name}」，换个中心或换维度再试。";
        }
    }

    // ── 附近结构（像 cubiomes-viewer 的结构列表）────────────────
    public ObservableList<NearbyStructure> NearbyStructures { get; } = [];

    private string _structureScanStatus = "点「扫描附近结构」列出以当前视图为中心、2000 格内的结构。";

    public string StructureScanStatus
    {
        get => _structureScanStatus;
        private set => SetProperty(ref _structureScanStatus, value);
    }

    public bool HasNearbyStructures => NearbyStructures.Count > 0;

    public ICommand ScanStructuresCommand { get; }
    public ICommand LocateStructureCommand { get; }

    /// <summary>由视图注入：拿当前地图中心（没有就退回坐标输入框/原点）。</summary>
    public Func<(int X, int Z)>? MapCenterProvider { get; set; }

    private void ScanNearbyStructures()
    {
        if (!CubiomesNative.Available)
        {
            StructureScanStatus = "原生引擎未加载，算不出结构位置。";
            return;
        }

        if (!SeedMap.TryParseSeed(Seed, out var seed))
        {
            StructureScanStatus = "先填一个种子再扫描。";
            return;
        }

        var (centerX, centerZ) = MapCenterProvider?.Invoke()
                                  ?? (int.TryParse(LocateX, out var lx) && int.TryParse(LocateZ, out var lz) ? (lx, lz) : (0, 0));

        const int radius = 2000;
        var version = CubiomesNative.VersionIndexFor(SeedVersion);
        var dimension = _dimensionIndex;

        StructureScanStatus = "正在扫描…";

        try
        {
            var found = new List<NearbyStructure>();

            foreach (var (key, x, z) in CubiomesNative.FindStructures(seed, version, dimension,
                         (centerX - radius) >> 4, (centerZ - radius) >> 4,
                         (centerX + radius) >> 4, (centerZ + radius) >> 4))
            {
                found.Add(NearbyStructure.Create(key, StructureLabels.Resolve(key), x, z, centerX, centerZ));
            }

            if (dimension == CubiomesNative.DimOverworld)
            {
                foreach (var (x, z) in CubiomesNative.FindStrongholds(seed, version))
                {
                    found.Add(NearbyStructure.Create("stronghold", StructureLabels.Resolve("stronghold"), x, z, centerX, centerZ));
                }
            }

            var rows = found
                .Where(r => r.Distance <= radius)
                .DistinctBy(r => (r.Key, r.X, r.Z))
                .OrderBy(r => r.Distance)
                .Take(120)
                .ToList();

            NearbyStructures.Replace(rows);
            OnPropertyChanged(nameof(HasNearbyStructures));

            StructureScanStatus = rows.Count == 0
                ? $"{radius} 格内没有找到结构（换个位置或换维度再试）。"
                : $"以 {centerX}, {centerZ} 为中心 {radius} 格内找到 {rows.Count} 个结构，按距离排序。";

            MapStatus = StructureScanStatus;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            StructureScanStatus = "扫描失败：" + ex.Message;
        }
    }

    /// <summary>把地图跳到输入的方块坐标。</summary>
    private void LocateTo()
    {
        if (!int.TryParse(LocateX?.Trim(), out var x) || !int.TryParse(LocateZ?.Trim(), out var z))
        {
            SeedError = "坐标要填整数方块坐标，例如 1200 和 -800。";
            return;
        }

        SeedError = "";
        LocateRequested?.Invoke(x, z);
        MapStatus = $"已定位到方块 {x}, {z}（拖动平移，滚轮缩放）";
        _main.LogsPage.AppendSystem($"工具箱：种子地图定位到 {x}, {z}");
    }

    /// <summary>定位到出生点（主世界）。</summary>
    private void LocateSpawn()
    {
        if (!CubiomesNative.Available)
        {
            MapStatus = "原生引擎未加载，算不出出生点。";
            return;
        }

        var version = CubiomesNative.VersionIndexFor(SeedVersion);
        var spawn = CubiomesNative.Spawn(SeedValue, version);
        if (spawn is not { } position)
        {
            MapStatus = "出生点计算失败。";
            return;
        }

        SpawnText = $"出生点 {position.X}, {position.Z}";
        OnPropertyChanged(nameof(SpawnText));
        LocateX = position.X.ToString();
        LocateZ = position.Z.ToString();
        OnPropertyChanged(nameof(LocateX));
        OnPropertyChanged(nameof(LocateZ));

        if (_dimensionIndex != 0) DimensionIndex = 0;
        SpawnRequested?.Invoke();
        MapStatus = $"已定位到出生点 {position.X}, {position.Z}";
    }

    /// <summary>把输入框里的坐标按 1:8 换算，并定位过去。</summary>
    private void ConvertCoordinates(bool toNether)
    {
        if (!int.TryParse(LocateX?.Trim(), out var x) || !int.TryParse(LocateZ?.Trim(), out var z))
        {
            SeedError = "坐标要填整数方块坐标。";
            return;
        }

        SeedError = "";
        var (nx, nz) = toNether
            ? MapViewportMath.ToNether(x, z)
            : MapViewportMath.ToOverworld(x, z);

        LocateX = nx.ToString();
        LocateZ = nz.ToString();
        OnPropertyChanged(nameof(LocateX));
        OnPropertyChanged(nameof(LocateZ));

        DimensionIndex = toNether ? 1 : 0;
        LocateRequested?.Invoke(nx, nz);
        MapStatus = toNether
            ? $"主世界 {x}, {z} ↔ 下界 {nx}, {nz}（1:8）"
            : $"下界 {x}, {z} ↔ 主世界 {nx}, {nz}（1:8）";
    }

    private void OpenChunkbase()
    {
        var version = Uri.EscapeDataString(SeedVersion);
        var seed = Uri.EscapeDataString(Seed.Trim());
        Open($"https://www.chunkbase.com/apps/seed-map#seed={seed}&platform=java&gameVersion={version}");
    }

    private void OpenMisland()
    {
        var seed = Uri.EscapeDataString(Seed.Trim());
        Open($"https://www.misland.net/seedmap?seed={seed}");
    }

    private void CopySeed()
    {
        if (string.IsNullOrWhiteSpace(Seed))
        {
            Status = "先把种子填进输入框。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(Seed.Trim());
            Status = "种子已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            Status = "复制失败：" + ex.Message;
        }
    }

    private void RefreshScreenshots()
    {
        var previous = SelectedScreenshot?.FullPath;

        Screenshots.Clear();
        if (_main.CurrentInstance is { } instance)
        {
            var directory = Path.Combine(instance.GameDirectory, "screenshots");
            if (Directory.Exists(directory))
            {
                var files = Directory.EnumerateFiles(directory)
                    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(120);

                foreach (var file in files)
                {
                    Screenshots.Add(new ScreenshotRow(file));
                }
            }
        }

        SelectedScreenshot = Screenshots.FirstOrDefault(s => s.FullPath == previous) ?? Screenshots.FirstOrDefault();
        OnPropertyChanged(nameof(HasScreenshots));
        OnPropertyChanged(nameof(ScreenshotsHint));
        OnPropertyChanged(nameof(InstanceLabel));
    }

    private void OpenScreenshotsFolder()
    {
        if (_main.CurrentInstance is not { } instance)
        {
            Status = "先在左侧选一个版本。";
            return;
        }

        var directory = Path.Combine(instance.GameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        Open(directory);
    }

    private void RevealScreenshot()
    {
        if (SelectedScreenshot is { } row) Open($"/select,\"{row.FullPath}\"");
    }

    private static void Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", target)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
        }
    }

    private static void OpenUrl(string url) => Open(url);
}

/// <summary>一张游戏截图。</summary>
public sealed class ScreenshotRow
{
    public ScreenshotRow(FileInfo file)
    {
        FullPath = file.FullName;
        FileName = file.Name;
        SizeText = file.Length >= 1024 * 1024
            ? $"{file.Length / 1024.0 / 1024:0.0} MB"
            : $"{file.Length / 1024.0:0} KB";
        TakenText = file.LastWriteTime.ToString("MM-dd HH:mm");

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 360;
        image.UriSource = new Uri(file.FullName);
        image.EndInit();
        image.Freeze();
        Image = image;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string SizeText { get; }
    public string TakenText { get; }
    public ImageSource Image { get; }
}

/// <summary>结构筛选选项：一个分组对应若干 cubiomes 结构 key。</summary>
public sealed class StructureFilterOption : ObservableObject
{
    private bool _isEnabled = true;

    public StructureFilterOption(string group, string label)
    {
        Group = group;
        Label = label;
    }

    public string Group { get; }
    public string Label { get; }

    public override string ToString() => Label;

    public event Action<StructureFilterOption>? Changed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            Changed?.Invoke(this);
        }
    }

    /// <summary>
    /// 这一项覆盖的 cubiomes 结构 key。
    /// 现在「一项 = 一个结构类型」，所以直接就是 Group；
    /// 以前这里是 10 个硬编码分组，新增的分组全落到 _ => []，
    /// 结果勾选它们等于把地图上的结构全过滤掉。
    /// </summary>
    public IReadOnlyList<string> Key => [Group];
}

/// <summary>一个游戏语言选项：options.txt 里的 lang 值 + 给人看的名字。</summary>
public sealed record GameLanguage(string Code, string Name)
{
    public override string ToString() => Name;

}

/// <summary>「记录」页签的一行：某个版本的启动次数 / 总时长 / 最近游玩。</summary>
public sealed class PlayRecordRow
{
    public string InstanceId { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string Subtitle { get; private init; } = "";
    public int Launches { get; private init; }
    public string TotalPlayText { get; private init; } = "0 分钟";
    public string AverageText { get; private init; } = "—";
    public string LastPlayedText { get; private init; } = "从未";
    public DateTime? LastPlayed { get; private init; }
    public int AbnormalExits { get; private init; }
    public bool IsRemoved => string.IsNullOrEmpty(Subtitle);
    public string LaunchesText => Launches == 0 ? "未启动" : $"{Launches} 次";
    public string AbnormalText => AbnormalExits == 0 ? "" : $"异常退出 {AbnormalExits} 次";

    public static PlayRecordRow From(Orion.Core.Instances.Instance? instance, InstancePlayStats? stat)
    {
        var total = stat?.TotalSeconds ?? 0;
        var launches = stat?.Launches ?? 0;

        return new PlayRecordRow
        {
            InstanceId = instance?.Id ?? stat?.InstanceId ?? "",
            Name = instance?.Name ?? stat?.Name ?? "(已删除的版本)",
            Subtitle = instance is null ? "" : $"{instance.GameVersion} · {instance.Loader}",
            Launches = launches,
            TotalPlayText = InstancePlayStats.FormatDuration(TimeSpan.FromSeconds(total)),
            AverageText = launches > 0 ? InstancePlayStats.FormatDuration(TimeSpan.FromSeconds(total / (double)launches)) : "—",
            LastPlayedText = stat?.LastPlayedUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "从未",
            LastPlayed = stat?.LastPlayedUtc,
            AbnormalExits = stat?.AbnormalExits ?? 0,
        };
    }
}

/// <summary>「附近结构」的一行。</summary>
public sealed class NearbyStructure
{
    public string Key { get; private init; } = "";
    public string Name { get; private init; } = "";
    public int X { get; private init; }
    public int Z { get; private init; }
    public double Distance { get; private init; }

    public string DistanceText => Distance >= 1000
        ? $"{Distance / 1000:0.0} km"
        : $"{Distance:0} 格";

    public string CoordinatesText => $"{X}, {Z}";

    public static NearbyStructure Create(string key, string name, int x, int z, int centerX, int centerZ) => new()
    {
        Key = key,
        Name = name,
        X = x,
        Z = z,
        Distance = Math.Sqrt(((double)x - centerX) * (x - centerX) + ((double)z - centerZ) * (z - centerZ)),
    };
}

/// <summary>群系下拉项：cubiomes 的 id + 中文名。</summary>
public sealed class BiomeOption(int id, string name)
{
    public int Id { get; } = id;
    public string Name { get; } = name;

    public override string ToString() => Name;
}
