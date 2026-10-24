using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Launch;

namespace Orion.App.ViewModels;

/// <summary>Log page: launcher messages, live game output and crash analysis.</summary>
public sealed class LogsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _crashSummary = "";
    private string _crashReportPath = "";
    private string _gameLogPath = "";
    private int _tabIndex;

    public LogsViewModel(MainViewModel main)
    {
        _main = main;

        ClearCommand = new RelayCommand(() => { LauncherLines.Clear(); GameLines.Clear(); });
        OpenLogFolderCommand = new RelayCommand(() => Open(_main.Context.Paths.LogsDir));
        OpenGameLogCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(GameLogPath)) Open(Path.GetDirectoryName(GameLogPath)!); });
        CopyCommand = new RelayCommand(Copy);

        LauncherLines.Add("Orap Craft Launcher 日志面板");
        LauncherLines.Add("========================");
    }

    public ICommand ClearCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenGameLogCommand { get; }
    public ICommand CopyCommand { get; }

    public ObservableList<string> LauncherLines { get; } = [];
    public ObservableList<string> GameLines { get; } = [];
    public ObservableList<string> CrashCauses { get; } = [];
    public ObservableList<string> Evidence { get; } = [];

    public int TabIndex
    {
        get => _tabIndex;
        set => SetProperty(ref _tabIndex, value);
    }

    public string CrashSummary
    {
        get => _crashSummary;
        private set => SetProperty(ref _crashSummary, value);
    }

    public string CrashReportPath
    {
        get => _crashReportPath;
        private set => SetProperty(ref _crashReportPath, value);
    }

    public string GameLogPath
    {
        get => _gameLogPath;
        private set => SetProperty(ref _gameLogPath, value);
    }

    public void AppendSystem(string message)
    {
        RunOnUi(() =>
        {
            LauncherLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (LauncherLines.Count > 1500) LauncherLines.RemoveAt(0);
        });
    }

    public void AppendLauncher(string line)
    {
        RunOnUi(() =>
        {
            LauncherLines.Add(line);
            while (LauncherLines.Count > 1500) LauncherLines.RemoveAt(0);
        });
    }

    public void AppendGame(string line)
    {
        RunOnUi(() =>
        {
            GameLines.Add(line);
            while (GameLines.Count > 3000) GameLines.RemoveAt(0);
        });
    }

    public void SetCrashAnalysis(CrashAnalysis analysis, string? logPath)
    {
        RunOnUi(() =>
        {
            GameLogPath = logPath ?? "";
            CrashSummary = analysis.Summary;
            CrashReportPath = analysis.CrashReportPath ?? "";

            CrashCauses.Replace(analysis.Causes);
            Evidence.Replace(analysis.Evidence);

            if (analysis.IsCrash) TabIndex = 2;

            LauncherLines.Add("");
            LauncherLines.Add($"=== 崩溃分析 {DateTime.Now:HH:mm:ss} ===");
            LauncherLines.Add("结论: " + analysis.Summary);
            foreach (var cause in analysis.Causes.Skip(1)) LauncherLines.Add("  - " + cause);
            foreach (var evidence in analysis.Evidence.Take(8)) LauncherLines.Add("  证据: " + evidence);
            if (!string.IsNullOrEmpty(analysis.CrashReportPath)) LauncherLines.Add("崩溃报告: " + analysis.CrashReportPath);
        });
    }

    private void Copy()
    {
        var text = TabIndex switch
        {
            1 => string.Join(Environment.NewLine, GameLines),
            2 => $"结论: {CrashSummary}{Environment.NewLine}" +
                 string.Join(Environment.NewLine, CrashCauses.Select(c => "- " + c)) + Environment.NewLine +
                 string.Join(Environment.NewLine, Evidence.Select(e => "  " + e)),
            _ => string.Join(Environment.NewLine, LauncherLines),
        };

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
            AppendSystem("日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            AppendSystem("复制失败: " + ex.Message);
        }
    }

    private static void Open(string directory)
    {
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
