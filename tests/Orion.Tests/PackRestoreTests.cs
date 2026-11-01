using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 还原成整合包原始内容：包外的 jar 必须被移走，包内的必须原地不动。
/// 这是"和 Axolotl 一样只装清单里的东西"的兜底手段。
/// </summary>
public sealed class PackRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orap-restore-" + Guid.NewGuid().ToString("N"));

    public PackRestoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public async Task WithoutSnapshotNothingHappens()
    {
        var game = Path.Combine(_root, "minecraft");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        File.WriteAllText(Path.Combine(game, "mods", "whatever.jar"), "x");

        var installer = CreateInstaller();
        var moved = await installer.RestoreToPackAsync(game);

        Assert.Equal(0, moved);
        Assert.True(File.Exists(Path.Combine(game, "mods", "whatever.jar")));
    }

    [Fact]
    public async Task ExtraJarsAreQuarantinedAndPackFilesStay()
    {
        var game = Path.Combine(_root, "minecraft");
        var mods = Path.Combine(game, "mods");
        Directory.CreateDirectory(mods);

        File.WriteAllText(Path.Combine(mods, "keep-me.jar"), "pack");
        File.WriteAllText(Path.Combine(mods, "extra.jar"), "added later");

        File.WriteAllText(Path.Combine(game, ".orap-pack-manifest.json"),
            """{"files":["keep-me.jar"]}""");

        var installer = CreateInstaller();
        var moved = await installer.RestoreToPackAsync(game);

        Assert.Equal(1, moved);
        Assert.True(File.Exists(Path.Combine(mods, "keep-me.jar")));
        Assert.False(File.Exists(Path.Combine(mods, "extra.jar")));

        var quarantined = Directory.GetFiles(Path.Combine(game, ".orap-quarantine"), "extra.jar",
            SearchOption.AllDirectories);
        Assert.Single(quarantined);
    }

    private PackArchiveInstaller CreateInstaller()
    {
        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();

        using var http = new HttpClient();
        var instances = new InstanceManager(paths);
        var importer = new InstanceImporter(instances, paths);

        return new PackArchiveInstaller(paths, instances, null!, importer, http, null);
    }
}
