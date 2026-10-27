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
        ModsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = MatchesFilter;
        SetModFilterCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key && key.Length > 0) ModFilter = key.ToUpperInvariant();
        });
        _main = main;

        RescanCommand = new AsyncRelayCommand(LoadModsAsync, () => SelectedInstance is not null);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchText) && !IsSearching);
        OpenFolderCommand = new RelayCommand(OpenModsFolder);
        OpenModrinthPageCommand = new RelayCommand(OpenModrinthPage);
    }

    private string _modFilter = "ALL";

    private bool MatchesFilter(object item) => item is ModRow row && _modFilter switch
    {
        "ENABLED" => row.Enabled,
        "DISABLED" => !row.Enabled,
        _ => true,
    };

    /// <summary>检查更新：只查询，不下载；发现可更新项后弹确认，用户同意才下载。</summary>
    public ICommand CheckUpdatesCommand { get; }

    public ObservableList<ModUpdateCandidate> Updates { get; } = [];

    public ICommand RescanCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenModrinthPageCommand { get; }

    public ObservableList<ModRow> Mods { get; } = [];

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

    public int DisabledCount => Mods.Count(m => !m.Enabled);
    public ObservableList<ModrinthRow> SearchResults { get; } = [];

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
            var mods = await ModScanner.ScanAsync(instance.GameDirectory).ConfigureAwait(true);
            Mods.Replace(mods.Select(m => new ModRow(m, this, ModScanner.IsCompatibleWith(m, instance.Loader))));

            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(IncompatibleCount));

            Status = Mods.Count == 0
                ? "该版本还没有安装 Mod。"
                : $"共 {Mods.Count} 个 Mod，{EnabledCount} 个启用" +
                  (IncompatibleCount > 0 ? $"，{IncompatibleCount} 个与当前加载器不匹配" : "");
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

            Updates.Replace(candidates);
            _main.Activities.Finish(activity, ActivityState.Completed);

            if (candidates.Count == 0)
            {
                Status = "已是最新版本，没有可更新的模组。";
                _main.LogsPage.AppendSystem(Status);
                return;
            }

            // 只询问，不自动下载：用户确认后才更新。
            var lines = string.Join(Environment.NewLine, candidates.Take(12).Select(c =>
                $"· {c.Title}  {c.CurrentVersionNumber} → {c.LatestVersionNumber}"));
            var more = candidates.Count > 12 ? $"{Environment.NewLine}…共 {candidates.Count} 项" : "";

            var answer = System.Windows.MessageBox.Show(
                $"发现 {candidates.Count} 个可更新项：{Environment.NewLine}{Environment.NewLine}{lines}{more}" +
                $"{Environment.NewLine}{Environment.NewLine}现在开始下载更新吗？（旧文件会先备份）",
                "检查更新",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                Status = $"已取消更新（发现 {candidates.Count} 项，未下载任何文件）。";
                _main.LogsPage.AppendSystem(Status);
                return;
            }

            await ApplyUpdatesAsync(instance, manifest, candidates).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _main.Activities.Finish(activity, ActivityState.Failed);
            Status = "检查更新失败: " + ex.Message;
            _main.LogsPage.AppendSystem(Status);
        }
    }

    /// <summary>按确认后的列表逐个更新：备份旧文件 → 下载新文件 → 更新清单。</summary>
    private async Task ApplyUpdatesAsync(Instance instance, ContentManifest manifest, IReadOnlyList<ModUpdateCandidate> candidates)
    {
        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var backupDir = Path.Combine(instance.GameDirectory, "backups", "mod-updates", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);

        var activity = _main.Activities.Begin("更新模组", $"{candidates.Count} 项");
        var done = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                var oldPath = Path.Combine(modsDirectory, candidate.CurrentFileName);
                if (File.Exists(oldPath))
                {
                    File.Copy(oldPath, Path.Combine(backupDir, candidate.CurrentFileName), overwrite: true);
                }

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
                _main.Activities.Report(activity, done * 100.0 / candidates.Count,
                    $"已更新 {candidate.Title}", "");
            }
            catch (Exception ex)
            {
                _main.LogsPage.AppendSystem($"更新 {candidate.Title} 失败: {ex.Message}");
            }
        }

        await manifest.SaveAsync().ConfigureAwait(true);
        _main.Activities.Finish(activity, done == candidates.Count ? ActivityState.Completed : ActivityState.Failed);

        Status = $"已更新 {done}/{candidates.Count} 个模组，旧文件备份在 {backupDir}";
        _main.LogsPage.AppendSystem(Status);
        await LoadModsAsync().ConfigureAwait(true);
    }
}
