using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 用 id 数组（而不是颜色）逐格比对，找出"平移中心后图片没跟着平移"的真实错位量。
/// 做法：a 以中心 0 渲染，b 以中心 +span/2 渲染，
/// 在 -4..+4 的行列偏移里搜一遍，看哪个偏移能让重叠区完全一致。
/// </summary>
public sealed class BiomeTileOffsetTest(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    [Theory(Skip = "已知问题：两次不同起点的渲染没有可解释的平移关系")]
    [InlineData(4)]
    [InlineData(16)]
    public void FindShiftBetweenTwoRenderings(int scale)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int width = 64;
        const int height = 8;
        var span = width * scale;

        var a = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, width, height);
        var b = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, span / 2, 0, scale, width, height);

        Assert.NotNull(a);
        Assert.NotNull(b);

        // 期望：a 的右半（列 32..63）== b 的左半（列 0..31）
        var best = (dx: 0, dz: 0, matches: -1);
        var results = new List<string>();

        for (var dz = -2; dz <= 2; dz++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var matches = 0;
                var total = 0;

                for (var row = 0; row < height - 2; row++)
                {
                    for (var i = 0; i < width / 2 - 4; i++)
                    {
                        var ax = width / 2 + i;
                        var az = row;
                        var bx = i + dx;
                        var bz = row + dz;

                        if (bx < 0 || bx >= width || bz < 0 || bz >= height) continue;

                        total++;
                        if (a[az * width + ax] == b[bz * width + bx]) matches++;
                    }
                }

                if (total == 0) continue;

                results.Add($"dx={dx,2} dz={dz,2} → {matches}/{total}");
                if (matches > best.matches) best = (dx, dz, matches);
            }
        }

        foreach (var line in results.OrderByDescending(r => r)) output.WriteLine(line);
        output.WriteLine($"最佳偏移：dx={best.dx} dz={best.dz}（匹配 {best.matches}）");

        // 如果最佳偏移不是 0，说明两次渲染之间存在固定错位
        Assert.Equal(0, best.dx);
        Assert.Equal(0, best.dz);
    }

    [Fact(Skip = "已知问题：同中心不同宽度内容不一致")]
    public void SameCenterDifferentWidthKeepsContent()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;

        // 同一中心、不同宽度：如果内容在重叠区一致，说明"起点相位"没问题，
        // 问题出在"把中心平移"这件事上。
        var small = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, 32, 4);
        var large = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, 64, 4);

        Assert.NotNull(small);
        Assert.NotNull(large);

        // small 覆盖 [-16*scale, +16*scale)，large 覆盖 [-32*scale, +32*scale)
        // small 的列 0..31 对应 large 的列 16..47
        var mismatches = 0;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 32; col++)
            {
                if (small![row * 32 + col] != large![row * 64 + 16 + col]) mismatches++;
            }
        }

        output.WriteLine($"同中心、32 vs 64 宽：重叠区不一致 {mismatches} / 128");

        // 也验证"列偏移 16"这个推断：如果列偏移不对，会大面积不一致
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void SinglePointMatchesRenderedCell()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;
        const int width = 16;
        const int height = 4;
        var span = width * scale;

        var ids = CubiomesNative.DebugBiomeIds(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, width, height);
        Assert.NotNull(ids);

        // 渲染区域起点 = 0 - (width/2)*scale
        var originX = -(width / 2) * scale;
        var originZ = -(height / 2) * scale;

        var checkX = 0;
        var checkZ = 0;
        var col = (checkX - originX) / scale;
        var row = (checkZ - originZ) / scale;

        var fromRender = ids![row * width + col];
        var fromSingle = CubiomesNative.BiomeAt(Seed, version, CubiomesNative.DimOverworld, scale,
            checkX, scale == 1 ? 63 : 15, checkZ);

        output.WriteLine($"方块({checkX},{checkZ}) 渲染 id={fromRender} 单点 id={fromSingle}");

        Assert.Equal(fromSingle, fromRender);
    }
}
