using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Orion.App.Mvvm;
using Orion.Core.Instances;

namespace Orion.App.ViewModels;

/// <summary>备份总览的一行：某个实例的某一份快照。</summary>
public sealed class BackupRow(Instance instance, BackupSnapshot snapshot)
{
    public Instance Instance { get; } = instance;

    public BackupSnapshot Snapshot { get; } = snapshot;

    public string InstanceName => Instance.Name;

    public string TimeText => Snapshot.TimeText;

    public string TriggerLabel => Snapshot.TriggerLabel;

    public string SizeText => Snapshot.SizeText;

    public int FileCount => Snapshot.FileCount;

    public string Detail => $"{Snapshot.SizeText} · {Snapshot.FileCount} 个文件";

    public bool IsLatest { get; init; }
}

/// <summary>
/// 备份总览：跨实例列出全部快照，支持导出到指定目录。只读 + 导出，不做云同步
/// （云同步需要账号与后端，这里明确不假装支持）。
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly InstanceBackupService _backups = new();
    private string _status = "";
    private bool _busy;

    public BackupViewModel(MainViewModel main)
    {
        _main = main;

        RefreshCommand = new RelayCommand(() => Refresh());
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);

        Refresh();
    }

    public ObservableList<BackupRow> Rows { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public bool HasRows => Rows.Count > 0;

    public string Summary
    {
        get
        {
            var instances = Rows.Select(r => r.InstanceName).Distinct().Count();
            var bytes = Rows.Sum(r => r.Snapshot.SizeBytes);
            return $"{instances} 个实例 · {Rows.Count} 份快照 · {FormatSize(bytes)}";
        }
    }

    public string EmptyHint => HasRows ? "" : "还没有任何快照。到「版本设置 → 快照」里给某个版本创建第一份。";

    /// <summary>被选中的那一行（导出用）。</summary>
    public BackupRow? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            ((AsyncRelayCommand)ExportCommand).RaiseCanExecuteChanged();
        }
    }

    private BackupRow? _selected;

    public void Refresh()
    {
        var previous = Selected?.Snapshot.Id;
        Rows.Clear();

        foreach (var instance in _main.Instances)
        {
            IReadOnlyList<BackupSnapshot> snapshots;
            try
            {
                snapshots = _backups.List(instance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i < snapshots.Count; i++)
            {
                Rows.Add(new BackupRow(instance, snapshots[i]) { IsLatest = i == 0 });
            }
        }

        Selected = Rows.FirstOrDefault(r => r.Snapshot.Id == previous) ?? Rows.FirstOrDefault();

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private async Task ExportAsync()
    {
        if (Selected is not { } row) return;

        var dialog = new OpenFolderDialog
        {
            Title = "选择快照导出目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            Status = "已取消导出。";
            return;
        }

        Busy = true;
        var activity = _main.Activities.Begin("导出快照", row.InstanceName);
        try
        {
            var zip = await Task.Run(() => _backups.Export(row.Instance, row.Snapshot.Id, dialog.FolderName)).ConfigureAwait(true);
            Status = $"已导出到 {zip}";
            _main.Activities.Finish(activity, ActivityState.Completed);
            _main.LogsPage.AppendSystem(Status);
        }
        catch (Exception ex)
        {
            Status = "导出失败: " + ex.Message;
            _main.Activities.Finish(activity, ActivityState.Failed);
            _main.LogsPage.AppendSystem(Status);
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenFolder()
    {
        var root = _main.Context.Paths.InstancesDir;
        if (Selected is { } row) root = Path.Combine(row.Instance.GameDirectory, "backups", "snapshots");

        Directory.CreateDirectory(root);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
