using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Instances;
using Orion.Core.Json;
using Orion.Core.Loaders;
using Orion.Core.Net;

namespace Orion.App.ViewModels;

/// <summary>One row of the version list.</summary>
public sealed class VersionRow
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset? ReleaseTime { get; init; }

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };

    public string TypeBrushKey => Type switch
    {
        "release" => "SuccessBrush",
        "snapshot" => "WarningBrush",
        _ => "MutedTextBrush",
    };

    public string ReleaseDate => ReleaseTime?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";
}

public sealed class LoaderOption(LoaderKind kind, string name)
{
    public LoaderKind Kind { get; } = kind;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public sealed class VersionFilterOption(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>Game download page: pick a version, pick a loader, install.</summary>
public sealed class VersionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private VersionFilterOption _selectedFilter;
    private LoaderOption _selectedLoader;
    private VersionRow? _selectedVersion;
    private LoaderVersionInfo? _selectedLoaderVersion;
    private string _searchText = "";
    private string _status = "正在读取版本清单…";
    private double _fraction;
    private bool _isInstalling;
    private bool _isLoadingVersions = true;
    private string _instanceName = "";

    public VersionsViewModel(MainViewModel main)
    {
        _main = main;

        _selectedFilter = Filters[0];
        _selectedLoader = Loaders[0];

        RefreshCommand = new AsyncRelayCommand(LoadVersionsAsync, () => !IsInstalling);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsInstalling && SelectedVersion is not null, ex => Status = "安装失败: " + ex.Message);
        CancelCommand = new RelayCommand(Cancel, () => IsInstalling);
    }

    public ObservableList<VersionRow> AllVersions { get; } = [];
    public ObservableList<VersionRow> FilteredVersions { get; } = [];
    public ObservableList<LoaderVersionInfo> LoaderVersions { get; } = [];

    public IReadOnlyList<VersionFilterOption> Filters { get; } =
    [
        new("release", "正式版"),
        new("snapshot", "快照"),
        new("old", "历史版本"),
        new("all", "全部"),
    ];

    public IReadOnlyList<LoaderOption> Loaders { get; } =
    [
        new(LoaderKind.Vanilla, "原版 Vanilla"),
        new(LoaderKind.NeoForge, "NeoForge"),
        new(LoaderKind.Fabric, "Fabric"),
        new(LoaderKind.Forge, "Forge"),
        new(LoaderKind.Quilt, "Quilt"),
    ];

    public ICommand RefreshCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    private CancellationTokenSource? _installCts;

    public VersionFilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!SetProperty(ref _selectedFilter, value)) return;
            ApplyFilter();
        }
    }

    public LoaderOption SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (!SetProperty(ref _selectedLoader, value)) return;
            OnPropertyChanged(nameof(SupportsLoaderVersions));
            _ = LoadLoaderVersionsAsync();
        }
    }

    public bool SupportsLoaderVersions => SelectedLoader.Kind != LoaderKind.Vanilla;

    public VersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!SetProperty(ref _selectedVersion, value)) return;
            ((AsyncRelayCommand)InstallCommand).RaiseCanExecuteChanged();
            _ = LoadLoaderVersionsAsync();

            if (value is not null && string.IsNullOrWhiteSpace(InstanceName))
                InstanceName = $"{value.Id} {SelectedLoader.Name}";
        }
    }

    public LoaderVersionInfo? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set => SetProperty(ref _selectedLoaderVersion, value);
    }

    public string InstanceName
    {
        get => _instanceName;
        set => SetProperty(ref _instanceName, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double Fraction
    {
        get => _fraction;
        private set => SetProperty(ref _fraction, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (!SetProperty(ref _isInstalling, value)) return;
            ((AsyncRelayCommand)InstallCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set => SetProperty(ref _isLoadingVersions, value);
    }

    public async Task LoadVersionsAsync()
    {
        IsLoadingVersions = true;
        Status = "正在读取版本清单…";

        try
        {
            var manifest = await _main.Context.VersionRepository.GetManifestAsync().ConfigureAwait(true);

            AllVersions.Replace(manifest.Versions.Select(v => new VersionRow
            {
                Id = v.Id,
                Type = v.Type,
                ReleaseTime = v.ReleaseTime,
            }));

            ApplyFilter();
            Status = $"共 {AllVersions.Count} 个版本（最新正式版 {manifest.Latest?.Release}，最新快照 {manifest.Latest?.Snapshot}）";

            SelectedVersion ??= FilteredVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = "无法获取版本清单: " + ex.Message;
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<VersionRow> query = AllVersions;

        query = SelectedFilter.Key switch
        {
            "release" => query.Where(v => v.Type == "release"),
            "snapshot" => query.Where(v => v.Type == "snapshot"),
            "old" => query.Where(v => v.Type is "old_beta" or "old_alpha"),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(v => v.Id.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        FilteredVersions.Replace(query.Take(400));
    }

    private async Task LoadLoaderVersionsAsync()
    {
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;

        if (SelectedVersion is null || SelectedLoader.Kind == LoaderKind.Vanilla) return;

        Status = $"正在获取 {SelectedLoader.Name} 版本列表…";

        try
        {
            var versions = await _main.Context.Loaders.GetVersionsAsync(SelectedLoader.Kind, SelectedVersion.Id).ConfigureAwait(true);
            LoaderVersions.Replace(versions.Take(80));

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Stable) ?? LoaderVersions.FirstOrDefault();

            Status = LoaderVersions.Count == 0
                ? $"{SelectedLoader.Name} 没有适用于 {SelectedVersion.Id} 的版本。"
                : $"{SelectedLoader.Name} {SelectedLoaderVersion?.Version} 可用于 {SelectedVersion.Id}（共 {LoaderVersions.Count} 个版本）";
        }
        catch (Exception ex)
        {
            Status = $"获取 {SelectedLoader.Name} 版本失败: {ex.Message}";
        }
    }

    private async Task InstallAsync()
    {
        var version = SelectedVersion;
        if (version is null) return;

        IsInstalling = true;
        Fraction = 0;
        _installCts = new CancellationTokenSource();
        var token = _installCts.Token;

        var loader = SelectedLoader.Kind;
        var title = loader == LoaderKind.Vanilla
            ? $"安装 Minecraft {version.Id}"
            : $"安装 {SelectedLoader.Name} {SelectedLoaderVersion?.Version} (MC {version.Id})";

        var activity = _main.Activities.Begin(title);
        var progress = new Progress<Core.Install.InstallProgress>(p =>
        {
            Fraction = p.Fraction;
            Status = $"[{p.Percent:0.0}%] {p.Message}";
            _main.Activities.Report(activity, p.Fraction,
                $"{p.Message} {p.Download?.CompletedFiles ?? 0}/{p.Download?.TotalFiles ?? 0}",
                p.Download is null ? "" : SpeedMeter.Humanize(p.Download.BytesPerSecond));
        });

        try
        {
            string versionId;

            if (loader == LoaderKind.Vanilla)
            {
                var result = await _main.Context.InstallService.InstallAsync(version.Id, new Core.Install.InstallOptions(), progress, token).ConfigureAwait(true);
                if (!result.Success)
                {
                    Status = result.Cancelled ? "安装已取消（可稍后继续）。" : "安装失败: " + result.Error;
                    _main.Activities.Finish(activity, result.Cancelled ? ActivityState.Cancelled : ActivityState.Failed, result.Error);
                    return;
                }

                versionId = version.Id;
                Status = $"安装完成：{result.FilesDownloaded} 个文件，{result.BytesDownloaded / 1024.0 / 1024.0:0.0} MB。";
            }
            else
            {
                var loaderVersion = SelectedLoaderVersion?.Version;
                if (string.IsNullOrWhiteSpace(loaderVersion))
                {
                    Status = $"请选择 {SelectedLoader.Name} 版本。";
                    _main.Activities.Finish(activity, ActivityState.Failed, "未选择加载器版本");
                    return;
                }

                var java = Core.Java.JavaDetector.SelectFor(21, _main.JavaRuntimes) ?? _main.JavaRuntimes.FirstOrDefault();
                var output = new Progress<string>(line =>
                {
                    Status = line;
                    _main.Activities.Report(activity, Math.Max(Fraction, 0.35), line, "");
                });

                var result = await _main.Context.Loaders.InstallAsync(
                    loader,
                    new LoaderInstallRequest
                    {
                        MinecraftVersion = version.Id,
                        LoaderVersion = loaderVersion,
                        Java = java,
                        InstallerMirror = _main.Context.Network.Source == DownloadSource.BmclApi
                            ? "https://bmclapi2.bangbang93.com/maven/"
                            : null,
                        Output = output,
                    },
                    token).ConfigureAwait(true);

                if (!result.Success)
                {
                    Status = "安装失败: " + result.Error;
                    _main.Activities.Finish(activity, ActivityState.Failed, result.Error);
                    _main.LogsPage.AppendSystem("加载器安装失败: " + result.Error);
                    return;
                }

                versionId = result.VersionId;
                Status = $"安装完成：版本 {versionId}。";
            }

            Fraction = 1;
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem($"安装完成: {versionId}");

            // Register an isolated instance for the freshly installed version.
            var name = string.IsNullOrWhiteSpace(InstanceName)
                ? $"{version.Id} {SelectedLoader.Name}".Trim()
                : InstanceName.Trim();

            var instance = await _main.Context.Instances.CreateAsync(name, versionId, version.Id, loader, SelectedLoaderVersion?.Version).ConfigureAwait(true);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);
            _main.CurrentInstance = _main.Instances.FirstOrDefault(i => i.Id == instance.Id);

            Status += $" 已创建实例「{instance.Name}」，可以到首页启动。";
            InstanceName = "";
        }
        catch (OperationCanceledException)
        {
            Status = "安装已取消（已下载的文件会被保留，可稍后继续）。";
            _main.Activities.Finish(activity, ActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            Status = "安装失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
            Orion.Core.Diagnostics.AppLog.Error("ui", "install-failed", ex);
        }
        finally
        {
            IsInstalling = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void Cancel() => _installCts?.Cancel();
}
