using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core;
using Orion.Core.Instances;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>
/// 资源包 / 光影包 / 数据包的本地管理。
///
/// 三者启用机制不同，这里如实分开处理：
/// 资源包写 options.txt 的 resourcePacks（顺序即优先级）；光影写 Iris/Oculus 的 shaderPack；
/// 数据包要改世界 NBT，风险太高，只提供列出 / 打开 / 删除。
/// </summary>
public sealed class ContentViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _tabIndex;
    private Instance? _selectedInstance;
    private string _status = "选择版本后可以管理该版本的资源包、光影和数据包。";
    private bool _isBusy;

    public ContentViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        OpenFolderCommand = new RelayCommand(parameter => OpenFolder(parameter as string));
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key && int.TryParse(key, out var index)) TabIndex = index;
        });
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SelectTabCommand { get; }

    public ObservableList<ContentPackRow> ResourcePacks { get; } = [];
    public ObservableList<ContentPackRow> ShaderPacks { get; } = [];
    public ObservableList<DatapackRow> Datapacks { get; } = [];

    public IReadOnlyList<string> Tabs { get; } = ["资源包", "光影", "数据包"];

    /// <summary>0 = 资源包，1 = 光影，2 = 数据包。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, Math.Clamp(value, 0, 2))) return;
            OnPropertyChanged(nameof(IsPacksTab));
            OnPropertyChanged(nameof(IsShadersTab));
            OnPropertyChanged(nameof(IsDatapacksTab));
        }
    }

    public bool IsPacksTab => TabIndex == 0;
    public bool IsShadersTab => TabIndex == 1;
    public bool IsDatapacksTab => TabIndex == 2;

    public ObservableList<Instance> Instances => _main.Instances;

    public Instance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value)) return;
            OnPropertyChanged(nameof(InstanceLabel));
            OnPropertyChanged(nameof(ShaderHint));
            OnPropertyChanged(nameof(ShaderPath));
            _ = RefreshAsync();
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

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    // ── 说明文案（如实告诉用户为什么数据包不能在这里启用） ─────────────────
    public string PacksHint => "勾选后按列表顺序生效：靠上的优先。写回前会自动备份 options.txt。";

    public string DatapacksHint =>
        "数据包是否启用记录在世界档案里（level.dat），改它属于高危写入，所以这里只提供列出、打开和删除；" +
        "启用/禁用请进游戏的世界「数据包」菜单。";

    public string ShaderPath => SelectedInstance is null
        ? ""
        : ContentInventoryService.ShaderPropertiesPath(SelectedInstance.GameDirectory) ?? "未检测到 Iris/Oculus 配置文件";

    public string ShaderHint => ContentInventoryService.ShaderPropertiesPath(
        SelectedInstance?.GameDirectory ?? "") is null
        ? "这个版本没有 Iris/Oculus 的配置文件，光影只能看列表和删文件；装好 Iris/Oculus 并启动一次后就能在这里切换。"
        : "选择后写入 config 里的 shaderPack，需要重启游戏才生效。";

    // ── 操作 ────────────────────────────────────────────────────────────────
    public async Task RefreshAsync()
    {
        var instance = SelectedInstance ?? _main.CurrentInstance;
        if (instance is null)
        {
            ResourcePacks.Clear();
            ShaderPacks.Clear();
            Datapacks.Clear();
            Status = "请先创建或选择一个版本。";
            return;
        }

        SelectedInstance = instance;
        IsBusy = true;

        try
        {
            var game = instance.GameDirectory;

            var packs = await Task.Run(() => ContentInventoryService.ScanResourcePacks(game)).ConfigureAwait(true);
            var shaders = await Task.Run(() => ContentInventoryService.ScanShaderPacks(game)).ConfigureAwait(true);
            var datapacks = await Task.Run(() => ContentInventoryService.ScanDatapacks(game)).ConfigureAwait(true);

            ResourcePacks.Replace(packs.Select(p => new ContentPackRow(p, this, ContentKind.ResourcePack)));
            ShaderPacks.Replace(shaders.Select(p => new ContentPackRow(p, this, ContentKind.ShaderPack)));
            Datapacks.Replace(datapacks.Select(d => new DatapackRow(d, this)));

            OnPropertyChanged(nameof(ShaderPath));
            OnPropertyChanged(nameof(ShaderHint));

            Status = $"资源包 {ResourcePacks.Count} 个（启用 {ResourcePacks.Count(p => p.Enabled)}）· " +
                     $"光影 {ShaderPacks.Count} 个（启用 {ShaderPacks.Count(p => p.Enabled)}）· " +
                     $"数据包 {Datapacks.Count} 个";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "读取资源目录失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把当前列表里的启用状态（按顺序）写回 options.txt。</summary>
    public async Task ApplyResourcePackOrderAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var enabled = ResourcePacks.Where(p => p.Enabled).Select(p => p.FileName).ToList();

        try
        {
            await ContentInventoryService
                .SetResourcePacksAsync(instance.GameDirectory, enabled)
                .ConfigureAwait(true);

            Status = enabled.Count == 0
                ? "已关闭全部资源包（options.txt 已更新，并备份为 .orap.bak）。"
                : $"已写入 {enabled.Count} 个资源包，顺序：{string.Join(" → ", enabled)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "写入 options.txt 失败: " + ex.Message;
        }
    }

    public async Task SetShaderAsync(ContentPackRow row)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        if (ContentInventoryService.ShaderPropertiesPath(instance.GameDirectory) is null)
        {
            Status = "这个版本没有 Iris/Oculus 配置文件，无法切换光影。";
            return;
        }

        // ToggleAsync 先把 row.Enabled 切成"目标状态"：要启用就写它，否则写关闭标记
        var target = row.Enabled ? row.FileName : null;

        try
        {
            await ContentInventoryService.SetShaderPackAsync(instance.GameDirectory, target).ConfigureAwait(true);
            Status = target is null ? "已关闭光影。" : $"已切换到光影：{row.DisplayName}（重启游戏生效）";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "写入光影配置失败: " + ex.Message;
        }
    }

    public async Task MoveAsync(ContentPackRow row, int delta)
    {
        var index = ResourcePacks.IndexOf(row);
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= ResourcePacks.Count) return;

        ResourcePacks.Move(index, target);
        await ApplyResourcePackOrderAsync().ConfigureAwait(true);
    }

    public async Task ToggleAsync(ContentPackRow row)
    {
        row.Enabled = !row.Enabled;

        if (row.Kind == ContentKind.ShaderPack)
        {
            await SetShaderAsync(row).ConfigureAwait(true);
            return;
        }

        await ApplyResourcePackOrderAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>删除一个内容包：先移到启动器回收目录，再从列表中移除。</summary>
    public async Task DeleteAsync(ContentPackRow row)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var trash = Path.Combine(_main.Context.Paths.Root, "trash", "content", instance.Id);
        Directory.CreateDirectory(trash);

        try
        {
            var destination = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(row.FileName)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(row.FileName)}");
            if (Directory.Exists(row.FilePath)) Directory.Move(row.FilePath, destination);
            else File.Move(row.FilePath, destination);

            Status = $"已把 {row.FileName} 移到回收目录（{trash}）。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "删除失败: " + ex.Message;
        }
    }

    public async Task DeleteDatapackAsync(DatapackRow row)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var trash = Path.Combine(_main.Context.Paths.Root, "trash", "content", instance.Id);
        Directory.CreateDirectory(trash);

        try
        {
            var destination = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(row.FileName)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(row.FileName)}");
            if (Directory.Exists(row.FilePath)) Directory.Move(row.FilePath, destination);
            else File.Move(row.FilePath, destination);

            Status = $"已移除数据包 {row.FileName}（移到回收目录）。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "删除失败: " + ex.Message;
        }
    }

    // ── 打开目录 ────────────────────────────────────────────────────────────
    private void OpenFolder(string? kind)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var relative = kind switch
        {
            "shaders" => ContentInventoryService.ShaderPacksDir,
            "datapacks" => ContentInventoryService.SavesDir,
            "config" => "config",
            _ => ContentInventoryService.ResourcePacksDir,
        };

        var path = Path.Combine(instance.GameDirectory, relative);
        Directory.CreateDirectory(path);

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "无法打开目录: " + ex.Message;
        }
    }

    /// <summary>跟随全局当前版本。</summary>
    public void SyncInstance()
    {
        if (_main.CurrentInstance is { } instance && !ReferenceEquals(SelectedInstance, instance))
        {
            SelectedInstance = instance;
        }
    }
}

public enum ContentKind
{
    ResourcePack,
    ShaderPack,
}

/// <summary>资源包 / 光影列表里的一行。</summary>
public sealed class ContentPackRow : ObservableObject
{
    private readonly ContentViewModel _owner;
    private bool _enabled;

    public ContentPackRow(ContentPackEntry entry, ContentViewModel owner, ContentKind kind)
    {
        Entry = entry;
        _owner = owner;
        Kind = kind;
        _enabled = entry.Enabled;

        ToggleCommand = new AsyncRelayCommand(() => _owner.ToggleAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteAsync(this));
        MoveUpCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, -1));
        MoveDownCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, 1));
    }

    public ContentPackEntry Entry { get; }
    public ContentKind Kind { get; }

    public string FileName => Entry.FileName;
    public string FilePath => Entry.FilePath;
    public string DisplayName => Entry.DisplayName;
    public string Description => Entry.Description ?? "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);
    public string SizeText => Entry.SizeText;
    public string FormatText => Entry.Format is null ? "" : $"格式 {Entry.Format}";
    public bool IsDirectory => Entry.IsDirectory;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    public string ToggleLabel => Kind == ContentKind.ShaderPack
        ? (Enabled ? "使用中" : "启用")
        : (Enabled ? "已启用" : "启用");

    public string ToggleOnLabel => "已启用";
    public string ToggleOffLabel => "启用";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
}

/// <summary>数据包列表里的一行（只读 + 删除）。</summary>
public sealed class DatapackRow
{
    private readonly ContentViewModel _owner;

    public DatapackRow(DatapackEntry entry, ContentViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteDatapackAsync(this));
    }

    public DatapackEntry Entry { get; }

    public string FileName => Entry.FileName;
    public string FilePath => Entry.FilePath;
    public string WorldName => Entry.WorldName;
    public string SizeText => Entry.SizeBytes > 1024 * 1024
        ? $"{Entry.SizeBytes / 1024.0 / 1024:0.0} MB"
        : $"{Entry.SizeBytes / 1024.0:0} KB";

    public ICommand DeleteCommand { get; }
}
