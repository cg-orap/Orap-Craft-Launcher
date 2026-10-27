using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;
using Orion.Core.Util;

namespace Orion.Core.Instances;

/// <summary>一份实例快照的元数据（saves / config / mods 的副本）。</summary>
public sealed class BackupSnapshot
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>manual / before-update / before-restore / pack-install</summary>
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "manual";
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    [JsonIgnore] public string TriggerLabel => Trigger switch
    {
        "before-update" => "更新前",
        "before-restore" => "恢复前",
        "pack-install" => "装整合包前",
        _ => "手动",
    };

    [JsonIgnore] public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes} B",
    };

    [JsonIgnore] public string TimeText => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
}

/// <summary>
/// 实例快照：把 saves / config / mods 复制到 <c>backups/snapshots/&lt;时间戳&gt;/</c>，恢复时先给当前状态
/// 拍一份"恢复前"快照，所以任何一次恢复都能回到上一步。
/// </summary>
public sealed class InstanceBackupService(string? backupRootOverride = null, bool useRecycleBin = true)
{
    private static readonly string[] Folders = ["saves", "config", "mods"];

    public string BackupRoot(Instance instance) =>
        backupRootOverride ?? Path.Combine(instance.GameDirectory, "backups", "snapshots");

    public IReadOnlyList<BackupSnapshot> List(Instance instance)
    {
        var root = BackupRoot(instance);
        if (!Directory.Exists(root)) return [];

        var snapshots = new List<BackupSnapshot>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var meta = Path.Combine(directory, "snapshot.json");
            if (File.Exists(meta))
            {
                try
                {
                    var snapshot = JsonUtil.Deserialize<BackupSnapshot>(File.ReadAllText(meta));
                    if (snapshot is not null)
                    {
                        snapshots.Add(snapshot);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // 坏掉的元数据按目录名兜底，不让一条脏数据挡住列表。
                }
            }

            snapshots.Add(new BackupSnapshot
            {
                Id = Path.GetFileName(directory),
                CreatedAt = Directory.GetCreationTime(directory),
                Trigger = "manual",
                Note = "（元数据缺失）",
            });
        }

        return snapshots.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public async Task<BackupSnapshot> CreateAsync(Instance instance, string trigger = "manual", string note = "", CancellationToken ct = default)
    {
        var root = BackupRoot(instance);
        Directory.CreateDirectory(root);

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var directory = Path.Combine(root, id);
        var suffix = 1;
        while (Directory.Exists(directory))
        {
            directory = Path.Combine(root, $"{id}-{suffix++}");
        }

        Directory.CreateDirectory(directory);

        long bytes = 0;
        var files = 0;
        foreach (var folder in Folders)
        {
            var source = Path.Combine(instance.GameDirectory, folder);
            if (!Directory.Exists(source)) continue;

            var target = Path.Combine(directory, folder);
            (bytes, files) = await CopyDirectoryAsync(source, target, bytes, files, ct).ConfigureAwait(false);
        }

        var snapshot = new BackupSnapshot
        {
            Id = Path.GetFileName(directory),
            CreatedAt = DateTimeOffset.Now,
            Trigger = trigger,
            SizeBytes = bytes,
            FileCount = files,
            Note = note,
        };

        await JsonUtil.WriteFileAsync(Path.Combine(directory, "snapshot.json"), snapshot, ct).ConfigureAwait(false);
        AppLog.Info("backup", "snapshot-created", ("instance", instance.Id), ("id", snapshot.Id), ("bytes", bytes), ("files", files));
        return snapshot;
    }

    /// <summary>恢复前会自动为当前状态拍一份 before-restore 快照。</summary>
    public async Task<BackupSnapshot> RestoreAsync(Instance instance, string snapshotId, CancellationToken ct = default)
    {
        var root = BackupRoot(instance);
        var source = Path.Combine(root, snapshotId);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"找不到快照 {snapshotId}");

        var safety = await CreateAsync(instance, "before-restore", $"恢复到 {snapshotId} 之前").ConfigureAwait(false);

        foreach (var folder in Folders)
        {
            var target = Path.Combine(instance.GameDirectory, folder);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

            var snapshotFolder = Path.Combine(source, folder);
            if (!Directory.Exists(snapshotFolder)) continue;

            await CopyDirectoryAsync(snapshotFolder, target, 0, 0, ct).ConfigureAwait(false);
        }

        AppLog.Info("backup", "snapshot-restored", ("instance", instance.Id), ("id", snapshotId), ("safety", safety.Id));
        return safety;
    }

    /// <summary>
    /// 把一份快照导出成 zip（单文件便于拷到移动硬盘/网盘）。目标目录不存在会自动创建，
    /// 同名文件会被覆盖。返回 zip 的完整路径。
    /// </summary>
    public string Export(Instance instance, string snapshotId, string destinationDirectory)
    {
        var source = Path.Combine(BackupRoot(instance), snapshotId);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"找不到快照 {snapshotId}");

        Directory.CreateDirectory(destinationDirectory);

        var safeName = string.Join("_", instance.Name.Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(destinationDirectory, $"{safeName}-{snapshotId}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        System.IO.Compression.ZipFile.CreateFromDirectory(source, zipPath);
        AppLog.Info("backup", "snapshot-exported", ("instance", instance.Id), ("id", snapshotId), ("zip", zipPath));
        return zipPath;
    }

    /// <summary>删除一份快照；返回是否真的删掉了。</summary>
    public bool Delete(Instance instance, string snapshotId)
    {
        var directory = Path.Combine(BackupRoot(instance), snapshotId);
        if (!Directory.Exists(directory)) return false;

        try
        {
            // 优先丢进 Windows 回收站：误删可以从资源管理器还原。
            if (useRecycleBin)
            {
                try
                {
                    Game.RecycleBin.RemoveDirectory(directory);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // 网络盘等场景没有回收站，退回永久删除。
                    AppLog.Warn("backup", "recycle-unavailable", ("id", snapshotId), ("error", ex.Message));
                }
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("backup", "snapshot-delete-failed", ("id", snapshotId), ("error", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 列出快照里可单独恢复的顶层条目：<c>saves/*</c> 的每个世界、<c>config</c> 下的每个文件/目录、
    /// <c>mods</c> 下的每个 jar。目录条目的大小是其内部文件之和。
    /// </summary>
    public IReadOnlyList<SnapshotEntry> ListEntries(Instance instance, string snapshotId)
    {
        var root = Path.Combine(BackupRoot(instance), snapshotId);
        if (!Directory.Exists(root)) return [];

        var entries = new List<SnapshotEntry>();
        foreach (var folder in Folders)
        {
            var folderPath = Path.Combine(root, folder);
            if (!Directory.Exists(folderPath)) continue;

            foreach (var child in Directory.EnumerateFileSystemEntries(folderPath))
            {
                var isDirectory = Directory.Exists(child);
                var size = isDirectory
                    ? Directory.EnumerateFiles(child, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                    : new FileInfo(child).Length;

                entries.Add(new SnapshotEntry($"{folder}/{Path.GetFileName(child)}", isDirectory, size));
            }
        }

        return entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 只恢复快照里的一个条目（一个世界目录 / 一个配置文件 / 一个 jar）。覆盖前会把当前的同名条目
    /// 复制到 <c>backups/manual-restores/&lt;时间戳&gt;/</c>，所以单文件恢复同样可撤销。
    /// 返回备份目录（没有旧文件时返回空字符串）。
    /// </summary>
    public string RestoreEntry(Instance instance, string snapshotId, string relativePath)
    {
        var source = Path.Combine(BackupRoot(instance), snapshotId, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"快照里没有 {relativePath}");
        }

        var target = Path.Combine(instance.GameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var safetyRoot = Path.Combine(instance.GameDirectory, "backups", "manual-restores", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var safetyTarget = Path.Combine(safetyRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        var hadExisting = File.Exists(target) || Directory.Exists(target);
        if (hadExisting)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(safetyTarget)!);
            if (Directory.Exists(target))
            {
                CopyDirectory(target, safetyTarget);
                Directory.Delete(target, recursive: true);
            }
            else
            {
                File.Copy(target, safetyTarget, overwrite: true);
                File.Delete(target);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
        }
        else
        {
            File.Copy(source, target, overwrite: true);
        }

        AppLog.Info("backup", "entry-restored",
            ("instance", instance.Id), ("snapshot", snapshotId), ("entry", relativePath),
            ("safety", hadExisting ? safetyRoot : "(none)"));

        return hadExisting ? safetyRoot : "";
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
    private string RestoreBackupRoot(Instance instance) =>
        Path.Combine(instance.GameDirectory, "backups", "manual-restores");

    /// <summary>列出单条恢复留下的旧文件备份（最新的在前）。</summary>
    public IReadOnlyList<RestoreBackupEntry> ListRestoreBackups(Instance instance)
    {
        var root = RestoreBackupRoot(instance);
        if (!Directory.Exists(root)) return [];

        var entries = new List<RestoreBackupEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
            entries.Add(new RestoreBackupEntry(
                Path.GetFileName(directory),
                Directory.GetCreationTime(directory),
                files.Sum(f => new FileInfo(f).Length),
                files.Count,
                directory));
        }

        return entries.OrderByDescending(e => e.CreatedAt).ToList();
    }

    /// <summary>删除一份单条恢复的旧文件备份（默认进回收站）。</summary>
    public bool DeleteRestoreBackup(Instance instance, string id)
    {
        var directory = Path.Combine(RestoreBackupRoot(instance), id);
        if (!Directory.Exists(directory)) return false;

        try
        {
            if (useRecycleBin)
            {
                try
                {
                    Game.RecycleBin.RemoveDirectory(directory);
                    AppLog.Info("backup", "restore-backup-deleted", ("instance", instance.Id), ("id", id), ("mode", "recycle"));
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    AppLog.Warn("backup", "recycle-unavailable", ("id", id), ("error", ex.Message));
                }
            }

            Directory.Delete(directory, recursive: true);
            AppLog.Info("backup", "restore-backup-deleted", ("instance", instance.Id), ("id", id), ("mode", "permanent"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("backup", "restore-backup-delete-failed", ("id", id), ("error", ex.Message));
            return false;
        }
    }
    private static async Task<(long Bytes, int Files)> CopyDirectoryAsync(
        string source,
        string target,
        long bytes,
        int files,
        CancellationToken ct)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var input = File.OpenRead(file))
            await using (var output = File.Create(destination))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            bytes += new FileInfo(destination).Length;
            files++;
        }

        return (bytes, files);
    }
}
/// <summary>快照里的一个可单独恢复的条目（文件或目录，如某个世界、某个 config、某个 jar）。</summary>
public sealed record SnapshotEntry(string RelativePath, bool IsDirectory, long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes} B",
    };

    public string KindLabel => IsDirectory ? "目录" : "文件";
}
/// <summary>单条恢复时被覆盖的旧文件（backups/manual-restores/&lt;时间戳&gt;）。</summary>
public sealed record RestoreBackupEntry(string Id, DateTimeOffset CreatedAt, long SizeBytes, int FileCount, string Directory)
{
    public string TimeText => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes} B",
    };

    public string Detail => $"{SizeText} · {FileCount} 个文件";
}
