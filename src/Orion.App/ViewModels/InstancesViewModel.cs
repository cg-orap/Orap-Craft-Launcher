using System.Diagnostics;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Instances;

namespace Orion.App.ViewModels;

/// <summary>Instances page: create, configure, verify, repair and remove instances.</summary>
public sealed class InstancesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    private Instance? _selectedInstance;
    private string _newName = "";
    private string _newVersion = "";
    private string _status = "选择一个版本以查看详情。";
    private bool _isBusy;

    private int _memoryMb = 4096;
    private string _jvmArguments = "";
    private string _javaPath = "";
    private int _windowWidth = 854;
    private int _windowHeight = 480;
    private bool _fullscreen;
    private string _displayName = "";

    public InstancesViewModel(MainViewModel main)
    {
        RestoreEntryCommand = new AsyncRelayCommand(RestoreEntryAsync);
        CreateSnapshotCommand = new AsyncRelayCommand(CreateSnapshotAsync);
        RestoreSnapshotCommand = new AsyncRelayCommand(RestoreSnapshotAsync);
        DeleteSnapshotCommand = new RelayCommand(DeleteSnapshot);
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is int index) TabIndex = index;
            else if (parameter is string text && int.TryParse(text, out var parsed)) TabIndex = parsed;
        });
        _main = main;

        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(NewVersion), ex => Status = "创建失败: " + ex.Message);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedInstance is not null && !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => SelectedInstance is not null && !IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => SelectedInstance is not null && !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        BrowseJavaCommand = new RelayCommand(BrowseJava);
        OpenVersionFolderCommand = new RelayCommand(OpenVersionFolder);
        ImportPackCommand = new AsyncRelayCommand(ImportPackAsync, () => !IsBusy);
        ExportPackCommand = new AsyncRelayCommand(ExportPackAsync, () => SelectedInstance is not null && !IsBusy);
    }

    private int _tabIndex;

    /// <summary>详情页当前页签：0 概览 / 1 模组 / 2 存档 / 3 日志 / 4 快照 / 5 设置。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, value)) return;
            OnPropertyChanged(nameof(IsOverviewTab));
            OnPropertyChanged(nameof(IsModsTab));
            OnPropertyChanged(nameof(IsSavesTab));
            OnPropertyChanged(nameof(IsLogsTab));
            OnPropertyChanged(nameof(IsSnapshotTab));
            OnPropertyChanged(nameof(IsSettingsTab));
        }
    }

    public bool IsOverviewTab => _tabIndex == 0;
    public bool IsModsTab => _tabIndex == 1;
    public bool IsSavesTab => _tabIndex == 2;
    public bool IsLogsTab => _tabIndex == 3;
    public bool IsSnapshotTab => _tabIndex == 4;
    public bool IsSettingsTab => _tabIndex == 5;

    public ICommand SelectTabCommand { get; }

    private readonly InstanceBackupService _backups = new();

    /// <summary>该版本的快照列表（saves / config / mods 的副本）。</summary>
    public ObservableList<BackupSnapshot> Snapshots { get; } = [];

    public bool HasSnapshots => Snapshots.Count > 0;

    public string SnapshotsHint => Snapshots.Count == 0 ? "还没有快照。点「创建快照」把当前的存档、配置和模组打包存一份。" : "";

    /// <summary>当前选中的快照（用于浏览内容、单条恢复）。</summary>
    public BackupSnapshot? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (!SetProperty(ref _selectedSnapshot, value)) return;
            OnPropertyChanged(nameof(HasSelectedSnapshot));
            LoadSnapshotEntries();
        }
    }

    private BackupSnapshot? _selectedSnapshot;

    /// <summary>选中快照里可单独恢复的条目：世界 / 配置 / jar。</summary>
    public ObservableList<SnapshotEntry> SnapshotEntries { get; } = [];

    public bool HasSelectedSnapshot => SelectedSnapshot is not null;

    public bool HasSnapshotEntries => SnapshotEntries.Count > 0;

    public string SnapshotEntriesHint
    {
        get
        {
            if (SelectedSnapshot is null) return "选中一份快照可以查看内容，并单独恢复某个世界、配置或模组。";
            return SnapshotEntries.Count == 0 ? "这份快照里没有可恢复的条目。" : "";
        }
    }

    public ICommand RestoreEntryCommand { get; }

    public ICommand CreateSnapshotCommand { get; }
    public ICommand RestoreSnapshotCommand { get; }
    public ICommand DeleteSnapshotCommand { get; }

    public ICommand ImportPackCommand { get; }
    public ICommand ExportPackCommand { get; }

    public ICommand CreateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand BrowseJavaCommand { get; }
    public ICommand OpenVersionFolderCommand { get; }

    public ObservableList<string> VersionOptions { get; } = [];

    public Instance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value)) return;

            _main.CurrentInstance = value;
            LoadFromInstance(value);

            ((AsyncRelayCommand)DeleteCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)SaveCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)VerifyCommand).RaiseCanExecuteChanged();

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionDetail));
              RefreshSnapshots();
        }
    }

    public bool HasSelection => SelectedInstance is not null;

    public string SelectionDetail => SelectedInstance is null
        ? "尚未选择版本"
        : $"{SelectedInstance.Subtitle}\n游戏目录: {SelectedInstance.GameDirectory}"
          + (SelectedInstance.IsExternal ? "\n外部导入版本：不会修改或删除原目录内容。" : "");

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string NewVersion
    {
        get => _newVersion;
        set
        {
            if (!SetProperty(ref _newVersion, value)) return;
            ((AsyncRelayCommand)CreateCommand).RaiseCanExecuteChanged();
        }
    }

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
            ((AsyncRelayCommand)CreateCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeleteCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)SaveCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)VerifyCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)ImportCommand).RaiseCanExecuteChanged();
        }
    }

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public int MemoryMb { get => _memoryMb; set => SetProperty(ref _memoryMb, value); }
    public string JvmArguments { get => _jvmArguments; set => SetProperty(ref _jvmArguments, value); }
    public string JavaPath { get => _javaPath; set => SetProperty(ref _javaPath, value); }
    public int WindowWidth { get => _windowWidth; set => SetProperty(ref _windowWidth, value); }
    public int WindowHeight { get => _windowHeight; set => SetProperty(ref _windowHeight, value); }
    public bool Fullscreen { get => _fullscreen; set => SetProperty(ref _fullscreen, value); }

    public async Task LoadAsync()
    {
        try
        {
            var manifest = await _main.Context.VersionRepository.GetManifestAsync().ConfigureAwait(true);
            VersionOptions.Replace(manifest.Versions
                .Where(v => v.Type is "release" or "snapshot")
                .Select(v => v.Id));

            NewVersion = VersionOptions.FirstOrDefault() ?? "";
        }
        catch (Exception ex)
        {
            Status = "无法读取版本清单: " + ex.Message;
        }

        SelectedInstance = _main.Instances.FirstOrDefault();
    }

    private void LoadFromInstance(Instance? instance)
    {
        if (instance is null) return;

        DisplayName = instance.Name;
        MemoryMb = instance.MemoryMb ?? _main.Context.Settings.MemoryMb;
        JvmArguments = instance.JvmArguments ?? "";
        JavaPath = instance.JavaPath ?? "";
        WindowWidth = instance.WindowWidth;
        WindowHeight = instance.WindowHeight;
        Fullscreen = instance.Fullscreen;
    }

    private async Task CreateAsync()
    {
        var version = NewVersion.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            Status = "请选择要使用的游戏版本。";
            return;
        }

        IsBusy = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(NewName) ? version : NewName.Trim();
            var created = await _main.Context.Instances.CreateAsync(name, version, version).ConfigureAwait(true);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            SelectedInstance = _main.Instances.FirstOrDefault(i => i.Id == created.Id);
            _main.CurrentInstance = SelectedInstance;

            Status = $"已创建版本「{created.Name}」，游戏目录 {created.GameDirectory}";
            NewName = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var hasData = InstanceManager.HasUserData(instance.GameDirectory);
        var message = hasData
            ? $"版本「{instance.Name}」包含存档或 Mod。\n\n删除不会永久抹掉文件：整个版本目录会被放进 Windows 回收站，你可以从资源管理器还原。\n\n是否继续？"
            : $"确定要删除版本「{instance.Name}」吗？\n\n整个版本目录会被放进 Windows 回收站，可从资源管理器还原。";

        if (System.Windows.MessageBox.Show(message, "删除版本", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _main.Context.Instances.Remove(instance, out var trashPath);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);
            SelectedInstance = _main.Instances.FirstOrDefault();

            Status = trashPath is null
                ? $"已解除版本「{instance.Name}」的注册（外部版本，原目录未改动）。"
                : $"已删除版本「{instance.Name}」，整个目录已放进 Windows 回收站：{trashPath}";
        }
        catch (IOException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        instance.Name = string.IsNullOrWhiteSpace(DisplayName) ? instance.Name : DisplayName.Trim();
        instance.MemoryMb = MemoryMb;
        instance.JvmArguments = string.IsNullOrWhiteSpace(JvmArguments) ? null : JvmArguments.Trim();
        instance.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? null : JavaPath.Trim();
        instance.WindowWidth = Math.Clamp(WindowWidth, 320, 7680);
        instance.WindowHeight = Math.Clamp(WindowHeight, 240, 4320);
        instance.Fullscreen = Fullscreen;

        await _main.Context.Instances.SaveAsync(instance).ConfigureAwait(true);
        await _main.ReloadInstancesAsync().ConfigureAwait(true);

        SelectedInstance = _main.Instances.FirstOrDefault(i => i.Id == instance.Id);
        Status = "版本设置已保存。";
    }

    private void OpenFolder()
    {
        var directory = SelectedInstance?.GameDirectory;
        if (directory is null || !Directory.Exists(directory))
        {
            Status = "游戏目录尚不存在。";
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void OpenVersionFolder()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var directory = Path.Combine(_main.Context.Paths.VersionsDir, instance.VersionId);
        if (!Directory.Exists(directory)) directory = _main.Context.Paths.VersionsDir;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
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

    private async Task VerifyAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        IsBusy = true;
        Status = "正在校验游戏文件…";
        var activity = _main.Activities.Begin($"校验 {instance.Name}", instance.VersionId);

        try
        {
            var progress = new Progress<Core.Install.VerifyProgress>(p =>
            {
                Status = $"[{p.Percent:0}%] {p.Message}";
                _main.Activities.Report(activity, p.Fraction, p.Message, "");
            });

            var result = await _main.Context.Integrity.VerifyAsync(instance.VersionId, _main.Context.Mirrors.Resolve, progress).ConfigureAwait(true);

            if (result.IsHealthy)
            {
                Status = $"校验通过：{result.CheckedFiles} 个文件全部完好。";
                _main.Activities.Finish(activity, ActivityState.Completed);
                return;
            }

            Status = $"发现 {result.Missing.Count} 个缺失、{result.Corrupt.Count} 个损坏的文件，正在修复…";
            var repair = await _main.Context.Integrity.RepairAsync(instance.VersionId, _main.Context.Mirrors.Resolve, progress).ConfigureAwait(true);

            Status = repair.Success
                ? $"修复完成：重新下载 {repair.FilesDownloaded} 个文件（{repair.BytesDownloaded / 1024.0 / 1024.0:0.0} MB）。"
                : "修复失败: " + repair.Error;

            _main.Activities.Finish(activity, repair.Success ? ActivityState.Completed : ActivityState.Failed, repair.Error);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "校验失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Installs a Modrinth .mrpack: creates a loader instance and extracts the pack into it.</summary>
    private async Task ImportPackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Modrinth 整合包 (.mrpack)",
            Filter = "Modrinth 整合包|*.mrpack|ZIP 压缩包|*.zip|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Status = "正在读取整合包…";
        var activity = _main.Activities.Begin($"导入整合包 {Path.GetFileName(dialog.FileName)}");

        try
        {
            var java = Core.Java.JavaDetector.SelectFor(21, _main.JavaRuntimes) ?? _main.JavaRuntimes.FirstOrDefault();
            var output = new Progress<string>(line =>
            {
                Status = line;
                _main.Activities.Report(activity, 0.5, line, "");
            });

            var result = await _main.Context.ModPacks.InstallAsync(dialog.FileName, null, java, output).ConfigureAwait(true);

            if (!result.Success)
            {
                Status = "整合包导入失败: " + result.Error;
                _main.Activities.Finish(activity, ActivityState.Failed, result.Error);
                return;
            }

            await _main.ReloadInstancesAsync().ConfigureAwait(true);
            SelectedInstance = _main.Instances.FirstOrDefault(i => i.Id == result.InstanceId);

            Status = $"整合包导入完成：{result.FilesDownloaded} 个文件，{result.OverridesCopied} 个覆盖文件。";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "整合包导入失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportPackAsync()
    {
        var instance = SelectedInstance;
        if (instance is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出为 Modrinth 整合包",
            Filter = "Modrinth 整合包|*.mrpack",
            FileName = $"{SanitizeFileName(instance.Name)}-{instance.GameVersion}.mrpack",
            AddExtension = true,
            DefaultExt = ".mrpack",
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Status = "正在导出整合包…";
        var activity = _main.Activities.Begin($"导出 {instance.Name}");

        try
        {
            var result = await Core.Mods.MrPackExporter.ExportAsync(instance, dialog.FileName, new Core.Mods.MrPackExporter.ExportOptions
            {
                Name = instance.Name,
                VersionId = "1.0.0",
            }).ConfigureAwait(true);

            if (!result.Success)
            {
                Status = "导出失败: " + result.Error;
                _main.Activities.Finish(activity, ActivityState.Failed, result.Error);
                return;
            }

            Status = $"已导出 {result.OverridesCopied} 个文件到 {dialog.FileName}（存档未包含）。";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "导出失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.Length == 0 ? "instance" : builder.ToString();
    }

    private async Task ImportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要导入的 .minecraft 目录，或 Prism / MultiMC 版本目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Status = "正在导入…";

        try
        {
            var result = await _main.Context.Importer.ImportAsync(dialog.FolderName).ConfigureAwait(true);
            await _main.ReloadInstancesAsync().ConfigureAwait(true);

            if (!result.Success)
            {
                Status = "导入失败: " + string.Join("; ", result.Warnings);
                return;
            }

            SelectedInstance = _main.Instances.FirstOrDefault(i => result.Instances.Any(r => r.Id == i.Id));
            _main.CurrentInstance = SelectedInstance;

            Status = $"已从「{result.DetectedSource}」导入 {result.Instances.Count} 个版本（外部版本，不会修改原目录）。";
            foreach (var warning in result.Warnings.Take(3)) Status += " " + warning;
        }
        catch (Exception ex)
        {
            Status = "导入失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
    /// <summary>跟随全局当前版本（侧栏/主页切换版本时同步详情页）。</summary>
    public void SyncSelection()
    {
        if (_main.CurrentInstance is { } instance && !ReferenceEquals(SelectedInstance, instance))
        {
            SelectedInstance = instance;
        }
    }

    public void RefreshSnapshots()
    {
        Snapshots.Clear();
        if (SelectedInstance is { } instance)
        {
            Snapshots.Replace(_backups.List(instance));
        }

        SelectedSnapshot = Snapshots.FirstOrDefault();
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(SnapshotsHint));
    }

    private async Task CreateSnapshotAsync()
    {
        if (SelectedInstance is not { } instance)
        {
            Status = "请先选择一个版本。";
            return;
        }

        var activity = _main.Activities.Begin("创建快照", instance.Name);
        try
        {
            Status = "正在创建快照…";
            var snapshot = await _backups.CreateAsync(instance, "manual").ConfigureAwait(true);
            RefreshSnapshots();
            Status = $"快照已创建：{snapshot.TimeText}（{snapshot.FileCount} 个文件 · {snapshot.SizeText}）";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            _main.Activities.Finish(activity, ActivityState.Failed);
            Status = "创建快照失败: " + ex.Message;
            _main.LogsPage.AppendSystem(Status);
        }
    }

    private async Task RestoreSnapshotAsync(object? parameter)
    {
        if (SelectedInstance is not { } instance || parameter is not BackupSnapshot snapshot) return;

        var answer = System.Windows.MessageBox.Show(
            $"要把「{instance.Name}」恢复到 {snapshot.TimeText} 的快照吗？{Environment.NewLine}{Environment.NewLine}" +
            "当前的 saves / config / mods 会被替换。恢复前会自动把当前状态另存为一份「恢复前」快照，可再恢复回来。",
            "恢复快照",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var activity = _main.Activities.Begin("恢复快照", snapshot.TimeText);
        try
        {
            Status = "正在恢复快照…";
            var safety = await _backups.RestoreAsync(instance, snapshot.Id).ConfigureAwait(true);
            RefreshSnapshots();
            Status = $"已恢复到 {snapshot.TimeText}；恢复前的状态存为 {safety.TimeText}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            _main.Activities.Finish(activity, ActivityState.Failed);
            Status = "恢复快照失败: " + ex.Message;
            _main.LogsPage.AppendSystem(Status);
        }
    }

    private void DeleteSnapshot(object? parameter)
    {
        if (SelectedInstance is not { } instance || parameter is not BackupSnapshot snapshot) return;

        var answer = System.Windows.MessageBox.Show(
            $"删除 {snapshot.TimeText} 的快照（{snapshot.SizeText}）？此操作不可撤销。",
            "删除快照",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        Status = _backups.Delete(instance, snapshot.Id) ? "快照已删除。" : "删除失败：找不到该快照。";
        RefreshSnapshots();
        _main.LogsPage.AppendSystem(Status);
    }
    private void LoadSnapshotEntries()
    {
        SnapshotEntries.Clear();
        if (SelectedInstance is { } instance && SelectedSnapshot is { } snapshot)
        {
            SnapshotEntries.Replace(_backups.ListEntries(instance, snapshot.Id));
        }

        OnPropertyChanged(nameof(HasSnapshotEntries));
        OnPropertyChanged(nameof(SnapshotEntriesHint));
    }

    /// <summary>只恢复快照里的一个世界/配置/jar；覆盖前的当前文件会另存到 backups/manual-restores。</summary>
    private async Task RestoreEntryAsync(object? parameter)
    {
        if (SelectedInstance is not { } instance ||
            SelectedSnapshot is not { } snapshot ||
            parameter is not SnapshotEntry entry)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"只恢复「{entry.RelativePath}」？{Environment.NewLine}{Environment.NewLine}" +
            "该路径会被快照里的版本覆盖，其它存档与配置不受影响；覆盖前的文件会另存到 backups/manual-restores。",
            "单条恢复",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        var activity = _main.Activities.Begin("恢复条目", entry.RelativePath);
        try
        {
            var safety = await Task.Run(() => _backups.RestoreEntry(instance, snapshot.Id, entry.RelativePath)).ConfigureAwait(true);
            RefreshSnapshots();
            LoadSnapshotEntries();
            Status = safety.Length == 0
                ? $"已恢复 {entry.RelativePath}（原本不存在，无需备份）。"
                : $"已恢复 {entry.RelativePath}；原文件另存于 {safety}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            _main.Activities.Finish(activity, ActivityState.Failed);
            Status = "单条恢复失败: " + ex.Message;
            _main.LogsPage.AppendSystem(Status);
        }
    }
}
