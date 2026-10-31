using Orion.Core.Game;
using Orion.Core.Instances;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 磁盘占用统计与清理白名单。
/// 关键不变量：saves / mods / resourcepacks / config 永远不在删除列表里，
/// 日志按保留天数过滤，预览数字必须等于实际收集到的量。
/// </summary>
public sealed class DiskUsageTests
{
    [Fact]
    public void MeasureAttributesEveryCategorySeparately()
    {
        using var sandbox = new Sandbox();
        sandbox.File("mods/a.jar", 1000);
        sandbox.File("mods/b.jar", 500);
        sandbox.File("resourcepacks/pack.zip", 300);
        sandbox.File("saves/world/level.dat", 200);
        sandbox.File("logs/latest.log", 50);
        sandbox.File("options.txt", 20);

        var usage = DiskUsageService.Measure(sandbox.Instance);

        Assert.Equal(1000 + 500, usage.Slice("mods")!.Bytes);
        Assert.Equal(2, usage.Slice("mods")!.FileCount);
        Assert.Equal(300, usage.Slice("resourcepacks")!.Bytes);
        Assert.Equal(200, usage.Slice("saves")!.Bytes);
        Assert.Equal(50, usage.Slice("logs")!.Bytes);

        // 零散文件的归类不会丢
        Assert.Equal(20, usage.Slice("other")!.Bytes);

        // 总量 = 各分类之和
        Assert.Equal(1000 + 500 + 300 + 200 + 50 + 20, usage.TotalBytes);
        Assert.Equal(usage.Slices.Sum(s => s.Bytes), usage.TotalBytes);
    }

    [Fact]
    public void OnlyLogsCrashesCacheAreMarkedCleanable()
    {
        using var sandbox = new Sandbox();
        sandbox.File("mods/a.jar", 10);

        var usage = DiskUsageService.Measure(sandbox.Instance);

        Assert.False(usage.Slice("mods")!.Cleanable);
        Assert.False(usage.Slice("saves")!.Cleanable);
        Assert.False(usage.Slice("resourcepacks")!.Cleanable);
        Assert.True(usage.Slice("logs")!.Cleanable);
        Assert.True(usage.Slice("crash-reports")!.Cleanable);
        Assert.True(usage.Slice(".orap-cache")!.Cleanable);
    }

    [Fact]
    public void CleanupNeverTouchesSavesModsOrPacks()
    {
        using var sandbox = new Sandbox();
        sandbox.File("mods/keep.jar", 100);
        sandbox.File("saves/world/level.dat", 100);
        sandbox.File("resourcepacks/keep.zip", 100);
        sandbox.File("config/keep.toml", 100);
        var oldLog = sandbox.File("logs/old.log", 100);
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-30));
        sandbox.File("crash-reports/crash.txt", 100);

        var options = new CleanupOptions { CleanLogs = true, CleanCrashReports = true, CleanCache = true };
        var preview = DiskUsageService.Preview(sandbox.Instance, options);
        var result = DiskUsageService.Clean(sandbox.Instance, options);

        Assert.Equal(2, preview.FileCount);              // 只有 old.log 和 crash.txt
        Assert.Equal(200, preview.Bytes);
        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(200, result.FreedBytes);

        Assert.True(File.Exists(Path.Combine(sandbox.Instance.GameDirectory, "mods", "keep.jar")));
        Assert.True(File.Exists(Path.Combine(sandbox.Instance.GameDirectory, "saves", "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(sandbox.Instance.GameDirectory, "resourcepacks", "keep.zip")));
        Assert.True(File.Exists(Path.Combine(sandbox.Instance.GameDirectory, "config", "keep.toml")));
    }

    [Fact]
    public void RecentLogsAreKeptAccordingToRetentionDays()
    {
        using var sandbox = new Sandbox();
        var old = sandbox.File("logs/ancient.log", 100);
        var fresh = sandbox.File("logs/today.log", 100);

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        var options = new CleanupOptions { CleanLogs = true, LogRetentionDays = 7, CleanCrashReports = false };
        var preview = DiskUsageService.Preview(sandbox.Instance, options);

        var target = Assert.Single(preview.Paths);
        Assert.Contains("ancient.log", target);
        Assert.DoesNotContain("today.log", target);
    }

    [Fact]
    public void ZeroRetentionDaysStillKeepsTheLogWrittenJustNow()
    {
        using var sandbox = new Sandbox();
        var fresh = sandbox.File("logs/latest.log", 10);
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddMinutes(1));

        var options = new CleanupOptions { CleanLogs = true, LogRetentionDays = 0, CleanCrashReports = false };
        var preview = DiskUsageService.Preview(sandbox.Instance, options);

        Assert.True(preview.IsEmpty);
    }

    [Fact]
    public void ChoosingNothingDeletesNothing()
    {
        using var sandbox = new Sandbox();
        sandbox.File("logs/old.log", 100);
        sandbox.File("crash-reports/crash.txt", 100);

        var preview = DiskUsageService.Preview(sandbox.Instance, new CleanupOptions());
        var result = DiskUsageService.Clean(sandbox.Instance, new CleanupOptions());

        Assert.True(preview.IsEmpty);
        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(sandbox.Instance.GameDirectory, "logs", "old.log")));
    }

    [Fact]
    public void InstanceCacheAndTrashAreIncludedWhenEnabled()
    {
        using var sandbox = new Sandbox();
        sandbox.File(".orap-cache/dep.jar", 400);
        var trash = Path.Combine(sandbox.Paths.Root, "trash", "mods");
        Directory.CreateDirectory(trash);
        File.WriteAllBytes(Path.Combine(trash, "old.jar"), new byte[250]);

        var options = new CleanupOptions { CleanCache = true, CleanTrash = true, CleanCrashReports = false };
        var preview = DiskUsageService.Preview(sandbox.Instance, options, sandbox.Paths);

        Assert.Equal(650, preview.Bytes);
        Assert.Contains(preview.Paths, p => p.EndsWith(".orap-cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Paths, p => p.StartsWith(trash, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedUsageSumsEveryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "orap-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new GamePaths(root);
            paths.EnsureBaseDirectories();

            File.WriteAllBytes(Path.Combine(paths.VersionsDir, "1.21.1.json"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(paths.LibrariesDir, "a"));
            File.WriteAllBytes(Path.Combine(paths.LibrariesDir, "a", "lib.jar"), new byte[200]);
            File.WriteAllBytes(Path.Combine(paths.AssetsDir, "objects", "hash"), new byte[300]);

            var shared = DiskUsageService.MeasureShared(paths);

            Assert.Equal(100, shared.VersionsBytes);
            Assert.Equal(200, shared.LibrariesBytes);
            Assert.Equal(300, shared.AssetsBytes);
            Assert.Equal(shared.VersionsBytes + shared.LibrariesBytes + shared.AssetsBytes
                         + shared.RuntimeBytes + shared.CacheBytes + shared.LogsBytes + shared.TrashBytes,
                shared.Total);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FormatBytesUsesReadableUnits()
    {
        Assert.Equal("512 B", DiskUsageService.FormatBytes(512));
        Assert.Equal("1 KB", DiskUsageService.FormatBytes(1024));
        Assert.Equal("1.0 MB", DiskUsageService.FormatBytes(1024 * 1024));
        Assert.Equal("2.00 GB", DiskUsageService.FormatBytes(2L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void MissingInstanceDirectoryMeasuresZero()
    {
        var instance = new Instance { Id = "x", Name = "x", GameDirectory = Path.Combine(Path.GetTempPath(), "orap-missing-" + Guid.NewGuid().ToString("N")) };

        var usage = DiskUsageService.Measure(instance);

        Assert.Equal(0, usage.TotalBytes);
    }

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Paths = new GamePaths(Path.Combine(Path.GetTempPath(), "orap-disk-" + Guid.NewGuid().ToString("N")));
            Paths.EnsureBaseDirectories();

            Instance = new Instance
            {
                Id = "probe",
                Name = "Probe",
                GameDirectory = Path.Combine(Paths.Root, "instances", "probe", "minecraft"),
            };

            Directory.CreateDirectory(Instance.GameDirectory);
        }

        public GamePaths Paths { get; }
        public Instance Instance { get; }

        /// <summary>写一个指定大小的文件，返回绝对路径。</summary>
        public string File(string relative, int bytes)
        {
            var path = Path.Combine(Instance.GameDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Paths.Root, recursive: true); } catch { }
        }
    }
}
