using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Orion.App.Mvvm;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Install;
using Orion.Core.Instances;
using Orion.Core.Java;
using Orion.Core.Launch;
using Orion.Core.Net;
using Orion.Core.Settings;

namespace Orion.App.ViewModels;

/// <summary>
/// The home page: one big launch button on a customisable background.
///
/// Everything account related lives in the dedicated login window, so this view model only owns the
/// launch flow, the status line under the button and the background image.
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private GameSession? _session;
    private bool _isLaunching;
    private string _launchStatus = "";
    private string _javaStatus = "";
    private string _instanceStatus = "";
    private readonly System.Windows.Threading.DispatcherTimer _memoryTimer = new();
    private readonly Services.PerfSampler _perf = new();
    private double? _cpuPercent;
    private double? _gpuPercent;

    private void RefreshMemorySample()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var mb = (int)Math.Round(process.WorkingSet64 / 1024.0 / 1024.0);
            if (mb != _launcherMemoryMb)
            {
                _launcherMemoryMb = mb;
                OnPropertyChanged(nameof(MemoryUsageText));
                OnPropertyChanged(nameof(MemoryPercent));
            }

            var cpu = _perf.SampleCpu();
            if (cpu is null ? _cpuPercent is not null : Math.Abs((cpu ?? 0) - (_cpuPercent ?? 0)) >= 1)
            {
                _cpuPercent = cpu;
                OnPropertyChanged(nameof(CpuText));
                OnPropertyChanged(nameof(CpuPercent));
            }

            var gpu = _perf.SampleGpu();
            if (gpu is null ? _gpuPercent is not null : Math.Abs((gpu ?? 0) - (_gpuPercent ?? 0)) >= 1)
            {
                _gpuPercent = gpu;
                OnPropertyChanged(nameof(GpuText));
                OnPropertyChanged(nameof(GpuPercent));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // process already gone: the sample is simply skipped
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    private int _launcherMemoryMb;
    private string _toastText = "";
    private ImageSource? _backgroundImage;
    private string? _backgroundImagePath;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => !IsLaunching, ex => LaunchStatus = "启动失败: " + ex.Message);
        StopCommand = new RelayCommand(StopGame, () => IsLaunching);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ChooseBackgroundCommand = new RelayCommand(ChooseBackground);
        ClearBackgroundCommand = new RelayCommand(ClearBackground, () => HasBackgroundImage);
        OpenVersionPickerCommand = new RelayCommand(() => _main.Navigate("instances"));
        OpenVersionSettingsCommand = new RelayCommand(() => _main.Navigate("instances"));   // sub-page, not a rail entry

        _memoryTimer.Interval = TimeSpan.FromSeconds(2);
        _memoryTimer.Tick += (_, _) => RefreshMemorySample();
        _memoryTimer.Start();
        RefreshMemorySample();

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = "";
        };

        ReloadBackground();
    }

    /// <summary>Transient launcher message shown as a floating toast, cleared automatically.</summary>
    public string ToastText
    {
        get => _toastText;
        private set
        {
            if (!SetProperty(ref _toastText, value)) return;
            OnPropertyChanged(nameof(HasToast));
        }
    }

    public bool HasToast => _toastText.Length > 0;

    private void ShowToast(string message)
    {
        ToastText = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public ICommand LaunchCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ChooseBackgroundCommand { get; }
    public ICommand ClearBackgroundCommand { get; }
    public ICommand OpenVersionPickerCommand { get; }
    public ICommand OpenVersionSettingsCommand { get; }

    public Instance? SelectedInstance => _main.CurrentInstance;

    /// <summary>Versions offered by the home page picker.</summary>
    public ObservableList<Instance> Instances => _main.Instances;

    /// <summary>Version chosen on the home page; changing it updates the selected version globally.</summary>
    public Instance? SelectedLaunchInstance
    {
        get => _main.CurrentInstance;
        set
        {
            // A ComboBox pushes null while its ItemsSource is still empty (startup) - that must not
            // clear the selected version.
            if (value is null || ReferenceEquals(value, _main.CurrentInstance)) return;

            _main.CurrentInstance = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstanceTitle));
            OnPropertyChanged(nameof(InstanceAvatarBrush));
            RefreshInstanceIcon();
            OnPropertyChanged(nameof(InstanceSubtitle));
            OnPropertyChanged(nameof(LaunchVersionLine));
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// 实例头像：优先用整合包自带的 icon.png（很多 CF/Modrinth 包都带），
    /// 没有就用"名字哈希 → 固定渐变 + 首字母"，比原来的浅灰占位清楚得多。
    /// </summary>
    public System.Windows.Media.Brush InstanceAvatarBrush
    {
        get
        {
            var seed = _main.CurrentInstance?.Name ?? "Orap Craft";
            var hash = 0;
            foreach (var ch in seed) hash = (hash * 31 + ch) & 0x7FFFFFFF;

            var hue = hash % 360;
            var from = ColorFromHue(hue, 0.55, 0.62);
            var to = ColorFromHue((hue + 38) % 360, 0.65, 0.42);

            return new System.Windows.Media.LinearGradientBrush(from, to, 45);
        }
    }

    private static System.Windows.Media.Color ColorFromHue(int hue, double saturation, double lightness)
    {
        var h = hue / 360.0;
        var s = saturation;
        var l = lightness;

        double R, G, B;
        if (s == 0) { R = G = B = l; }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            R = HueToRgb(p, q, h + 1.0 / 3);
            G = HueToRgb(p, q, h);
            B = HueToRgb(p, q, h - 1.0 / 3);
        }

        return System.Windows.Media.Color.FromRgb((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private System.Windows.Media.ImageSource? _instanceIcon;

    /// <summary>整合包自带的图标（&lt;gameDir&gt;/icon.png）；没有就是 null。</summary>
    public System.Windows.Media.ImageSource? InstanceIcon
    {
        get => _instanceIcon;
        private set
        {
            _instanceIcon = value;
            OnPropertyChanged(nameof(InstanceIcon));
            OnPropertyChanged(nameof(HasInstanceIcon));
        }
    }

    public bool HasInstanceIcon => _instanceIcon is not null;

    /// <summary>从实例目录里的 icon.png 读图标（读不到就当作没有，界面回落到渐变首字母）。</summary>
    public void RefreshInstanceIcon()
    {
        var gameDirectory = _main.CurrentInstance?.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory)) { InstanceIcon = null; return; }

        var path = System.IO.Path.Combine(gameDirectory, "icon.png");
        if (!System.IO.File.Exists(path)) { InstanceIcon = null; return; }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            InstanceIcon = image;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UriFormatException)
        {
            InstanceIcon = null;
        }
    }

    public string InstanceTitle => _main.CurrentInstance?.Name ?? "尚未选择版本";

    public string InstanceSubtitle => _main.CurrentInstance is null
        ? "前往「版本管理」创建或导入一个版本"
        : _main.CurrentInstance.Subtitle;

    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!SetProperty(ref _isLaunching, value)) return;
            OnPropertyChanged(nameof(LaunchButtonText));
            ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)LaunchCommand).RaiseCanExecuteChanged();
        }
    }

    public string LaunchButtonText => IsLaunching ? "运行中…" : "启动游戏";

    public string RuntimeText => string.IsNullOrWhiteSpace(JavaStatus) ? "自动选择" : JavaStatus;

    public string MemoryText => ((_main.CurrentInstance?.MemoryMb ?? _main.Context.Settings.MemoryMb)).ToString() + " MB";

    public string StateText => IsLaunching ? "运行中" : "就绪";

    /// <summary>Launcher working set, refreshed a few times per second by the status timer.</summary>
    /// <summary>内存占用相对分配内存的百分比，用于性能条。</summary>
    public double MemoryPercent
    {
        get
        {
            var allocated = Math.Max(1, _main.CurrentInstance?.EffectiveMemory ?? _main.Context.Settings.MemoryMb);
            return Math.Clamp(_launcherMemoryMb * 100.0 / allocated, 0, 100);
        }
    }

    /// <summary>系统 CPU 占用，计数器不可用时显示 —（不显示假的 0%）。</summary>
    public string CpuText => _cpuPercent is { } v ? $"{v:0} %" : "—";

    public double CpuPercent => _cpuPercent ?? 0;

    /// <summary>GPU 占用，驱动未暴露 GPU Engine 计数器时显示 —。</summary>
    public string GpuText => _gpuPercent is { } v ? $"{v:0} %" : "—";

    public double GpuPercent => _gpuPercent ?? 0;

    public string MemoryUsageText
    {
        get
        {
            var used = _launcherMemoryMb;
            var allocated = _main.CurrentInstance?.MemoryMb ?? _main.Context?.Settings.MemoryMb ?? 0;
            return $"{used} MB 已用 · {allocated} MB 分配";
        }
    }

    /// <summary>Second line of the launch button (PCL style): which version will start.</summary>
    public string LaunchVersionLine => _main.CurrentInstance is { } instance
        ? $"{instance.Name} · {instance.Subtitle}"
        : "尚未选择版本";

    public string LaunchStatus
    {
        get => _launchStatus;
        set
        {
            if (!SetProperty(ref _launchStatus, value)) return;

            // Short hints become a toast; long progress text stays visible while it is meaningful.
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 120) ShowToast(value);
        }
    }

    public string JavaStatus
    {
        get => _javaStatus;
        set => SetProperty(ref _javaStatus, value);
    }

    public string InstanceStatus
    {
        get => _instanceStatus;
        set => SetProperty(ref _instanceStatus, value);
    }

    /// <summary>User supplied background picture, or null for the built-in gradient.</summary>
    public ImageSource? BackgroundImage
    {
        get => _backgroundImage;
        private set
        {
            if (!SetProperty(ref _backgroundImage, value)) return;
            OnPropertyChanged(nameof(HasBackgroundImage));
            ((RelayCommand)ClearBackgroundCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasBackgroundImage => _backgroundImage is not null;

    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        private set => SetProperty(ref _backgroundImagePath, value);
    }

    public async Task RefreshAsync()
    {
        var instance = _main.CurrentInstance;
        OnPropertyChanged(nameof(SelectedInstance));
        OnPropertyChanged(nameof(InstanceTitle));
        OnPropertyChanged(nameof(InstanceSubtitle));

        if (instance is null)
        {
            JavaStatus = "未选择版本";
            InstanceStatus = "请先创建版本";
            return;
        }

        try
        {
            var resolved = await _main.Context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);

            var runtimes = _main.JavaRuntimes.Count > 0
                ? _main.JavaRuntimes.ToList()
                : (await _main.Context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

            var java = SelectJava(instance, resolved.RequiredJavaMajorVersion, runtimes);
            JavaStatus = java is null
                ? $"未找到 Java {resolved.RequiredJavaMajorVersion}"
                : java.DisplayName;

            var installed = File.Exists(_main.Context.Paths.VersionJarPath(resolved.ClientJarVersionId));
            InstanceStatus = installed ? "已安装" : "未安装（首次启动会自动下载）";
        }
        catch (Exception ex) when (ex is VersionNotFoundException or HttpRequestException or IOException)
        {
            JavaStatus = "无法解析版本";
            InstanceStatus = ex.Message;
        }
    }

    private static JavaRuntime? SelectJava(Instance instance, int requiredMajor, IReadOnlyList<JavaRuntime> runtimes)
    {
        if (!string.IsNullOrWhiteSpace(instance.JavaPath) && File.Exists(instance.JavaPath))
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(instance.JavaPath));
            var match = runtimes.FirstOrDefault(r => string.Equals(r.Home, home, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return JavaDetector.SelectFor(requiredMajor, runtimes);
    }

    /// <summary>
    /// 启动前预检：扫描 mods 目录的必需前置，缺就自动补齐；补不上再问用户要不要硬启动。
    /// 返回 false 表示用户选择取消启动。
    /// </summary>
    private async Task<bool> PreflightDependenciesAsync(Core.Instances.Instance instance)
    {
        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        if (!Directory.Exists(modsDirectory)) return true;

        IReadOnlyList<Core.Mods.RequiredDependency> missing;
        try
        {
            var scan = Core.Mods.ModDependencyScanner.Scan(modsDirectory);
            missing = scan.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫描失败不该拦住启动
            return true;
        }

        if (missing.Count == 0) return true;

        LaunchStatus = $"检测到 {missing.Count} 个缺失前置，正在自动补齐…";

        try
        {
            var result = await _main.Context.DependencyRepair.RepairAsync(
                instance.GameDirectory,
                missing,
                instance.GameVersion,
                instance.Loader == Core.Instances.LoaderKind.Vanilla
                    ? null
                    : Core.Mods.CurseForgeClient.LoaderTypeOf(instance.Loader),
                Path.Combine(instance.GameDirectory, ".orap-cache"),
                new Progress<string>(line => LaunchStatus = line)).ConfigureAwait(true);

            if (result.Remaining.Count == 0)
            {
                LaunchStatus = result.Installed.Count > 0
                    ? $"已自动补齐 {result.Installed.Count} 个前置。"
                    : LaunchStatus;
                return true;
            }

            var list = string.Join("\n", result.Remaining.Take(10)
                .Select(m => $"· {m.ModId}（被 {m.RequiredBy} 需要）"));
            var answer = MessageBox.Show(
                $"还有 {result.Remaining.Count} 个前置没补上：\n\n{list}\n\n" +
                "现在启动很可能会停在「Error loading mods」界面。仍要启动吗？",
                "前置缺失",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            var answer = MessageBox.Show(
                "检查前置时网络出错：" + ex.Message + "\n\n仍要尝试启动吗？",
                "检查前置失败",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes;
        }
    }

    private async Task LaunchAsync()
    {
        var instance = _main.CurrentInstance;
        if (instance is null)
        {
            LaunchStatus = "请先在「版本管理」中创建或选择一个版本。";
            _main.Navigate("instances");
            return;
        }

        var account = _main.CurrentAccount;
        if (account is null)
        {
            LaunchStatus = "请先在左侧登录账户。";
            _main.OpenLogin();
            return;
        }

        if (account.Type == AccountType.Microsoft && account.IsExpired)
        {
            LaunchStatus = "正在刷新微软登录令牌…";
            await RefreshTokenAsync().ConfigureAwait(true);
            account = _main.CurrentAccount;
            if (account is null || account.IsExpired)
            {
                LaunchStatus = "微软账户令牌已失效，请重新登录。";
                _main.OpenLogin();
                return;
            }
        }

        // 启动前先查一遍缺失前置：整合包最常见的崩溃就是缺前置（Error loading mods）。
        if (!await PreflightDependenciesAsync(instance).ConfigureAwait(true)) return;

        var activity = _main.Activities.Begin($"启动 {instance.Name}", instance.Subtitle);
        IsLaunching = true;
        LaunchStatus = "正在准备启动…";

        try
        {
            var context = _main.Context;
            var resolved = await context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);

            var runtimes = _main.JavaRuntimes.Count > 0
                ? _main.JavaRuntimes.ToList()
                : (await context.JavaDetector.DiscoverAsync().ConfigureAwait(true)).ToList();

            var java = SelectJava(instance, resolved.RequiredJavaMajorVersion, runtimes);
            if (java is null)
            {
                LaunchStatus = $"未找到 Java {resolved.RequiredJavaMajorVersion}。请到「设置」页指定 Java 路径。";
                // Stay on the home page: jumping to Settings threw away the user context.
                var detected = string.Join("、", runtimes.Select(r => r.MajorVersion).Where(m => m > 0).Distinct().OrderByDescending(m => m).Select(m => "Java " + m));
                LaunchStatus = detected.Length == 0
                    ? $"{instance.Name} 需要 Java {resolved.RequiredJavaMajorVersion}，但没有检测到任何 Java。"
                    : $"{instance.Name} 需要 Java {resolved.RequiredJavaMajorVersion}，只检测到 {detected}。请安装对应 JDK，或在「设置 → Java」手动指定路径。";
                _main.Activities.Finish(activity, ActivityState.Failed, LaunchStatus);
                _main.LogsPage.AppendSystem(LaunchStatus);
                return;
            }

            if (!File.Exists(context.Paths.VersionJarPath(resolved.ClientJarVersionId)))
            {
                  LaunchStatus = "客户端未安装，正在自动安装…";
                _main.Activities.Report(activity, 0.05, "安装游戏文件…", "");

                var progress = new Progress<InstallProgress>(p =>
                {
                    _main.Activities.Report(activity, p.Fraction * 0.6, p.Message,
                        p.Download is null ? "" : SpeedMeter.Humanize(p.Download.BytesPerSecond));
                    LaunchStatus = p.Message;
                });

                var install = await context.InstallService
                    .InstallAsync(instance.VersionId, new InstallOptions(), progress)
                    .ConfigureAwait(true);

                if (!install.Success)
                {
                    LaunchStatus = "安装失败: " + (install.Error ?? "未知错误");
                    _main.Activities.Finish(activity, ActivityState.Failed, install.Error);
                    return;
                }

                resolved = await context.VersionResolver.ResolveAsync(instance.VersionId).ConfigureAwait(true);
            }

            var request = new LaunchRequest
            {
                Version = resolved,
                Account = account,
                Java = java,
                GameDirectory = instance.GameDirectory,
                MemoryMb = instance.MemoryMb ?? context.Settings.MemoryMb,
                MinMemoryMb = instance.MinMemoryMb ?? context.Settings.MinMemoryMb,
                ExtraJvmArguments = instance.JvmArguments ?? context.Settings.JvmArguments,
                ExtraGameArguments = instance.GameArguments,
                WindowWidth = instance.WindowWidth,
                WindowHeight = instance.WindowHeight,
                Fullscreen = instance.Fullscreen,
                LauncherVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            };

            _main.Activities.Report(activity, 0.7, "启动 JVM…", "");

            var logProgress = new Progress<string>(line =>
            {
                LaunchStatus = line;
                _main.LogsPage.AppendSystem(line);
            });

            _session = await context.Launcher.LaunchAsync(request, logProgress).ConfigureAwait(true);
            _session.OutputReceived += OnGameOutput;

            InstanceStatus = $"运行中 · PID {_session.ProcessId}";
            LaunchStatus = $"游戏已启动 (PID {_session.ProcessId})";
            _main.Activities.Report(activity, 1, "游戏运行中", "");
            _main.Activities.Finish(activity, ActivityState.Completed);

            var exitCode = await _session.WaitForExitAsync().ConfigureAwait(true);
            var killed = _session.KilledByLauncher;
            var lines = _session.RecentLines;
            var logPath = _session.LogFilePath;

            await _session.DisposeAsync().ConfigureAwait(true);
            _session.OutputReceived -= OnGameOutput;
            _session = null;
            IsLaunching = false;

            instance.LastPlayedAt = DateTimeOffset.Now;
            await context.Instances.SaveAsync(instance).ConfigureAwait(true);

            if (killed)
            {
                LaunchStatus = "游戏已被启动器结束。";
                InstanceStatus = "已停止";
                _main.LogsPage.AppendSystem("游戏进程已由启动器结束。");
                return;
            }

            var analysis = await CrashAnalyzer.AnalyzeAsync(instance.GameDirectory, exitCode, lines).ConfigureAwait(true);
            _main.LogsPage.SetCrashAnalysis(analysis, logPath);

            if (exitCode == 0)
            {
                LaunchStatus = "游戏已正常退出。";
                InstanceStatus = "已退出";
            }
            else
            {
                LaunchStatus = $"游戏异常退出 (代码 {exitCode}) — {analysis.Summary}";
                InstanceStatus = "崩溃";
            }
        }
        catch (OperationCanceledException)
        {
            LaunchStatus = "启动已取消。";
            _main.Activities.Finish(activity, ActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            LaunchStatus = "启动失败: " + ex.Message;
            AppLog.Error("ui", "launch-failed", ex);
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            if (_session is null) IsLaunching = false;
        }
    }

    private void OnGameOutput(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(() => _main.LogsPage.AppendGame(line));
    }

    private void StopGame()
    {
        if (_session is null) return;

        LaunchStatus = "正在结束游戏进程…";
        _session.Kill();
    }

    private async Task RefreshTokenAsync()
    {
        var account = _main.CurrentAccount;
        if (account is null || account.Type != AccountType.Microsoft) return;

        try
        {
            LaunchStatus = "正在刷新令牌…";
            var refreshed = await _main.Context.Auth.RefreshAsync(account).ConfigureAwait(true);

            var accounts = (await _main.Context.AccountStore.LoadAsync().ConfigureAwait(true)).ToList();
            var index = accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) accounts[index] = refreshed;
            else accounts.Add(refreshed);

            await _main.Context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);
            await _main.ReloadAccountsAsync().ConfigureAwait(true);
            _main.CurrentAccount = _main.Accounts.FirstOrDefault(a => string.Equals(a.Id, refreshed.Id, StringComparison.OrdinalIgnoreCase));

            LaunchStatus = $"令牌已刷新: {refreshed.Username}";
            _main.LogsPage.AppendSystem($"微软令牌已刷新 ({refreshed.Username})");
        }
        catch (MicrosoftAuthException ex)
        {
            LaunchStatus = ex.Message;
        }
    }

    #region background image

    /// <summary>Reads the saved path (also used on startup) and decodes it into a brush source.</summary>
    public void ReloadBackground()
    {
        var path = _main.Context.Settings.BackgroundImagePath;
        ApplyBackground(path, persist: false);
    }

    private void ChooseBackground()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择主界面背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        ApplyBackground(dialog.FileName, persist: true);
    }

    private void ClearBackground()
    {
        ApplyBackground(null, persist: true);
    }

    private void ApplyBackground(string? path, bool persist)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundImagePath = null;
            BackgroundImage = null;
        }
        else
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                BackgroundImagePath = path;
                BackgroundImage = image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                BackgroundImagePath = null;
                BackgroundImage = null;
                AppLog.Warn("ui", "background-load-failed", ("error", ex.Message));
                _main.LogsPage.AppendSystem("无法读取该背景图片：" + ex.Message);
            }
        }

        if (!persist) return;

        _main.Context.Settings.BackgroundImagePath = BackgroundImagePath;
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _main.Context.SettingsStore.SaveAsync(_main.Context.Settings).ConfigureAwait(true);
            AppLog.Info("ui", "background-saved", ("path", BackgroundImagePath ?? "<default>"));
        }
        catch (IOException ex)
        {
            AppLog.Warn("ui", "background-save-failed", ("error", ex.Message));
        }
    }

    #endregion
}
