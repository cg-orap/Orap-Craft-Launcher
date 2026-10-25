using System.Diagnostics;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Net;

namespace Orion.App.ViewModels;

/// <summary>Download manager page: live jobs, throughput and error reports.</summary>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _maxConcurrency;
    private string _status = "";

    public DownloadsViewModel(MainViewModel main)
    {
        _main = main;
        _maxConcurrency = main.Context.Settings.MaxConcurrency;

        SaveConcurrencyCommand = new AsyncRelayCommand(SaveConcurrencyAsync);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_main.Context.Paths.LogsDir));
        OpenDownloadsFolderCommand = new RelayCommand(() => OpenFolder(Path.Combine(_main.Context.Paths.Root, "cache")));
        ClearFinishedCommand = new RelayCommand(ClearFinished);
        CancelTaskCommand = new RelayCommand(CancelTask);
        RefreshCommand = new RelayCommand(() => OnPropertyChanged(nameof(Items)));
    }

    public ICommand SaveConcurrencyCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenDownloadsFolderCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand CancelTaskCommand { get; }
    public ICommand RefreshCommand { get; }

    public ObservableList<ActivityItem> Items => _main.Activities.Items;

    public string StatusText => _main.Activities.Status;

    public bool IsBusy => _main.Activities.IsBusy;

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => SetProperty(ref _maxConcurrency, Math.Clamp(value, 1, 256));
    }

    public IReadOnlyList<DownloadSource> Sources { get; } = [DownloadSource.Official, DownloadSource.BmclApi];

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private async Task SaveConcurrencyAsync()
    {
        _main.Context.Settings.MaxConcurrency = MaxConcurrency;
        _main.Context.DownloadEngineOptions.MaxConcurrency = MaxConcurrency;
        await _main.Context.SettingsStore.SaveAsync(_main.Context.Settings).ConfigureAwait(true);

        Status = $"已保存：最大并发连接数 {MaxConcurrency}（对后续任务生效）。";
    }

    private void ClearFinished()
    {
        var finished = Items.Where(i => i.State != ActivityState.Running).ToList();
        foreach (var item in finished) Items.Remove(item);
    }

    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    /// <summary>Abandons one task: the activity is closed even if the work cannot observe the token.</summary>
    private void CancelTask(object? parameter)
    {
        if (parameter is not ActivityItem item) return;
        if (item.State != ActivityState.Running) return;

        _main.CancelRunningWork();   // stops the catalogue install pipeline (pack downloads included)
        _main.Activities.Finish(item, ActivityState.Cancelled);
        System.Diagnostics.Debug.WriteLine("task cancelled: " + item.Title);
    }
}
