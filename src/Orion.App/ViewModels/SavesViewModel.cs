using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orion.App.Mvvm;
using Orion.Core.Instances;

namespace Orion.App.ViewModels;

/// <summary>
/// 存档与截图管理：列出世界（读 level.dat 的名字/版本/种子/最后游玩）、备份成 zip、恢复、删除，
/// 以及截图缩略图列表。
/// </summary>
public sealed class SavesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _tabIndex;
    private Instance? _selectedInstance;
    private WorldRow? _selectedWorld;
    private string _status = "选择版本后可以管理该版本的存档与截图。";
    private bool _isBusy;

    public SavesViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder("saves"));
        OpenScreenshotFolderCommand = new RelayCommand(() => OpenFolder("screenshots"));
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        BackupWorldCommand = new AsyncRelayCommand(() => BackupWorldAsync(SelectedWorld), () => SelectedWorld is not null && !IsBusy);
        DeleteWorldCommand = new AsyncRelayCommand(() => DeleteWorldAsync(SelectedWorld), () => SelectedWorld is not null && !IsBusy);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !IsBusy);
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key && int.TryParse(key, out var index)) TabIndex = index;
        });
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenScreenshotFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand BackupWorldCommand { get; }
    public ICommand DeleteWorldCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand SelectTabCommand { get; }

    public ObservableList<WorldRow> Worlds { get; } = [];
    public ObservableList<SaveScreenshotRow> Screenshots { get; } = [];
    public IReadOnlyList<string> Tabs { get; } = ["存档", "截图"];

    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(IsWorldsTab));
            OnPropertyChanged(nameof(IsScreenshotsTab));
        }
    }

    public bool IsWorldsTab => TabIndex == 0;
    public bool IsScreenshotsTab => TabIndex == 1;

    public ObservableList<Instance> Instances => _main.Instances;

    public Instance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value)) return;
            OnPropertyChanged(nameof(InstanceLabel));
            _ = RefreshAsync();
        }
    }

    public WorldRow? SelectedWorld
    {
        get => _selectedWorld;
        set
        {
            if (!SetProperty(ref _selectedWorld, value)) return;
            ((AsyncRelayCommand)BackupWorldCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeleteWorldCommand).RaiseCanExecuteChanged();
        }
    }

    public string InstanceLabel => SelectedInstance is null
        ? "未选择版本"
        : $"{SelectedInstance.Name} · {SelectedInstance.GameVersion}";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)BackupWorldCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeleteWorldCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)RestoreBackupCommand).RaiseCanExecuteChanged();
        }
    }

    public string WorldsCountText => $"共 {Worlds.Count} 个世界";
    public string ScreenshotsCountText => $"共 {Screenshots.Count} 张截图";

    public async Task RefreshAsync()
    {
        var instance = SelectedInstance ?? _main.CurrentInstance;
        if (instance is null)
        {
            Worlds.Clear();
            Screenshots.Clear();
            Status = "请先创建或选择一个版本。";
            return;
        }

        SelectedInstance = instance;
        IsBusy = true;

        try
        {
            var game = instance.GameDirectory;

            var (worlds, shots) = await Task.Run(() =>
                (WorldService.Scan(game), ScreenshotService.Scan(game))).ConfigureAwait(true);

            var previous = SelectedWorld?.Entry.Directory;
            Worlds.Replace(worlds.Select(w => new WorldRow(w, this)));
            Screenshots.Replace(shots.Select(s => new SaveScreenshotRow(s, this)));

            SelectedWorld = Worlds.FirstOrDefault(w => w.Entry.Directory == previous) ?? Worlds.FirstOrDefault();

            OnPropertyChanged(nameof(WorldsCountText));
            OnPropertyChanged(nameof(ScreenshotsCountText));

            Status = $"世界 {Worlds.Count} 个 · 截图 {Screenshots.Count} 张";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "读取存档失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task BackupWorldAsync(WorldRow? row)
    {
        var instance = SelectedInstance;
        if (instance is null || row is null) return;

        IsBusy = true;
        try
        {
            var root = BackupRoot(instance);
            var result = await WorldService.BackupAsync(instance.GameDirectory, row.Entry.Directory, root)
                .ConfigureAwait(true);

            Status = $"已备份「{result.WorldName}」→ {result.ZipPath}（{result.SizeText}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "备份失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteWorldAsync(WorldRow? row)
    {
        var instance = SelectedInstance;
        if (instance is null || row is null) return;

        try
        {
            WorldService.Delete(row.Entry.Directory);
            Status = $"已把「{row.Entry.Name}」移到回收站（可从资源管理器还原）。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "删除失败: " + ex.Message;
        }
    }

    /// <summary>从备份目录挑一个 zip 恢复；恢复前会把现有的同名世界另存一份。</summary>
    public async Task RestoreBackupAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var root = BackupRoot(instance);
        Directory.CreateDirectory(root);

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要恢复的存档备份",
            InitialDirectory = root,
            Filter = "存档备份 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var target = await WorldService.RestoreAsync(instance.GameDirectory, dialog.FileName).ConfigureAwait(true);
            Status = $"已恢复到 {target}（原世界已另存为 *.before-restore-*）";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "恢复失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteScreenshotAsync(SaveScreenshotRow? row)
    {
        if (row is null) return;

        try
        {
            ScreenshotService.Delete(row.Entry.Path);
            Status = $"已删除截图 {row.Entry.FileName}（进回收站）。";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "删除截图失败: " + ex.Message;
        }
    }

    public void OpenScreenshot(SaveScreenshotRow? row)
    {
        if (row is null) return;
        OpenPath(row.Entry.Path);
    }

    public void SyncInstance()
    {
        if (_main.CurrentInstance is { } instance && !ReferenceEquals(SelectedInstance, instance))
        {
            SelectedInstance = instance;
        }
    }

    internal string BackupRoot(Instance instance) =>
        Path.Combine(_main.Context.Paths.Root, "backups", "worlds", instance.Id);

    private void OpenBackupFolder()
    {
        var instance = SelectedInstance;
        if (instance is null) return;
        OpenFolder(Path.Combine(_main.Context.Paths.Root, "backups", "worlds", instance.Id), create: true);
    }

    private void OpenFolder(string relative) => OpenFolder(relative, create: true, relative: true);

    private void OpenFolder(string path, bool create, bool relative = false)
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var full = relative ? Path.Combine(instance.GameDirectory, path) : path;

        try
        {
            if (create) Directory.CreateDirectory(full);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "无法打开目录: " + ex.Message;
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "无法打开文件: " + ex.Message;
        }
    }
}

/// <summary>世界列表的一行。</summary>
public sealed class WorldRow
{
    private readonly SavesViewModel _owner;

    public WorldRow(WorldEntry entry, SavesViewModel owner)
    {
        Entry = entry;
        _owner = owner;

        BackupCommand = new AsyncRelayCommand(() => _owner.BackupWorldAsync(this));
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteWorldAsync(this));
        OpenFolderCommand = new RelayCommand(() => _ = OpenAsync(entry.Directory));
    }

    public WorldEntry Entry { get; }

    public string Name => Entry.Name;
    public string FolderName => Entry.FolderName;
    public string VersionText => Entry.GameVersion is null ? "版本未知" : "MC " + Entry.GameVersion;
    public string GameTypeText => Entry.GameType ?? "";
    public bool HasGameType => !string.IsNullOrWhiteSpace(Entry.GameType);
    public string LastPlayedText => "最后游玩 " + Entry.LastPlayedText;
    public string SizeText => Entry.SizeText;
    public string SeedText => Entry.SeedText.Length == 0 ? "" : "种子 " + Entry.SeedText;
    public bool HasSeed => Entry.Seed is not null;

    public ICommand BackupCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }

    private static Task OpenAsync(string directory)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        return Task.CompletedTask;
    }
}

/// <summary>截图列表的一行（缩略图懒加载到 320px 宽）。</summary>
public sealed class SaveScreenshotRow
{
    private readonly SavesViewModel _owner;
    private ImageSource? _thumbnail;

    public SaveScreenshotRow(ScreenshotEntry entry, SavesViewModel owner)
    {
        Entry = entry;
        _owner = owner;

        OpenCommand = new RelayCommand(() => _owner.OpenScreenshot(this));
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteScreenshotAsync(this));
    }

    public ScreenshotEntry Entry { get; }

    public string FileName => Entry.FileName;
    public string Dimensions => Entry.Dimensions;
    public string TakenAtText => Entry.TakenAtText;
    public string SizeText => Entry.SizeText;

    /// <summary>缩略图：按 320px 宽解码，避免一屏几十张 4K 截图把内存打爆。</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is not null) return _thumbnail;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 320;
                image.UriSource = new Uri(Entry.Path);
                image.EndInit();
                image.Freeze();
                _thumbnail = image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                _thumbnail = null;
            }

            return _thumbnail;
        }
    }

    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }
}
