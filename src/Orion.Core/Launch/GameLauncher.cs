using System.Diagnostics;
using System.Text;
using Orion.Core.Diagnostics;
using Orion.Core.Game;

namespace Orion.Core.Launch;

public sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A running Minecraft process: stdout/stderr are streamed into a log file and exposed as
/// an event so the UI can show live output.
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    private const int RingCapacity = 2000;

    private readonly Process _process;
    private readonly Queue<string> _recent = new(RingCapacity);
    private readonly object _gate = new();
    private readonly StreamWriter? _logWriter;
    private readonly GameLogFormatter _formatter = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GameSession(Process process, string logFilePath, StreamWriter? writer, string versionId, string gameDirectory)
    {
        _process = process;
        _logWriter = writer;
        VersionId = versionId;
        GameDirectory = gameDirectory;
        LogFilePath = logFilePath;
        StartedAt = DateTimeOffset.UtcNow;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try
            {
                _exit.TrySetResult(_process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                _exit.TrySetResult(-1);
            }
        };
    }

    public int ProcessId => _process.Id;
    public string VersionId { get; }
    public string GameDirectory { get; }
    public string LogFilePath { get; }
    public DateTimeOffset StartedAt { get; }

    public event Action<string>? OutputReceived;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (_gate) return _recent.ToArray();
        }
    }

    public Task<int> WaitForExitAsync() => _exit.Task;

    /// <summary>Set when the launcher itself ended the process (user pressed stop).</summary>
    public bool KilledByLauncher { get; private set; }

    internal void AppendRaw(string raw)
    {
        foreach (var line in _formatter.Process(raw)) Append(line);
    }

    internal void Append(string line)
    {
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RingCapacity) _recent.Dequeue();

            try
            {
                _logWriter?.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            OutputReceived?.Invoke(line);
        }
        catch
        {
            // A UI subscriber must never take down the launcher.
        }
    }

    public void Kill()
    {
        KilledByLauncher = true;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("launch", "kill-failed", ("error", ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (_logWriter is not null)
            {
                await _logWriter.FlushAsync().ConfigureAwait(false);
                await _logWriter.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }

        _process.Dispose();
    }
}

/// <summary>
/// Starts Minecraft and supervises the process.
///
/// Before spawning, every native library is (re-)extracted into the version's natives
/// directory, so a launch never depends on the install step having completed successfully.
/// </summary>
public sealed class GameLauncher
{
    private readonly GamePaths _paths;
    private readonly bool _forceUtf8Logs;

    public GameLauncher(GamePaths paths, bool forceUtf8Console = true)
    {
        _paths = paths;
        _forceUtf8Logs = forceUtf8Console;
    }

    public async Task<GameSession> LaunchAsync(
        LaunchRequest request,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(request.Java.ExecutablePath))
            throw new LaunchException($"找不到 Java 可执行文件: {request.Java.ExecutablePath}");

        Directory.CreateDirectory(request.GameDirectory);

        // 1. Natives must exist before the JVM starts.
        var context = Game.RuleContext.ForCurrentMachine();
        var libraries = LibraryPlanner.Plan(_paths, request.Version.Metadata.Libraries, context);
        var nativesDirectory = _paths.VersionNativesDir(request.Version.Id);

        if (libraries.Natives.Count > 0)
        {
            var extraction = await NativeExtractor.ExtractAsync(libraries.Natives, nativesDirectory, ct).ConfigureAwait(false);
            if (extraction.Errors.Count > 0)
            {
                foreach (var error in extraction.Errors) log?.Report("本地库解压警告: " + error);
            }
        }

        // 2. Build the command line.
        var plan = LaunchPlanner.Build(_paths, request);
        foreach (var warning in plan.Warnings) log?.Report("启动警告: " + warning);

        var arguments = new List<string>(plan.Arguments.Count + 4);
        if (_forceUtf8Logs)
        {
            // Keeps the captured log readable regardless of the Windows console code page.
            arguments.Add("-Dfile.encoding=UTF-8");
            if (request.Java.MajorVersion >= 19)
            {
                arguments.Add("-Dstdout.encoding=UTF-8");
                arguments.Add("-Dstderr.encoding=UTF-8");
            }
        }

        arguments.AddRange(plan.Arguments);

        var logDirectory = Path.Combine(request.GameDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"orion-{SafeFileName(request.Version.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var writer = new StreamWriter(new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync($"[Orion] {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 {request.Version.Id} (root {request.Version.RootVersionId})").ConfigureAwait(false);
        await writer.WriteLineAsync($"[Orion] 账户: {request.Account.Username} ({request.Account.Type})").ConfigureAwait(false);
        await writer.WriteLineAsync($"[Orion] Java: {request.Java.Version} @ {request.Java.Home}").ConfigureAwait(false);
        await writer.WriteLineAsync($"[Orion] 游戏目录: {plan.WorkingDirectory}").ConfigureAwait(false);
        await writer.WriteLineAsync($"[Orion] 命令行: {Redact(plan.ToDisplayString(), request.Account.AccessToken)}").ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        AppLog.Info("launch", "starting",
            ("version", request.Version.Id),
            ("java", request.Java.Version),
            ("memory", request.MemoryMb),
            ("gameDir", plan.WorkingDirectory));

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new LaunchException("无法启动 Java 进程。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await writer.WriteLineAsync("[Orion] 进程启动失败: " + ex.Message).ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
            throw new LaunchException($"启动 Java 进程失败: {ex.Message}", ex);
        }

        var session = new GameSession(process, logFile, writer, request.Version.Id, plan.WorkingDirectory);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) session.AppendRaw(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) session.AppendRaw(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        log?.Report($"已启动游戏进程 (PID {process.Id})，日志: {logFile}");
        AppLog.Info("launch", "started", ("pid", process.Id), ("log", logFile));

        return session;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }

    /// <summary>Removes the access token from a command line before it is logged.</summary>
    public static string Redact(string text, string? accessToken)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (string.IsNullOrEmpty(accessToken) || accessToken.Length < 6) return text;
        return text.Replace(accessToken, "<redacted>", StringComparison.Ordinal);
    }
}
