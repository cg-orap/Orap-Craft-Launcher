using System.IO;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Diagnostics;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>One search hit in the resource catalogue (modpack or mod).</summary>
public sealed class CatalogRow(ModrinthProject project, CatalogViewModel owner, string kind)
{
    public ModrinthProject Project { get; } = project;

    public string Title => string.IsNullOrWhiteSpace(Project.Title) ? Project.Slug : Project.Title;

    public string Description => Project.Description;

    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "未知作者" : Project.Author;

    public string DownloadsText => Project.Downloads > 0 ? $"{Project.Downloads:N0} 次下载" : "";

    public string Kind => kind;

    public string KindLabel => kind == CatalogViewModel.ModpackKind ? "整合包" : "模组";

    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallAsync(this));
}

/// <summary>
/// 资源下载: browse and install Modrinth modpacks and mods.
///
/// Modpacks are installed through <see cref="MrPackInstaller"/> (which creates a new version from the
/// pack's own loader dependencies); mods are downloaded straight into the selected version's
/// <c>mods</c> directory after Modrinth filtering by that version's game version and loader.
/// </summary>
public sealed class CatalogViewModel : ObservableObject
{
    public const string ModpackKind = "modpack";
    public const string ModKind = "mod";

    private readonly MainViewModel _main;

    private string _kind = ModpackKind;
    private string _searchText = "";
    private string _status = "";
    private bool _busy;

    public CatalogViewModel(MainViewModel main)
    {
        _main = main;

        SearchCommand = new AsyncRelayCommand(() => SearchAsync(), () => !_busy);
        SearchModpacksCommand = new AsyncRelayCommand(() => SwitchKindAsync(ModpackKind), () => !_busy);
        SearchModsCommand = new AsyncRelayCommand(() => SwitchKindAsync(ModKind), () => !_busy);
    }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand SearchModpacksCommand { get; }
    public AsyncRelayCommand SearchModsCommand { get; }

    public ObservableList<CatalogRow> Results { get; } = [];

    public string Kind
    {
        get => _kind;
        private set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsModpackTab));
            OnPropertyChanged(nameof(IsModTab));
            OnPropertyChanged(nameof(KindHint));
        }
    }

    public bool IsModpackTab
    {
        get => _kind == ModpackKind;
        set
        {
            if (value && _kind != ModpackKind) _ = SwitchKindAsync(ModpackKind);
        }
    }

    public bool IsModTab
    {
        get => _kind == ModKind;
        set
        {
            if (value && _kind != ModKind) _ = SwitchKindAsync(ModKind);
        }
    }

    public string KindHint => _kind == ModpackKind
        ? "整合包会安装为一个新的版本（含它自带的加载器与模组），不会影响现有版本。"
        : "模组会安装到当前选中版本的 mods 目录；Modrinth 结果已按该版本的 Minecraft 版本与加载器过滤。";

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
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
            SearchModpacksCommand.RaiseCanExecuteChanged();
            SearchModsCommand.RaiseCanExecuteChanged();
        }
    }

    public string TargetVersionName => _main.CurrentInstance?.Name ?? "（未选择版本）";

    public async Task InitializeAsync()
    {
        if (Results.Count == 0) await SearchAsync().ConfigureAwait(true);
    }

    private async Task SwitchKindAsync(string kind)
    {
        if (_kind == kind) return;

        Kind = kind;
        Results.Clear();
        await SearchAsync().ConfigureAwait(true);
    }

    /// <summary>Runs a Modrinth search for the active category.</summary>
    public async Task SearchAsync()
    {
        if (_busy) return;

        Busy = true;
        Results.Clear();
        Status = _kind == ModpackKind ? "正在搜索整合包…" : "正在搜索模组…";

        try
        {
            var query = new ModrinthSearchQuery
            {
                Query = SearchText.Trim(),
                ProjectType = _kind,
                Limit = 24,
                SortBy = string.IsNullOrWhiteSpace(SearchText) ? "downloads" : "relevance",
            };

            // Mods must match the selected version, otherwise the install would be useless.
            if (_kind == ModKind && _main.CurrentInstance is { } instance)
            {
                query.GameVersion = string.IsNullOrWhiteSpace(instance.GameVersion) ? null : instance.GameVersion;
                if (instance.Loader != Core.Instances.LoaderKind.Vanilla) query.Loader = instance.Loader;
            }

            var result = await _main.Context.Modrinth.SearchAsync(query).ConfigureAwait(true);

            foreach (var project in result.Hits) Results.Add(new CatalogRow(project, this, _kind));

            Status = Results.Count == 0
                ? "没有找到结果，换个关键词试试。"
                : $"共 {result.TotalHits:N0} 个结果，显示前 {Results.Count} 个。";

            OnPropertyChanged(nameof(TargetVersionName));
            OnPropertyChanged(nameof(KindHint));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Status = "搜索失败：" + ex.Message;
            AppLog.Warn("catalog", "search-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Installs one hit: a modpack as a new version, or a mod into the current version.</summary>
    public async Task InstallAsync(CatalogRow row)
    {
        if (row.Kind == ModpackKind) await InstallModpackAsync(row).ConfigureAwait(true);
        else await InstallModAsync(row).ConfigureAwait(true);
    }

    private async Task InstallModpackAsync(CatalogRow row)
    {
        Busy = true;

        try
        {
            Status = $"正在获取整合包 {row.Title} 的版本列表…";

            var versions = await _main.Context.Modrinth.GetVersionsAsync(row.Project.Identifier).ConfigureAwait(true);
            var version = versions.FirstOrDefault(v => v.PrimaryFile is not null);

            if (version?.PrimaryFile is null)
            {
                Status = "该整合包没有可下载的文件。";
                return;
            }

            Status = $"正在下载 {version.PrimaryFile.Filename}…";

            var temp = Path.Combine(_main.Context.Paths.CacheDir, "packs");
            Directory.CreateDirectory(temp);
            var target = Path.Combine(temp, version.PrimaryFile.Filename);

            if (!File.Exists(target))
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(version.PrimaryFile.Url).ConfigureAwait(true);
                await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(true);
            }

            Status = "正在安装整合包（会创建新版本）…";
            var activity = _main.Activities.Begin($"安装整合包 {row.Title}", version.PrimaryFile.Filename);
            var progress = new Progress<string>(line => Status = line);

            var result = await _main.Context.ModPacks
                .InstallAsync(target, row.Title, null, progress)
                .ConfigureAwait(true);

            _main.Activities.Finish(activity, result.Success ? ActivityState.Completed : ActivityState.Failed, result.Error);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            Status = result.Success
                ? $"整合包已安装：{row.Title}（版本 {result.VersionId}，下载 {result.FilesDownloaded} 个文件）。"
                : "整合包安装失败：" + (result.Error ?? "未知错误");

            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = "整合包安装失败：" + ex.Message;
            AppLog.Warn("catalog", "modpack-install-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallModAsync(CatalogRow row)
    {
        if (_main.CurrentInstance is not { } instance)
        {
            Status = "请先在「版本管理」中创建或选择一个版本，再安装模组。";
            return;
        }

        Busy = true;

        try
        {
            Status = $"正在获取 {row.Title} 的版本列表…";

            var versions = await _main.Context.Modrinth
                .GetVersionsAsync(row.Project.Identifier, instance.GameVersion, instance.Loader)
                .ConfigureAwait(true);

            var version = versions.FirstOrDefault(v => v.PrimaryFile is not null);
            if (version is null)
            {
                Status = $"该模组没有适配 {instance.GameVersion} / {instance.DisplayLoader} 的文件。";
                return;
            }

            Status = $"正在下载 {version.PrimaryFile!.Filename}…";

            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            var installed = await _main.Context.Modrinth.InstallAsync(version, modsDirectory).ConfigureAwait(true);

            Status = $"已安装到 {Path.GetFileName(installed)}（{instance.Name}）";
            _main.LogsPage.AppendSystem($"已安装模组 {row.Title} → {installed}");

            await _main.ModsPage.LoadModsAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Status = "模组安装失败：" + ex.Message;
            AppLog.Warn("catalog", "mod-install-failed", ("error", ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }
}
