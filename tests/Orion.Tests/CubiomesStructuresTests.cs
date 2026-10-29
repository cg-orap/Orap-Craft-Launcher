using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

/// <summary>结构与出生点（原生引擎）。DLL 缺失时整组跳过断言。</summary>
public sealed class CubiomesStructuresTests
{
    private static int Version => CubiomesNative.VersionIndexFor("1.21.4");

    [Fact]
    public void StructureCatalogueCoversThreeDimensions()
    {
        if (!CubiomesNative.Available) return;

        var types = CubiomesNative.StructureTypes;
        Assert.True(types.Count >= 15, $"结构类型只有 {types.Count} 个");

        Assert.Contains(types, t => t.Key == "village" && t.Dimension == CubiomesNative.DimOverworld);
        Assert.Contains(types, t => t.Key == "fortress" && t.Dimension == CubiomesNative.DimNether);
        Assert.Contains(types, t => t.Key == "end_city" && t.Dimension == CubiomesNative.DimEnd);
    }

    [Fact]
    public void OverworldHasVillagesAndStrongholds()
    {
        if (!CubiomesNative.Available) return;

        var structures = CubiomesNative.FindStructures(42, Version, CubiomesNative.DimOverworld, -200, -200, 200, 200);
        Assert.True(structures.Count > 20, $"±200 区块内只找到 {structures.Count} 个结构");
        Assert.Contains(structures, s => s.Key == "village");

        var strongholds = CubiomesNative.FindStrongholds(42, Version);
        Assert.NotEmpty(strongholds);
        // 1.9+ 首个要塞落在第一环（半径 1408–2688），留点余量到 3000
        Assert.InRange(Math.Abs(strongholds[0].X), 0, 3000);
        Assert.InRange(Math.Abs(strongholds[0].Z), 0, 3000);
    }

    [Fact]
    public void NetherHasFortressesAndBastions()
    {
        if (!CubiomesNative.Available) return;

        var structures = CubiomesNative.FindStructures(42, Version, CubiomesNative.DimNether, -200, -200, 200, 200);
        Assert.Contains(structures, s => s.Key == "fortress");
        Assert.Contains(structures, s => s.Key == "bastion");

        // 下界结构不应该污染主世界查询
        var overworld = CubiomesNative.FindStructures(42, Version, CubiomesNative.DimOverworld, -200, -200, 200, 200);
        Assert.DoesNotContain(overworld, s => s.Key is "fortress" or "bastion");
    }

    [Fact]
    public void EndHasCities()
    {
        if (!CubiomesNative.Available) return;

        var structures = CubiomesNative.FindStructures(42, Version, CubiomesNative.DimEnd, -200, -200, 200, 200);
        Assert.Contains(structures, s => s.Key == "end_city");
    }

    [Fact]
    public void SpawnIsReportedAndStableForASeed()
    {
        if (!CubiomesNative.Available) return;

        var first = CubiomesNative.Spawn(42, Version);
        var second = CubiomesNative.Spawn(42, Version);
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.InRange(Math.Abs(first!.Value.X), 0, 20_000);
    }
}