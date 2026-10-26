using System.Windows.Media;

namespace Orion.App.ViewModels;

/// <summary>日志级别，用来做颜色高亮与筛选（不做任何分析）。</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
    Debug,
}

/// <summary>
/// 一行日志。原始文本保持不变，级别由文本前缀推断，颜色交给 XAML 的 DataTrigger，
/// 这样切换主题时高亮颜色会跟着调色板走。
/// </summary>
public sealed class LogLineView(string text)
{
    public string Text { get; } = text;

    public LogLevel Level { get; } = Detect(text);

    public string LevelLabel => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Debug => "DEBUG",
        _ => "INFO",
    };

    private static LogLevel Detect(string line)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.Ordinal) ||
            line.Contains("[错误]", StringComparison.Ordinal) ||
            line.Contains("失败", StringComparison.Ordinal))
        {
            return LogLevel.Error;
        }

        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[警告]", StringComparison.Ordinal))
        {
            return LogLevel.Warn;
        }

        if (line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        return LogLevel.Info;
    }
}
