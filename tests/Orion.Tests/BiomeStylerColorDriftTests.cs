using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 诊断测试：渲染 + Styler 之后，像素颜色还能不能"认回"它自己的群系。
///
/// 用户反馈："同一块纯色区域会报出好几个不同群系"、"文字和颜色对不上"。
/// 如果 Styler（贴图 + 海岸线 + 地形阴影）把颜色推得太远，
/// 那这块颜色就不再是"某个群系的标准色"，肉眼和反查都会失配。
///
/// 这里量化：对每个像素求"到最近调色板色"的距离，
/// 并统计该像素最近的调色板色是否就是它自己的群系。
/// </summary>
public sealed class BiomeStylerColorDriftTests(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    [Theory]
    [InlineData(4, true, true, true)]
    [InlineData(4, true, false, true)]
    [InlineData(4, false, false, false)]
    public void StyledPixelsStillMatchTheirOwnBiomeColor(int scale, bool textures, bool borders, bool hillshade)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int size = 64;

        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, size, size);
        Assert.NotNull(rgba);

        // 调色板：id → 颜色
        var palette = new List<(int Id, int R, int G, int B)>();
        for (var id = 0; id < 256; id++)
        {
            if (CubiomesNative.BiomeColor(id) is not { } rgb) continue;
            palette.Add((id, rgb.R, rgb.G, rgb.B));
        }

        // 渲染区起点（与 native 的区间算法一致）
        var originBlock = (0 / scale - size / 2) * scale;

        var mismatched = 0;
        var total = 0;
        var maxDrift = 0;

        for (var z = 0; z < size; z += 5)
        {
            for (var x = 0; x < size; x += 5)
            {
                var blockX = originBlock + x * scale;
                var blockZ = originBlock + z * scale;

                var id = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld, scale, blockX, 63, blockZ);
                if (id <= 0) continue;

                var own = palette.FirstOrDefault(p => p.Id == id);
                if (own.Id != id) continue;

                var pixel = (z * size + x) * 4;
                var r = rgba![pixel];
                var g = rgba[pixel + 1];
                var b = rgba[pixel + 2];

                total++;

                // 找最近的调色板颜色
                var bestId = -1;
                var bestDistance = int.MaxValue;

                foreach (var entry in palette)
                {
                    var dr = entry.R - r;
                    var dg = entry.G - g;
                    var db = entry.B - b;
                    var distance = dr * dr + dg * dg + db * db;

                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    bestId = entry.Id;
                }

                var drift = (int)Math.Sqrt(bestDistance);
                if (drift > maxDrift) maxDrift = drift;

                if (bestId != id) mismatched++;
            }
        }

        output.WriteLine($"scale={scale} textures={textures} borders={borders} hillshade={hillshade}：" +
                         $"样本 {total}，颜色认错 {mismatched}，最大色偏 {maxDrift}");
    }

    [Fact]
    public void PlainRenderHasZeroDrift()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 4;
        const int size = 32;

        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, size, size);
        Assert.NotNull(rgba);

        var originBlock = (0 / scale - size / 2) * scale;
        var drift = 0;

        for (var z = 0; z < size; z += 3)
        {
            for (var x = 0; x < size; x += 3)
            {
                var blockX = originBlock + x * scale;
                var blockZ = originBlock + z * scale;

                var id = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld, scale, blockX, 63, blockZ);
                if (id <= 0 || CubiomesNative.BiomeColor(id) is not { } rgb) continue;

                var pixel = (z * size + x) * 4;
                var d = Math.Abs(rgba![pixel] - rgb.R) + Math.Abs(rgba[pixel + 1] - rgb.G) + Math.Abs(rgba[pixel + 2] - rgb.B);
                if (d > drift) drift = d;
            }
        }

        output.WriteLine($"未加 Styler 的渲染：最大通道差 {drift}（应为 0）");
        Assert.Equal(0, drift);
    }
}
