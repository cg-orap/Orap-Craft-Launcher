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

        ClearCommand = new RelayCommand(() =>
        {
            LauncherLines.Clear();
            GameLines.Clear();
            LauncherLog.Clear();
            GameLog.Clear();
            RefreshFilter();
        });
        OpenLogFolderCommand = new RelayCommand(() => Open(_main.Context.Paths.LogsDir));
        OpenGameLogCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(GameLogPath)) Open(Path.GetDirectoryName(GameLogPath)!); });
        CopyCommand = new RelayCommand(Copy);
        CopyCrashPathCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(CrashReportPath)) return;
            try
            {
                Clipboard.SetText(CrashReportPath);
                AppendSystem("崩溃报告路径已复制。");
            }
            catch (Exception ex)
            {
                AppendSystem("复制失败: " + ex.Message);
            }
        });

        GameLogView = System.Windows.Data.CollectionViewSource.GetDefaultView(GameLog);
        GameLogView.Filter = Matches;

        SetLevelCommand = new RelayCommand(parameter =>
        {
            if (parameter is string level && level.Length > 0) LevelFilter = level;
        });

        LauncherLines.Add("Orap Craft Launcher 日志面板");
        LauncherLines.Add("========================");
        LauncherLog.Add(new LogLineView(LauncherLines[0]));
        LauncherLog.Add(new LogLineView(LauncherLines[1]));
    }

    private string _levelFilter = "ALL";
    private string _searchText = "";
    private bool _autoScroll = true;

    /// <summary>级别筛选：ALL / ERROR / WARN / INFO / DEBUG。</summary>
    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (!SetProperty(ref _levelFilter, value)) return;
            RefreshFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            RefreshFilter();
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public int ErrorCount => GameLog.Count(l => l.Level == LogLevel.Error);
    public int WarnCount => GameLog.Count(l => l.Level == LogLevel.Warn);

    /// <summary>筛选后的游戏日志；界面只读这个。</summary>
    public System.ComponentModel.ICollectionView GameLogView { get; }

    private void RefreshFilter()
    {
        GameLogView?.Refresh();
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarnCount));
    }

    private bool Matches(object item)
    {
        if (item is not LogLineView line) return false;
        if (_levelFilter != "ALL" && !string.Equals(line.LevelLabel, _levelFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (_searchText.Length > 0 && !line.Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public ICommand SetLevelCommand { get; }

    /// <summary>日志级别筛选（分段控件用）。</summary>
    public IReadOnlyList<string> LevelFilters { get; } = ["ALL", "ERROR", "WARN", "INFO"];

    public int LevelIndex
    {
        get => _levelFilter switch { "ERROR" => 1, "WARN" => 2, "INFO" => 3, _ => 0 };
        set
        {
            var key = Math.Clamp(value, 0, 3) switch { 1 => "ERROR", 2 => "WARN", 3 => "INFO", _ => "ALL" };
            SetLevelCommand.Execute(key);
            OnPropertyChanged();
        }
    }

    public ICommand ClearCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenGameLogCommand { get; }
    public ICommand CopyCommand { get; }

    /// <summary>复制崩溃报告路径（不分析内容）。</summary>
    public ICommand CopyCrashPathCommand { get; }

    public ObservableList<string> LauncherLines { get; } = [];

    /// <summary>与 LauncherLines 同步的带级别版本，供界面高亮与筛选使用。</summary>
    public ObservableList<LogLineView> LauncherLog { get; } = [];

    public ObservableList<LogLineView> GameLog { get; } = [];
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
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LauncherLines.Add(line);
            LauncherLog.Add(new LogLineView(line));
            while (LauncherLines.Count > 1500) LauncherLines.RemoveAt(0);
            while (LauncherLog.Count > 1500) LauncherLog.RemoveAt(0);
        });
    }

    public void AppendLauncher(string line)
    {
        RunOnUi(() =>
        {
            LauncherLines.Add(line);
            LauncherLog.Add(new LogLineView(line));
            while (LauncherLines.Count > 1500) LauncherLines.RemoveAt(0);
            while (LauncherLog.Count > 1500) LauncherLog.RemoveAt(0);
            RefreshFilter();
        });
    }

    public void AppendGame(string line)
    {
        RunOnUi(() =>
        {
            GameLines.Add(line);
            GameLog.Add(new LogLineView(line));
            while (GameLines.Count > 3000) GameLines.RemoveAt(0);
            while (GameLog.Count > 3000) GameLog.RemoveAt(0);
            RefreshFilter();
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
            LauncherLog.Add(new LogLineView(""));
            LauncherLines.Add($"=== 崩溃报告 {DateTime.Now:HH:mm:ss} ===");
            LauncherLog.Add(new LogLineView(LauncherLines[^1]));
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
