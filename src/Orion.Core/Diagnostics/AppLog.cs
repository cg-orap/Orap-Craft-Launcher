using System.Text;

namespace Orion.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// Small structured, thread-safe file logger.
///
/// Design notes:
/// * No third party dependency: the launcher ships as a single self-contained EXE.
/// * Log files are written under &lt;data root&gt;/logs and roll daily.
/// * A bounded in-memory ring buffer feeds the in-app log viewer, so the UI never has
///   to re-read files while the game is writing to them.
/// </summary>
public static class AppLog
{
    private const int RingSize = 4000;

    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private static string? _directory;
    private static string? _currentFile;
    private static DateOnly _currentDate;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for every written line (already formatted). May fire on a worker thread.</summary>
    public static event Action<string>? LineWritten;

    public static string? LogDirectory => _directory;

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
            _currentFile = null;
        }

        Info("log", "session-started",
            ("pid", Environment.ProcessId),
            ("os", Environment.OSVersion.VersionString),
            ("runtime", Environment.Version.ToString()),
            ("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()));
    }

    public static void Debug(string category, string message, params (string Key, object? Value)[] fields)
        => Write(LogLevel.Debug, category, message, fields);

    public static void Info(string category, string message, params (string Key, object? Value)[] fields)
        => Write(LogLevel.Info, category, message, fields);

    public static void Warn(string category, string message, params (string Key, object? Value)[] fields)
        => Write(LogLevel.Warn, category, message, fields);

    public static void Error(string category, string message, Exception? exception = null, params (string Key, object? Value)[] fields)
        => Write(LogLevel.Error, category, message, fields, exception);

    public static void Write(LogLevel level, string category, string message, (string Key, object? Value)[] fields, Exception? exception = null)
    {
        if (level < MinimumLevel) return;

        var builder = new StringBuilder(160);
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" [").Append(LevelLabel(level)).Append("] ");
        builder.Append('[').Append(category).Append("] ");
        builder.Append(message);
        foreach (var (key, value) in fields)
        {
            builder.Append(' ').Append(key).Append('=').Append(Format(value));
        }

        if (exception is not null)
        {
            builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        var line = builder.ToString();

        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingSize) Ring.Dequeue();

            if (_directory is not null)
            {
                try
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (_currentFile is null || today != _currentDate)
                    {
                        _currentDate = today;
                        _currentFile = Path.Combine(_directory, $"launcher-{today:yyyyMMdd}.log");
                    }

                    File.AppendAllText(_currentFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Logging must never take the application down.
                }
            }
        }

        if (exception is not null && level >= LogLevel.Error)
        {
            lock (Gate)
            {
                if (_directory is not null)
                {
                    try
                    {
                        var crashFile = Path.Combine(_directory, "exceptions.log");
                        File.AppendAllText(crashFile, line + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // A subscriber must not break logging.
        }
    }

    /// <summary>Returns a snapshot of the most recent lines (oldest first).</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Ring.ToArray();
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO ",
    };

    private static string Format(object? value) => value switch
    {
        null => "null",
        string s when s.Contains(' ') => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };
}
