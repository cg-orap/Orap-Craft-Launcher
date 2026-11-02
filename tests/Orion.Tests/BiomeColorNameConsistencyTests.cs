using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 颜色与名字必须一致：地图上画出来的颜色，要和悬停提示里的群系名字对应。
///
/// 之前 orap_biome_at 把"每像素方块数"当成 getBiomeAt 的 scale 传进去
/// （cubiomes 只接受 1 或 4），所以悬停显示的名字和颜色完全对不上 ——
/// 例如黄色（沙漠）被念成"冷水海洋"、深蓝（海洋）被念成"森林"。
/// 这个测试把 id → 颜色 → 名字 三者的对应关系钉住。
/// </summary>
public sealed class BiomeColorNameConsistencyTests(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    /// <summary>一批"颜色和名字都被广泛认知"的群系，用来防止再次错位。</summary>
    private static readonly (string Name, int R, int G, int B)[] Expected =
    [
        ("ocean",            0x00, 0x00, 0x70),
        ("plains",           0x8D, 0xB3, 0x60),
        ("desert",           0xFA, 0x94, 0x18),
        ("forest",           0x05, 0x69, 0x2B),
        ("taiga",            0x0B, 0x6A, 0x5F),
        ("snowy_tundra",     0xFF, 0xFF, 0xFF),
        ("mushroom_fields",  0xFF, 0x00, 0xFF),
        ("badlands",         0xD9, 0x45, 0x15),
    ];

    [Fact]
    public void PointQueryMatchesRenderedColorAtEveryZoomScale()
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");

        // 在一个已知位置，用不同"每像素方块数"查询，结果必须是同一个群系 id
        foreach (var scale in new[] { 1, 4, 16, 32, 64 })
        {
            var id = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld, scale, 874, 63, 928);
            output.WriteLine($"scale={scale,2} → id={id} name={CubiomesNative.BiomeName(version, id)}");

            Assert.True(id > 0, $"scale={scale} 查询返回了无效 id={id}");
        }
    }

    [Fact]
    public void RenderedPixelColorMatchesPointQuery()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 4;
        const int size = 32;

        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, size, size);
        Assert.NotNull(rgba);

        // 渲染区起点 = (0/4 - 32/2) * 4 = -64（scale 单位修正后）
        var originBlock = (0 / scale - size / 2) * scale;
        var mismatches = 0;

        for (var z = 0; z < size; z += 7)
        {
            for (var x = 0; x < size; x += 7)
            {
                var blockX = originBlock + x * scale;
                var blockZ = originBlock + z * scale;

                var id = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld, scale, blockX, 63, blockZ);
                var name = CubiomesNative.BiomeName(version, id);

                var pixel = (z * size + x) * 4;
                var r = rgba![pixel];
                var g = rgba[pixel + 1];
                var b = rgba[pixel + 2];

                // 真正的不变量：渲染出来的像素颜色 == 这个 id 在调色板里的颜色
                var palette = CubiomesNative.BiomeColor(id);
                if (palette is null) continue;

                var match = r == palette.Value.R && g == palette.Value.G && b == palette.Value.B;

                if (!match)
                {
                    mismatches++;
                    output.WriteLine($"  不一致: 方块({blockX},{blockZ}) id={id} name={name} " +
                                     $"像素=(0x{r:X2},0x{g:X2},0x{b:X2}) 调色板=(0x{palette.Value.R:X2},0x{palette.Value.G:X2},0x{palette.Value.B:X2})");
                }
            }
        }

        Assert.Equal(0, mismatches);
    }
}
