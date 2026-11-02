using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 用"同一座岛在不同渲染尺寸下直径必须相同"这条不变量，
/// 把 cubiomes 末地高度图 mapEndSurfaceHeight 的坐标单位试出来。
///
/// unitMode: 0 = 方块坐标，1 = scale 单位，2 = 1:4 坐标
/// 只有正确的那一种，才会在 32/64/128 三种网格下量出同一个直径。
/// </summary>
public sealed class EndHeightUnitTests(ITestOutputHelper output)
{
    private const long Seed = 1;

    /// <summary>返回 -1 表示岛被视野截断（此时不能用来比较直径）。</summary>
    private static int MeasureDiameter(float[] heights, int width, int height, int scale)
    {
        var row = height / 2;
        var first = -1;
        var last = -1;

        for (var x = 0; x < width; x++)
        {
            if (heights[row * width + x] <= 0) continue;
            if (first < 0) first = x;
            last = x;
        }

        if (first < 0) return 0;
        if (first == 0 || last == width - 1) return -1;   // 触边 = 被截断

        return (last - first + 1) * scale;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DiameterIsInvariantAcrossGridSizes(int unitMode)
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 4;

        // 三种网格，覆盖范围分别是 128 / 256 / 512 方块（都以原点为中心）
        var sizes = new[] { (Width: 32, Height: 32), (Width: 64, Height: 64), (Width: 128, Height: 128) };
        var diameters = new List<int>();

        foreach (var (width, height) in sizes)
        {
            var heights = CubiomesNative.DebugEndHeights(Seed, version, 0, 0, scale, width, height, unitMode);
            Assert.NotNull(heights);

            var diameter = MeasureDiameter(heights!, width, height, scale);

            if (diameter > 0) diameters.Add(diameter);

            var landCells = heights!.Count(h => h > 0);
            output.WriteLine($"unitMode={unitMode} 网格={width}×{height}（视野 {width * scale} 方块）" +
                             $" → 直径 {diameter} 方块，陆地格 {landCells}");
        }

        // 视野比岛大之后，直径必须稳定
        var big = diameters.Where(d => d > 0).ToList();

        if (big.Count >= 2)
        {
            var spread = big.Max() - big.Min();
            output.WriteLine($"unitMode={unitMode} 直径波动 = {spread} 方块");

            Assert.True(spread <= 32, $"unitMode={unitMode} 直径随尺寸变化 {spread} 方块，单位不对");
        }
    }
}
