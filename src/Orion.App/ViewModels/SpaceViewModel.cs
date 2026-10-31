using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Instances;

namespace Orion.App.ViewModels;

/// <summary>
/// 空间页：共享目录 + 每个实例的占用分类，以及白名单清理（日志 / 崩溃报告 / 缓存 / 回收站）。
/// 删除一律走回收站，且 saves / mods / 资源包 只在统计里出现，不提供删除入口。
/// </summary>
public sealed class SpaceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private InstanceUsage? _selectedUsage;
    private string _status = "点「重新扫描」统计占用。清理只删日志、崩溃报告、缓存和回收站。";
    private bool _isBusy;
    private int _logRetentionDays = 7;
    private bool _cleanLogs = true;
    private bool _cleanCrashReports = true;
    private bool _cleanCache = true;
    private bool _cleanTrash;
    private CleanupPreview? _preview;

    public SpaceViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => SelectedUsage is not null && !IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => Preview is { IsEmpty: false } && !IsBusy);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.Root));
        OpenInstanceFolderCommand = new RelayCommand(() =>
        {
            if (SelectedUsage is not null) OpenFolder(SelectedUsage.GameDirectory);
        });
    }

    public ICommand RefreshCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenInstanceFolderCommand { get; }

    public ObservableList<InstanceUsage> Usages { get; } = [];
    public ObservableList<UsageSliceRow> Slices { get; } = [];
    public ObservableList<SharedUsageRow> SharedRows { get; } = [];

    public InstanceUsage? SelectedUsage
    {
        get => _selectedUsage;
        set
        {
            if (!SetProperty(ref _selectedUsage, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedTotalText));
            Preview = null;
            ((AsyncRelayCommand)PreviewCommand).RaiseCanExecuteChanged();
            _ = LoadSlicesAsync();
        }
    }

    public bool HasSelection => SelectedUsage is not null;

    public string SelectedTitle => SelectedUsage?.Name ?? "未选择实例";

    public string SelectedTotalText => SelectedUsage is null ? "" : $"共 {SelectedUsage.SizeText}";

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
            ((AsyncRelayCommand)PreviewCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)CleanCommand).RaiseCanExecuteChanged();
        }
    }

    public int LogRetentionDays
    {
        get => _logRetentionDays;
        set
        {
            if (SetProperty(ref _logRetentionDays, Math.Clamp(value, 0, 90))) Preview = null;
        }
    }

    public bool CleanLogs
    {
        get => _cleanLogs;
        set { if (SetProperty(ref _cleanLogs, value)) Preview = null; }
    }

    public bool CleanCrashReports
    {
        get => _cleanCrashReports;
        set { if (SetProperty(ref _cleanCrashReports, value)) Preview = null; }
    }

    public bool CleanCache
    {
        get => _cleanCache;
        set { if (SetProperty(ref _cleanCache, value)) Preview = null; }
    }

    public bool CleanTrash
    {
        get => _cleanTrash;
        set { if (SetProperty(ref _cleanTrash, value)) Preview = null; }
    }

    public CleanupPreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value)) return;
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(HasPreview));
            ((AsyncRelayCommand)CleanCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasPreview => Preview is not null;

    public string PreviewText => Preview switch
    {
        null => "勾选要清理的内容，然后点「预览」。",
        { IsEmpty: true } => "按当前选项没有可清理的内容。",
        _ => $"将删除 {Preview.FileCount} 个文件，释放 {Preview.SizeText}（清理只删白名单内容）",
    };

    public string SharedTotalText
    {
        get
        {
            var total = SharedRows.Sum(r => r.Bytes);
            return $"共享目录共 {DiskUsageService.FormatBytes(total)}";
        }
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;

        try
        {
            var instances = _main.Instances.ToList();
            var paths = _main.Context.Paths;

            var (usages, shared) = await Task.Run(() =>
            {
                var measured = DiskUsageService.MeasureAll(instances);
                var sharedUsage = DiskUsageService.MeasureShared(paths);
                return (measured, sharedUsage);
            }).ConfigureAwait(true);

            Usages.Replace(usages);

            SharedRows.Replace(new List<SharedUsageRow>
            {
                new("版本 JSON / jar", "versions", shared.VersionsBytes),
                new("库文件", "libraries", shared.LibrariesBytes),
                new("游戏资源", "assets", shared.AssetsBytes),
                new("Java 运行时", "runtime", shared.RuntimeBytes),
                new("下载缓存", "cache", shared.CacheBytes, true),
                new("启动器日志", "logs", shared.LogsBytes, true),
                new("回收目录", "trash", shared.TrashBytes, true),
            });

            OnPropertyChanged(nameof(SharedTotalText));

            if (SelectedUsage is null || usages.All(u => u.InstanceId != SelectedUsage.InstanceId))
            {
                SelectedUsage = Usages.FirstOrDefault();
            }

            Preview = null;
            Status = $"已统计 {usages.Count} 个实例，共享目录 {DiskUsageService.FormatBytes(shared.Total)}。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "统计失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task LoadSlicesAsync()
    {
        if (SelectedUsage is null)
        {
            Slices.Clear();
            return Task.CompletedTask;
        }

        var total = Math.Max(1, SelectedUsage.TotalBytes);
        Slices.Replace(SelectedUsage.Slices.Select(s => new UsageSliceRow(s, total)));
        return Task.CompletedTask;
    }

    private async Task PreviewAsync()
    {
        var usage = SelectedUsage;
        if (usage is null) return;

        var instance = _main.Instances.FirstOrDefault(i => i.Id == usage.InstanceId);
        if (instance is null) return;

        try
        {
            var options = BuildOptions();
            var paths = _main.Context.Paths;

            Preview = await Task.Run(() => DiskUsageService.Preview(instance, options, paths)).ConfigureAwait(true);

            if (Preview.IsEmpty)
            {
                Status = "按当前选项没有可清理的内容。";
                return;
            }

            Status = $"预览：{Preview.FileCount} 个文件 / {Preview.SizeText}";
            ((AsyncRelayCommand)CleanCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "预览失败: " + ex.Message;
        }
    }

    private async Task CleanAsync()
    {
        var usage = SelectedUsage;
        if (usage is null || Preview is null) return;

        var instance = _main.Instances.FirstOrDefault(i => i.Id == usage.InstanceId);
        if (instance is null) return;

        IsBusy = true;
        var activity = _main.Activities.Begin("清理空间", instance.Name);

        try
        {
            var options = BuildOptions();
            var paths = _main.Context.Paths;

            var result = await Task.Run(() => DiskUsageService.Clean(instance, options, paths)).ConfigureAwait(true);

            var message = $"清理完成：{result.DeletedFiles} 个文件，释放 {result.SizeText}" +
                          (result.FailedFiles > 0 ? $"（{result.FailedFiles} 个删不掉，可能在占用）" : "");

            Status = message;
            _main.Activities.Finish(activity, result.FailedFiles == 0
                ? ActivityState.Completed
                : ActivityState.Failed, message);

            Preview = null;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "清理失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CleanupOptions BuildOptions() => new()
    {
        CleanLogs = CleanLogs,
        LogRetentionDays = LogRetentionDays,
        CleanCrashReports = CleanCrashReports,
        CleanCache = CleanCache,
        CleanTrash = CleanTrash,
    };

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "无法打开目录: " + ex.Message;
        }
    }
}

/// <summary>实例内一个分类的展示行（带占比条）。</summary>
public sealed class UsageSliceRow
{
    public UsageSliceRow(UsageSlice slice, double instanceTotalBytes)
    {
        Slice = slice;

        // 占比是"这一块占实例总占用的百分比"，进度条 Maximum = 100
        Percent = instanceTotalBytes <= 0 ? 0 : Math.Clamp(slice.Bytes * 100.0 / instanceTotalBytes, 0, 100);
    }

    public UsageSlice Slice { get; }

    public string Label => Slice.Label;
    public string SizeText => Slice.SizeText;
    public string FileText => $"{Slice.FileCount} 个文件";
    public bool Cleanable => Slice.Cleanable;
    public double Percent { get; }
    public string CleanableText => Slice.Cleanable ? "可清理" : "";
}

/// <summary>共享目录的一项占用。</summary>
public sealed record SharedUsageRow(string Label, string Key, long Bytes, bool Cleanable = false)
{
    public string SizeText => DiskUsageService.FormatBytes(Bytes);
    public string CleanableText => Cleanable ? "可清理（见下方选项）" : "";
}
