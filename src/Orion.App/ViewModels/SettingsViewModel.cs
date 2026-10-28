using System.Diagnostics;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Java;
using Orion.Core.Net;

namespace Orion.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private string _javaPath = "";
    private int _memoryMb = 4096;
    private int _minMemoryMb = 512;
    private int _maxConcurrency = 8;
    private DownloadSource _source = DownloadSource.Official;
    private string _proxyUrl = "";
    private string _theme = "Dark";
    private string _accent = "Blue";
    private int _sectionIndex;

    /// <summary>子界面：0 常规 / 1 外观 / 2 Java 与内存 / 3 网络与下载 / 4 账户 / 5 关于。</summary>
    public int SectionIndex
    {
        get => _sectionIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Sections.Count - 1);
            if (!SetProperty(ref _sectionIndex, clamped)) return;

            OnPropertyChanged(nameof(IsGeneralSection));
            OnPropertyChanged(nameof(IsAppearanceSection));
            OnPropertyChanged(nameof(IsJavaSection));
            OnPropertyChanged(nameof(IsNetworkSection));
            OnPropertyChanged(nameof(IsAccountSection));
            OnPropertyChanged(nameof(IsAboutSection));
            OnPropertyChanged(nameof(SectionSubtitle));
        }
    }

    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new("general", "常规", "\uE7F4", "启动行为、数据目录与日志编码"),
        new("appearance", "外观与主题", "\uE790", "主题、主题色与主界面背景"),
        new("java", "Java 与内存", "\uE943", "运行时路径、内存分配"),
        new("network", "网络与下载", "\uE896", "下载源、并发、代理与 CurseForge"),
        new("account", "账户", "\uE77B", "微软登录客户端 ID"),
        new("about", "关于与维护", "\uE946", "版本信息、数据位置与清理"),
    ];

    public string SectionSubtitle => Sections[Math.Clamp(_sectionIndex, 0, Sections.Count - 1)].Subtitle;

    public bool IsGeneralSection => _sectionIndex == 0;
    public bool IsAppearanceSection => _sectionIndex == 1;
    public bool IsJavaSection => _sectionIndex == 2;
    public bool IsNetworkSection => _sectionIndex == 3;
    public bool IsAccountSection => _sectionIndex == 4;
    public bool IsAboutSection => _sectionIndex == 5;

    /// <summary>Accent swatches shown in settings (colour is only for the preview dot).</summary>
    public IReadOnlyList<AccentOption> Accents { get; } =
    [
        new("Moss", "苔藓绿", "#FF6FBF73"),
        new("Jade", "墨绿", "#FF1F8A5B"),
        new("Ochre", "赭石", "#FF9B5F2A"),
        new("Blue", "经典蓝", "#FF4C8DFF"),
        new("Violet", "紫罗兰", "#FF8B5CF6"),
        new("Teal", "青绿", "#FF14B8A6"),
        new("Amber", "琥珀", "#FFF59E0B"),
        new("Rose", "玫瑰", "#FFF43F5E"),
        new("Orange", "暖橙", "#FFF97316"),
        new("Emerald", "翡翠", "#FF10B981"),
    ];
    private bool _forceUtf8Console = true;
    private string _microsoftClientId = "";
    private string _curseForgeApiKey = "";
    private string _status = "设置会立即保存到数据目录。";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;

        SaveCommand = new AsyncRelayCommand(SaveAsync, null, ex => Status = "保存失败: " + ex.Message);
        TestCurseForgeKeyCommand = new AsyncRelayCommand(TestCurseForgeKeyAsync, null, ex => Status = "测试失败: " + ex.Message);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.Root));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.LogsDir));
        OpenAccountsCommand = new RelayCommand(() => _main.Navigate("home"));
    }

    public ICommand SaveCommand { get; }
    public ICommand TestCurseForgeKeyCommand { get; }
    public ICommand DetectJavaCommand { get; }
    public ICommand BrowseJavaCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenAccountsCommand { get; }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("Rock", "深岩（默认）"),
        new("Snow", "霜白（浅色）"),
        new("Paper", "暖纸（浅色）"),
        ];

    public IReadOnlyList<SourceOption> SourceOptions { get; } =
    [
        new(DownloadSource.Official, "Mojang / Microsoft 官方源"),
        new(DownloadSource.BmclApi, "BMCLAPI 镜像（备用）"),
    ];

    public string DataRoot => _main.Context.Paths.Root;

    /// <summary>关于页展示的启动器版本。</summary>
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version is { } v
        ? $"v{v.Major}.{v.Minor}.{v.Build}"
        : "v0.2";

    public ObservableList<JavaRuntime> JavaRuntimes => _main.JavaRuntimes;

    public string JavaPath
    {
        get => _javaPath;
        set => SetProperty(ref _javaPath, value);
    }

    public int MemoryMb
    {
        get => _memoryMb;
        set => SetProperty(ref _memoryMb, Math.Clamp(value, 512, 65536));
    }

    public int MinMemoryMb
    {
        get => _minMemoryMb;
        set => SetProperty(ref _minMemoryMb, Math.Clamp(value, 256, 16384));
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => SetProperty(ref _maxConcurrency, Math.Clamp(value, 1, 256));
    }

    public DownloadSource Source
    {
        get => _source;
        set => SetProperty(ref _source, value);
    }

    public string ProxyUrl
    {
        get => _proxyUrl;
        set => SetProperty(ref _proxyUrl, value);
    }

    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            App.ApplyTheme(value, _accent);
        }
    }

    /// <summary>Accent colour scheme (Blue / Violet / Teal / Amber / Rose).</summary>
    public string Accent
    {
        get => _accent;
        set
        {
            if (!SetProperty(ref _accent, value)) return;
            App.ApplyTheme(_theme, value);
        }
    }

    public bool ForceUtf8Console
    {
        get => _forceUtf8Console;
        set => SetProperty(ref _forceUtf8Console, value);
    }

    /// <summary>CurseForge API key: enables the official download-url endpoint for CF packs.</summary>
    public string CurseForgeApiKey
    {
        get => _curseForgeApiKey;
        set => SetProperty(ref _curseForgeApiKey, value);
    }

    public string MicrosoftClientId
    {
        get => _microsoftClientId;
        set => SetProperty(ref _microsoftClientId, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Calls the CurseForge games endpoint: 200 means the key is accepted.</summary>
    private async Task TestCurseForgeKeyAsync()
    {
        var key = CurseForgeApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            Status = "请先填写 CurseForge API Key。";
            return;
        }

        Status = "正在验证 CurseForge API Key…";
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, "https://api.curseforge.com/v1/games?pageSize=1");
            request.Headers.TryAddWithoutValidation("x-api-key", key);

            using var response = await _main.Context.Http.SendAsync(request).ConfigureAwait(true);
            Status = response.IsSuccessStatusCode
                ? $"Key 有效（HTTP {(int)response.StatusCode}）。保存后即可用于 CurseForge 整合包。"
                : $"Key 被拒绝：HTTP {(int)response.StatusCode}。请检查是否复制完整或申请是否通过。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status = "测试失败：" + ex.Message;
        }
    }
    public void LoadFromSettings()
    {
        var settings = _main.Context.Settings;

        JavaPath = settings.JavaPath ?? "";
        MemoryMb = settings.MemoryMb;
        MinMemoryMb = settings.MinMemoryMb;
        MaxConcurrency = settings.MaxConcurrency;
        Source = settings.Source;
        ProxyUrl = settings.ProxyUrl ?? "";
        Theme = settings.Theme;
        Accent = settings.Accent;
        ForceUtf8Console = settings.ForceUtf8Console;
        MicrosoftClientId = settings.MicrosoftClientId ?? "";
        CurseForgeApiKey = settings.CurseForgeApiKey ?? "";

        OnPropertyChanged(nameof(DataRoot));
    }

    private async Task SaveAsync()
    {
        var settings = _main.Context.Settings;

        settings.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
        settings.MemoryMb = MemoryMb;
        settings.MinMemoryMb = MinMemoryMb;
        settings.MaxConcurrency = MaxConcurrency;
        settings.Source = Source;
        settings.ProxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim();
        settings.Theme = Theme;
        settings.Accent = Accent;
        settings.ForceUtf8Console = ForceUtf8Console;
        settings.MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim();
        settings.CurseForgeApiKey = string.IsNullOrWhiteSpace(CurseForgeApiKey) ? null : CurseForgeApiKey.Trim();

        await _main.Context.SettingsStore.SaveAsync(settings).ConfigureAwait(true);

        _main.Context.Network.Source = Source;
        _main.Context.Network.ProxyUrl = settings.ProxyUrl;
        _main.Context.DownloadEngineOptions.MaxConcurrency = MaxConcurrency;

        Status = "设置已保存。代理与下载源的变更将在下次启动后对新的 HTTP 连接生效。";
        _main.LogsPage.AppendSystem("设置已保存。");
    }

    private async Task DetectJavaAsync()
    {
        Status = "正在检测系统 Java…";
        var runtimes = await _main.Context.JavaDetector.DiscoverAsync(forceRefresh: true).ConfigureAwait(true);
        _main.JavaRuntimes.Replace(runtimes);

        Status = runtimes.Count == 0
            ? "没有检测到 Java。请手动指定 java.exe 路径，或安装 Java 21。"
            : $"检测到 {runtimes.Count} 个 Java 运行时：{string.Join("、", runtimes.Select(r => $"Java {r.MajorVersion}"))}";
    }

    private void BrowseJava()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件|java.exe|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true) JavaPath = dialog.FileName;
    }

    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}

/// <summary>设置页左侧的子界面条目。</summary>
public sealed record SettingsSection(string Key, string Name, string Glyph, string Subtitle);

public sealed class AccentOption(string key, string name, string color)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public System.Windows.Media.Color Color { get; } = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!;
}

public sealed class ThemeOption(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public sealed class SourceOption(DownloadSource source, string name)
{
    public DownloadSource Source { get; } = source;
    public string Name { get; } = name;
    public override string ToString() => Name;
}
