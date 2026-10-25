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
    private string _section = "general";

    /// <summary>Active sub-tab: general (appearance, data, Java, memory) or network (download, account, logs).</summary>
    public string Section
    {
        get => _section;
        set
        {
            if (!SetProperty(ref _section, value)) return;
            OnPropertyChanged(nameof(IsGeneralSection));
            OnPropertyChanged(nameof(IsNetworkSection));
        }
    }

    public bool IsGeneralSection
    {
        get => _section == "general";
        set { if (value) Section = "general"; }
    }

    public bool IsNetworkSection
    {
        get => _section == "network";
        set { if (value) Section = "network"; }
    }

    /// <summary>Accent swatches shown in settings (colour is only for the preview dot).</summary>
    public IReadOnlyList<AccentOption> Accents { get; } =
    [
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
    private string _status = "设置会立即保存到数据目录。";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;

        SaveCommand = new AsyncRelayCommand(SaveAsync, null, ex => Status = "保存失败: " + ex.Message);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.Root));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.LogsDir));
        OpenAccountsCommand = new RelayCommand(() => _main.Navigate("home"));
    }

    public ICommand SaveCommand { get; }
    public ICommand DetectJavaCommand { get; }
    public ICommand BrowseJavaCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenAccountsCommand { get; }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("Dark", "深色"),
        new("Light", "浅色"),
    ];

    public IReadOnlyList<SourceOption> SourceOptions { get; } =
    [
        new(DownloadSource.Official, "Mojang / Microsoft 官方源"),
        new(DownloadSource.BmclApi, "BMCLAPI 镜像（备用）"),
    ];

    public string DataRoot => _main.Context.Paths.Root;

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
        set => SetProperty(ref _maxConcurrency, Math.Clamp(value, 1, 32));
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
