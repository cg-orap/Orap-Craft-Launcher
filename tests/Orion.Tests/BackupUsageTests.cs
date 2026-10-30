using Orion.Core.Instances;
using Xunit;

namespace Orion.Tests;

/// <summary>侧边栏「备份占用」：只统计快照目录，配额不足要能算成 100% 而不是溢出。</summary>
public sealed class BackupUsageTests
{
    [Fact]
    public void MissingDirectoriesCountAsZero()
    {
        var usage = BackupUsage.Measure([@"Z:\definitely-not-here\snapshots"], 8192);

        Assert.Equal(0, usage.UsedBytes);
        Assert.Equal(0, usage.Percent);
        Assert.Equal("0 B / 8 GB", usage.Text);
    }

    [Fact]
    public void SumsFilesAcrossSnapshotRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "orap-backup-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "a", "backups", "snapshots");
        var second = Path.Combine(root, "b", "backups", "snapshots");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            File.WriteAllBytes(Path.Combine(first, "one.bin"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(second, "two.bin"), new byte[4096]);

            var usage = BackupUsage.MeasureForInstances(
                [Path.Combine(root, "a"), Path.Combine(root, "b")], quotaMb: 8192);

            Assert.Equal(6144, usage.UsedBytes);
            Assert.Equal("6 KB / 8 GB", usage.Text);
            Assert.InRange(usage.Percent, 0, 0.01);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PercentClampsAtOneHundred()
    {
        var root = Path.Combine(Path.GetTempPath(), "orap-backup-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllBytes(Path.Combine(root, "big.bin"), new byte[2 * 1024 * 1024]);   // 2 MB
            var usage = BackupUsage.Measure([root], quotaMb: 1);                            // 配额 1 MB

            Assert.True(usage.UsedBytes > usage.QuotaBytes);
            Assert.Equal(100, usage.Percent);
            Assert.True(usage.IsNearLimit);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    [InlineData((long)(1.5 * 1024 * 1024 * 1024), "1.5 GB")]
    public void FormatsHumanReadableSizes(long bytes, string expected)
    {
        Assert.Equal(expected, BackupUsage.Format(bytes));
    }
}