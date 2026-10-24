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

    private string _status = "选择一个实例以管理 Mod。";
    private string _searchText = "";
    private bool _isBusy;
    private bool _isSearching;
    private Instance? _selectedInstance;

    public ModsViewModel(MainViewModel main)
    {
        _main = main;

        RescanCommand = new AsyncRelayCommand(LoadModsAsync, () => SelectedInstance is not null);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchText) && !IsSearching);
        OpenFolderCommand = new RelayCommand(OpenModsFolder);
        OpenModrinthPageCommand = new RelayCommand(OpenModrinthPage);
    }

    public ICommand RescanCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenModrinthPageCommand { get; }

    public ObservableList<ModRow> Mods { get; } = [];
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
        ? "未选择实例"
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
            Status = "请先创建或选择一个实例。";
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
                ? "该实例还没有安装 Mod。"
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
            Status = "请先选择一个实例。";
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
}
