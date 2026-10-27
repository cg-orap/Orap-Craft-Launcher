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
public sealed class InstanceBackupService(string? backupRootOverride = null)
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

    /// <summary>删除一份快照；返回是否真的删掉了。</summary>
    public bool Delete(Instance instance, string snapshotId)
    {
        var directory = Path.Combine(BackupRoot(instance), snapshotId);
        if (!Directory.Exists(directory)) return false;

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("backup", "snapshot-delete-failed", ("id", snapshotId), ("error", ex.Message));
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
