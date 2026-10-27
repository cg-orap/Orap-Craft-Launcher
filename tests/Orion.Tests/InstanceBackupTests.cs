using Orion.Core.Instances;

namespace Orion.Tests;

/// <summary>实例快照：创建 / 列表 / 恢复（含安全快照）/ 删除，全部在临时目录里完成。</summary>
public sealed class InstanceBackupTests : IDisposable
{
    private readonly string _root;
    private readonly Instance _instance;
    private readonly InstanceBackupService _service = new();

    public InstanceBackupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orion-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _instance = new Instance
        {
            Id = "test",
            Name = "测试版本",
            GameVersion = "1.21.1",
            GameDirectory = _root,
        };

        Directory.CreateDirectory(Path.Combine(_root, "saves", "world"));
        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "original-level");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "sodium.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        File.WriteAllText(Path.Combine(_root, "mods", "sodium.jar"), "jar-bytes");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task CreateCopiesAllThreeFoldersAndRecordsMetadata()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual", "第一次快照");

        Assert.True(snapshot.FileCount >= 3);
        Assert.True(snapshot.SizeBytes > 0);
        Assert.Equal("手动", snapshot.TriggerLabel);

        var directory = Path.Combine(_service.BackupRoot(_instance), snapshot.Id);
        Assert.True(File.Exists(Path.Combine(directory, "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(directory, "saves", "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(directory, "mods", "sodium.jar")));
    }

    [Fact]
    public async Task ListReturnsNewestFirstAndSurvivesMissingMetadata()
    {
        var first = await _service.CreateAsync(_instance, "manual");
        Directory.CreateDirectory(Path.Combine(_service.BackupRoot(_instance), "99999999-000000"));

        var listed = _service.List(_instance);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, s => s.Id == first.Id);
        Assert.Contains(listed, s => s.Note.Contains("元数据缺失"));
    }

    [Fact]
    public async Task RestoreBringsFilesBackAndKeepsASafetySnapshot()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");

        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "corrupted");
        File.Delete(Path.Combine(_root, "mods", "sodium.jar"));
        File.WriteAllText(Path.Combine(_root, "saves", "world", "extra.dat"), "later-file");

        var safety = await _service.RestoreAsync(_instance, snapshot.Id);

        Assert.Equal("before-restore", safety.Trigger);
        Assert.Equal("original-level", File.ReadAllText(Path.Combine(_root, "saves", "world", "level.dat")));
        Assert.True(File.Exists(Path.Combine(_root, "mods", "sodium.jar")));
        // 恢复是"整目录替换"，快照之后新增的文件不会残留。
        Assert.False(File.Exists(Path.Combine(_root, "saves", "world", "extra.dat")));
        Assert.Equal(2, _service.List(_instance).Count);
    }

    [Fact]
    public async Task RestoreOfUnknownSnapshotThrows()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _service.RestoreAsync(_instance, "nope"));
    }

    [Fact]
    public async Task ExportWritesAZipContainingTheSnapshot()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");
        var destination = Path.Combine(_root, "exports");

        var zip = _service.Export(_instance, snapshot.Id, destination);

        Assert.True(File.Exists(zip));
        Assert.Equal(destination, Path.GetDirectoryName(zip));

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/').EndsWith("saves/world/level.dat"));
        Assert.Contains(archive.Entries, e => e.FullName.EndsWith("snapshot.json"));
    }

    [Fact]
    public void ExportOfUnknownSnapshotThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _service.Export(_instance, "nope", Path.Combine(_root, "exports")));
    }

    [Fact]
    public async Task ListEntriesReturnsWorldsConfigsAndJars()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");
        var entries = _service.ListEntries(_instance, snapshot.Id);

        Assert.Contains(entries, e => e.RelativePath == "saves/world" && e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "config/sodium.json" && !e.IsDirectory);
        Assert.Contains(entries, e => e.RelativePath == "mods/sodium.jar");
    }

    [Fact]
    public async Task RestoreEntryBringsBackOneWorldAndKeepsTheOthers()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");

        // 第二个世界：快照之后新建，恢复 world 时不应该被动到。
        Directory.CreateDirectory(Path.Combine(_root, "saves", "other"));
        File.WriteAllText(Path.Combine(_root, "saves", "other", "level.dat"), "other-world");

        File.WriteAllText(Path.Combine(_root, "saves", "world", "level.dat"), "corrupted");

        var safety = _service.RestoreEntry(_instance, snapshot.Id, "saves/world");

        Assert.True(Directory.Exists(safety));
        Assert.Equal("original-level", File.ReadAllText(Path.Combine(_root, "saves", "world", "level.dat")));
        Assert.Equal("other-world", File.ReadAllText(Path.Combine(_root, "saves", "other", "level.dat")));
    }

    [Fact]
    public async Task RestoreEntryWorksForASingleConfigFile()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");
        File.WriteAllText(Path.Combine(_root, "config", "sodium.json"), "{broken}");

        _service.RestoreEntry(_instance, snapshot.Id, "config/sodium.json");

        Assert.Equal("{}", File.ReadAllText(Path.Combine(_root, "config", "sodium.json")));
    }

    [Fact]
    public void RestoreEntryOfMissingPathThrows()
    {
        Assert.Throws<FileNotFoundException>(() => _service.RestoreEntry(_instance, "20260101-000000", "saves/nope"));
    }

    [Fact]
    public async Task DeleteRemovesTheSnapshot()
    {
        var snapshot = await _service.CreateAsync(_instance, "manual");

        Assert.True(_service.Delete(_instance, snapshot.Id));
        Assert.Empty(_service.List(_instance));
        Assert.False(_service.Delete(_instance, snapshot.Id));
    }
}
