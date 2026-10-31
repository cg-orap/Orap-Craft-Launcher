using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// CurseForge 文件选择：**不能**把别的 MC 版本的文件当成"能用"。
/// 真实踩过的坑：1.19.2 的整合包被补进 Iceberg-26.3 和 LibX-1.20.1，
/// Forge 不加载它们，启动后依旧 "iceberg is not installed"。
/// </summary>
public sealed class CurseForgeFileSelectionTests
{
    private static CurseForgeFile File(int id, string name, params string[] gameVersions) =>
        new(1, id, name, name, null, gameVersions);

    [Fact]
    public void PicksTheNewestFileThatMatchesTheMinecraftVersion()
    {
        var files = new[]
        {
            File(100, "Iceberg-26.3-forge.jar", "26.3", "Forge"),
            File(90, "Iceberg-1.20.1-forge.jar", "1.20.1", "Forge"),
            File(80, "Iceberg-1.19.2-forge.jar", "1.19.2", "Forge"),
        };

        var best = CurseForgeFileSelection.SelectBest(files, "1.19.2", "Forge", ".jar");

        Assert.NotNull(best);
        Assert.Equal("Iceberg-1.19.2-forge.jar", best!.FileName);
    }

    [Fact]
    public void RefusesToInstallAFileForAnotherMinecraftVersion()
    {
        var files = new[]
        {
            File(100, "Iceberg-26.3-forge.jar", "26.3", "Forge"),
            File(90, "LibX-1.20.1.jar", "1.20.1", "Forge"),
        };

        Assert.Null(CurseForgeFileSelection.SelectBest(files, "1.19.2", "Forge", ".jar"));
    }

    [Fact]
    public void RespectsTheLoaderTag()
    {
        var files = new[]
        {
            File(100, "Thing-fabric-1.19.2.jar", "1.19.2", "Fabric"),
            File(90, "Thing-forge-1.19.2.jar", "1.19.2", "Forge"),
        };

        var best = CurseForgeFileSelection.SelectBest(files, "1.19.2", "Forge", ".jar");

        Assert.Equal("Thing-forge-1.19.2.jar", best!.FileName);
    }

    [Fact]
    public void WithoutAGameVersionItFallsBackToTheNewest()
    {
        var files = new[]
        {
            File(100, "Thing-26.3.jar", "26.3", "Forge"),
            File(90, "Thing-1.19.2.jar", "1.19.2", "Forge"),
        };

        var best = CurseForgeFileSelection.SelectBest(files, null, null, ".jar", requireCompatible: false);

        Assert.Equal("Thing-26.3.jar", best!.FileName);
    }

    [Fact]
    public void PrefersFilesWithTheExpectedExtension()
    {
        var files = new[]
        {
            File(100, "Thing-sources.jar", "1.19.2", "Forge"),
            File(90, "Thing.zip", "1.19.2", "Forge"),
        };

        var best = CurseForgeFileSelection.SelectBest(files, "1.19.2", "Forge", ".zip");

        Assert.Equal("Thing.zip", best!.FileName);
    }

    [Fact]
    public void IsCompatibleRequiresBothVersionAndLoaderWhenGiven()
    {
        var file = File(1, "x.jar", "1.19.2", "Forge");

        Assert.True(CurseForgeFileSelection.IsCompatible(file, "1.19.2", "Forge"));
        Assert.False(CurseForgeFileSelection.IsCompatible(file, "1.20.1", "Forge"));
        Assert.False(CurseForgeFileSelection.IsCompatible(file, "1.19.2", "Fabric"));
    }
}
