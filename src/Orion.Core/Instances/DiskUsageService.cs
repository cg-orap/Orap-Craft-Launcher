using System.Diagnostics;
using Orion.Core.Game;

namespace Orion.Core.Instances;

/// <summary>一块磁盘占用的归类（模组 / 资源包 / 日志 …）。</summary>
public sealed record UsageSlice(string Key, string Label, long Bytes, int FileCount, bool Cleanable)
{
    public string SizeText => DiskUsageService.FormatBytes(Bytes);
}

/// <summary>一个实例的磁盘占用：总量 + 分类。</summary>
public sealed record InstanceUsage(
    string InstanceId,
    string Name,
    string GameDirectory,
    long TotalBytes,
    IReadOnlyList<UsageSlice> Slices)
{
    public string SizeText => DiskUsageService.FormatBytes(TotalBytes);

    public UsageSlice? Slice(string key) =>
        Slices.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>共享目录（版本、库、资源对象）的占用：只统计，不提供删除。</summary>
public sealed record SharedUsage(long VersionsBytes, long LibrariesBytes, long AssetsBytes, long RuntimeBytes,
    long CacheBytes, long LogsBytes, long TrashBytes)
{
    public long Total => VersionsBytes + LibrariesBytes + AssetsBytes + RuntimeBytes + CacheBytes + LogsBytes + TrashBytes;
}

/// <summary>清理选项：默认什么都不删，必须显式勾选。</summary>
public sealed record CleanupOptions
{
    /// <summary>删除早于 N 天的实例日志（当前正在写的日志永远不动）。</summary>
    public int LogRetentionDays { get; init; } = 7;

    public bool CleanLogs { get; init; }

    public bool CleanCrashReports { get; init; }

    /// <summary>实例内的 .orap-cache（补齐前置时下载的临时 jar）与启动器缓存目录。</summary>
    public bool CleanCache { get; init; }

    /// <summary>回收目录（trash）里的东西。</summary>
    public bool CleanTrash { get; init; }
}

/// <summary>清理前预览：将删多少文件、释放多少空间。</summary>
public sealed record CleanupPreview(IReadOnlyList<string> Paths, long Bytes, int FileCount)
{
    public string SizeText => DiskUsageService.FormatBytes(Bytes);
    public bool IsEmpty => FileCount == 0;
}

/// <summary>清理结果。</summary>
public sealed record CleanupResult(int DeletedFiles, long FreedBytes, int FailedFiles, IReadOnlyList<string> Notes)
{
    public string SizeText => DiskUsageService.FormatBytes(FreedBytes);
}

/// <summary>
/// 实例磁盘占用统计与安全清理。
///
/// 清理遵循**白名单**：只有日志、崩溃报告、缓存、临时 jar、回收目录可以被删除；
/// saves / mods / resourcepacks / config 只统计，界面上不给入口。
/// 所有删除都走 Windows 回收站（<see cref="RecycleBin"/>），用户可以从资源管理器还原。
/// </summary>
public static class DiskUsageService
{
    /// <summary>实例内部按目录归类；顺序即界面展示顺序。</summary>
    private static readonly (string Key, string Label, string Relative, bool Cleanable)[] Categories =
    [
        ("mods", "模组", "mods", false),
        ("resourcepacks", "资源包", "resourcepacks", false),
        ("shaderpacks", "光影", "shaderpacks", false),
        ("saves", "存档", "saves", false),
        ("config", "配置", "config", false),
        ("logs", "日志", "logs", true),
        ("crash-reports", "崩溃报告", "crash-reports", true),
        (".orap-cache", "临时缓存", ".orap-cache", true),
    ];

    public static InstanceUsage Measure(Instance instance, CancellationToken ct = default)
    {
        var slices = new List<UsageSlice>();
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, label, relative, cleanable) in Categories)
        {
            ct.ThrowIfCancellationRequested();

            var directory = Path.Combine(instance.GameDirectory, relative);
            accounted.Add(Path.GetFullPath(directory));

            var (bytes, files) = MeasureDirectory(directory, ct);
            slices.Add(new UsageSlice(key, label, bytes, files, cleanable));
        }

        // 其它零散文件（options.txt、日志外的 jar 等）归到"其它"，避免总和小于实际占用
        var (otherBytes, otherFiles) = MeasureRemainder(instance.GameDirectory, accounted, ct);
        if (otherBytes > 0 || otherFiles > 0)
        {
            slices.Add(new UsageSlice("other", "其它", otherBytes, otherFiles, false));
        }

        var total = slices.Sum(s => s.Bytes);
        return new InstanceUsage(instance.Id, instance.Name, instance.GameDirectory, total, slices);
    }

    public static IReadOnlyList<InstanceUsage> MeasureAll(IEnumerable<Instance> instances, CancellationToken ct = default) =>
        [.. instances.Select(instance => Measure(instance, ct))];

    /// <summary>共享目录：版本 / 库 / 资源 / 运行时 / 缓存 / 启动器日志 / 回收站。</summary>
    public static SharedUsage MeasureShared(GamePaths paths, CancellationToken ct = default)
    {
        var (versions, _) = MeasureDirectory(paths.VersionsDir, ct);
        var (libraries, _) = MeasureDirectory(paths.LibrariesDir, ct);
        var (assets, _) = MeasureDirectory(paths.AssetsDir, ct);
        var (runtime, _) = MeasureDirectory(paths.RuntimeDir, ct);
        var (cache, _) = MeasureDirectory(paths.CacheDir, ct);
        var (logs, _) = MeasureDirectory(paths.LogsDir, ct);
        var (trash, _) = MeasureDirectory(Path.Combine(paths.Root, "trash"), ct);

        return new SharedUsage(versions, libraries, assets, runtime, cache, logs, trash);
    }

    /// <summary>
    /// 预览清理：把要删的文件列出来，先给用户看数字，再决定删不删。
    /// </summary>
    public static CleanupPreview Preview(Instance instance, CleanupOptions options, GamePaths? paths = null,
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var pathsToDelete = Collect(instance, options, paths, now ?? DateTimeOffset.Now, ct);
        long bytes = 0;
        var files = 0;

        foreach (var path in pathsToDelete)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                var (dirBytes, dirFiles) = MeasureDirectory(path, ct);
                bytes += dirBytes;
                files += dirFiles;
            }
            else if (File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
                files++;
            }
        }

        return new CleanupPreview(pathsToDelete, bytes, files);
    }

    /// <summary>
    /// 执行清理。只删 <see cref="Collect"/> 列出来的东西（白名单），
    /// 且用回收站删除，误点也能还原。
    /// </summary>
    public static CleanupResult Clean(Instance instance, CleanupOptions options, GamePaths? paths = null,
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var targets = Collect(instance, options, paths, now ?? DateTimeOffset.Now, ct);
        var preview = Preview(instance, options, paths, now, ct);

        var failed = 0;
        foreach (var path in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path)) RecycleBin.RemoveDirectory(path);
                else if (File.Exists(path)) RecycleBin.RemoveFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        var notes = new List<string>();
        if (options.CleanLogs)
        {
            notes.Add($"日志保留最近 {options.LogRetentionDays} 天");
        }

        return new CleanupResult(preview.FileCount - failed, preview.Bytes, failed, notes);
    }

    /// <summary>白名单收集：只返回允许删的路径。</summary>
    internal static List<string> Collect(Instance instance, CleanupOptions options, GamePaths? paths,
        DateTimeOffset now, CancellationToken ct)
    {
        var targets = new List<string>();
        var game = instance.GameDirectory;

        if (options.CleanLogs)
        {
            var logs = Path.Combine(game, "logs");
            if (Directory.Exists(logs))
            {
                var cutoff = now.AddDays(-Math.Max(0, options.LogRetentionDays));
                foreach (var file in Directory.EnumerateFiles(logs, "*.log*"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) targets.Add(file);
                    }
                    catch (IOException)
                    {
                        // 正在写的日志跳过
                    }
                }
            }
        }

        if (options.CleanCrashReports)
        {
            var crashes = Path.Combine(game, "crash-reports");
            if (Directory.Exists(crashes))
            {
                targets.AddRange(Directory.EnumerateFiles(crashes, "*", SearchOption.TopDirectoryOnly));
            }
        }

        if (options.CleanCache)
        {
            var instanceCache = Path.Combine(game, ".orap-cache");
            if (Directory.Exists(instanceCache)) targets.Add(instanceCache);

            if (paths is not null)
            {
                foreach (var dir in new[] { Path.Combine(paths.CacheDir, "packs"), Path.Combine(paths.CacheDir, "icons") })
                {
                    if (Directory.Exists(dir)) targets.Add(dir);
                }
            }
        }

        if (options.CleanTrash && paths is not null)
        {
            var trash = Path.Combine(paths.Root, "trash");
            if (Directory.Exists(trash))
            {
                targets.AddRange(Directory.EnumerateDirectories(trash));
                targets.AddRange(Directory.EnumerateFiles(trash));
            }
        }

        return targets;
    }

    // ── 统计工具 ──────────────────────────────────────────────────────────────
    internal static (long Bytes, int Files) MeasureDirectory(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return (0, 0);

        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                bytes += info.Length;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 权限不足的子目录跳过，统计到哪算哪
        }

        return (bytes, files);
    }

    private static (long Bytes, int Files) MeasureRemainder(string root, HashSet<string> excluded, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return (0, 0);

        long bytes = 0;
        var files = 0;

        try
        {
            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var skip = false;
                foreach (var directory in excluded)
                {
                    if (info.FullName.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        skip = true;
                        break;
                    }
                }

                if (skip) continue;
                bytes += info.Length;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (bytes, files);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.00} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
