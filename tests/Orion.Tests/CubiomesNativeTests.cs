using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// cubiomes 原生引擎的集成测试。
///
/// DLL 不存在时（CI 或未编译 native 的机器）这些用例会跳过断言而不是失败——启动器本身在
/// 缺 DLL 时回退到简化模型，测试也应保持一致。
/// </summary>
public sealed class CubiomesNativeTests
{
    [Fact]
    public void EngineLoadsAndReportsVersions()
    {
        if (!CubiomesNative.Available)
        {
            Assert.False(string.IsNullOrWhiteSpace(CubiomesNative.LoadError));
            return;
        }

        Assert.NotEmpty(CubiomesNative.Versions);
        Assert.Contains(CubiomesNative.Versions, v => v.StartsWith("1.21"));
        Assert.Contains("1.18.2", CubiomesNative.Versions);
    }

    [Fact]
    public void VersionMappingFallsBackToNewestForUnknownVersions()
    {
        if (!CubiomesNative.Available) return;

        Assert.Equal(0, CubiomesNative.VersionIndexFor("1.21.4"));
        var modern = CubiomesNative.VersionIndexFor("26.3");
        Assert.InRange(modern, 0, CubiomesNative.Versions.Count - 1);

        var old = CubiomesNative.VersionIndexFor("1.12.2");
        Assert.Equal(CubiomesNative.Versions.ToList().IndexOf("1.12.2"), old);
    }

    /// <summary>
    /// cubiomes README 的示例：种子 262 在 (0,0) 附近应有蘑菇岛（1.18）。
    /// 这是"引擎真的在跑原版算法"的关键证据。
    /// </summary>
    [Fact]
    public void Seed262HasMushroomFieldsNearOriginOn1_18()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.18.2");
        var id = CubiomesNative.BiomeAt(262, version, CubiomesNative.DimOverworld, 1, 0, 63, 0);
        Assert.Equal(14, id);   // mushroom_fields
        Assert.Equal("mushroom_fields", CubiomesNative.BiomeName(version, id));
    }

    [Fact]
    public void RenderedTileIsOpaqueAndVaried()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.4");
        var pixels = CubiomesNative.RenderRgba(42, version, CubiomesNative.DimOverworld, 0, 0, 16, 48, 32);
        Assert.NotNull(pixels);
        Assert.Equal(48 * 32 * 4, pixels!.Length);

        var colors = new HashSet<uint>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 3]);
            colors.Add(0xFF000000u | ((uint)pixels[i] << 16) | ((uint)pixels[i + 1] << 8) | pixels[i + 2]);
        }

        Assert.True(colors.Count >= 3, $"瓦片里只有 {colors.Count} 种颜色，疑似没在生成群系");
    }

    [Fact]
    public void SpawnIsNearOriginForTypicalSeed()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.4");
        var spawn = CubiomesNative.Spawn(42, version);
        Assert.NotNull(spawn);
        Assert.InRange(spawn!.Value.X, -20_000, 20_000);
        Assert.InRange(spawn.Value.Z, -20_000, 20_000);
    }

    [Fact]
    public void BiomeNamesMapToChinese()
    {
        Assert.Equal("蘑菇岛", BiomeCatalog.FromCubiomesName("mushroom_fields"));
        Assert.Equal("平原", BiomeCatalog.FromCubiomesName("minecraft:plains"));
        // 未收录的名字原样返回，避免把新群系显示成"未知"
        Assert.Equal("some_new_biome", BiomeCatalog.FromCubiomesName("some_new_biome"));
        Assert.Null(BiomeCatalog.FromCubiomesName(""));
    }
}
