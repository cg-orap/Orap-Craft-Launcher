using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        LocateCommand = new RelayCommand(LocateTo);
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
    public IReadOnlyList<string> Tabs { get; } = ["种子地图", "查看截图", "游戏默认设置"];

    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, Math.Clamp(value, 0, Tabs.Count - 1))) return;
            OnPropertyChanged(nameof(IsSeedTab));
            OnPropertyChanged(nameof(IsScreenshotsTab));
            OnPropertyChanged(nameof(IsDefaultsTab));

            if (IsScreenshotsTab) RefreshScreenshots();
        }
    }

    public bool IsSeedTab => _tabIndex == 0;
    public bool IsScreenshotsTab => _tabIndex == 1;
    public bool IsDefaultsTab => _tabIndex == 2;

    public string InstanceLabel => _main.CurrentInstance is { } instance
        ? $"{instance.Name} · MC {instance.GameVersion}"
        : "未选择版本";

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

    /// <summary>当前用的是哪个引擎（cubiomes 原生 / 简化模型）。</summary>
    public string MapEngineLabel => CubiomesNative.Available
        ? $"群系引擎：cubiomes（支持 {CubiomesNative.Versions[0]} 及更早）"
        : "群系引擎：简化模型（未加载 OrapCubiomes.dll）";

    public string MapModelNotice =>
        CubiomesNative.Available
            ? "群系由 cubiomes 按原版算法生成（与 Chunkbase / Amidst 同源）；史莱姆区块同样是精确值。"
            : "群系由 5 通道噪声近似生成（可复现，但与游戏内实际地形不保证一致）；史莱姆区块是精确值。";


    /// <summary>坐标定位：X / Z 输入框 + 事件（画布由 View 来驱动）。</summary>
    public string LocateX { get; set; } = "0";
    public string LocateZ { get; set; } = "0";

    public event Action<int, int>? LocateRequested;

    public ICommand LocateCommand { get; }

    public ICommand GenerateMapCommand { get; }

    public ICommand OpenChunkbaseCommand { get; }
    public ICommand OpenMislandCommand { get; }
    public ICommand CopySeedCommand { get; }

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

    public string DefaultsSummary => $"视距 {RenderDistance} · 界面尺寸 {(GuiScale == 0 ? "自动" : GuiScale.ToString())} · "
        + $"自动跳跃 {(AutoJump ? "开" : "关")} · FOV {Fov}";

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

        GameOptionsTemplate.Default = template;

        OnPropertyChanged(nameof(DefaultsEnabled));
        OnPropertyChanged(nameof(RenderDistance));
        OnPropertyChanged(nameof(GuiScale));
        OnPropertyChanged(nameof(AutoJump));
        OnPropertyChanged(nameof(Fov));
        OnPropertyChanged(nameof(Gamma));
        OnPropertyChanged(nameof(Particles));
        OnPropertyChanged(nameof(BobView));
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
