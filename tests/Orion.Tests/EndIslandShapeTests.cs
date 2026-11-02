using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 末地岛屿轮廓：主岛只应该是一小块（真实游戏里半径约 100 格），
/// 而不是群系 the_end 覆盖的半径 1024 格的大圆盘。
///
/// 实现方式是拿 cubiomes 的末地地表高度做遮罩：高度 &lt;= 0 的地方就是虚空。
/// </summary>
public sealed class EndIslandShapeTests(ITestOutputHelper output)
{
    private const long Seed = 1;

    [Fact]
    public void CentralIslandIsAboutTwoHundredBlocksAcross()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 4;
        const int size = 128;      // 128 格 × 4 = 512 方块视野

        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimEnd, 0, 0, scale, size, size);
        Assert.NotNull(rgba);

        // 沿中心行扫描，统计非虚空的连续长度（= 主岛直径）
        var row = size / 2;
        var landPixels = 0;
        var first = -1;
        var last = -1;

        for (var x = 0; x < size; x++)
        {
            var i = (row * size + x) * 4;
            var r = rgba![i];
            var g = rgba[i + 1];
            var b = rgba[i + 2];

            // 虚空色是 (0x12,0x0E,0x1C)，很暗；陆地是末地石亮色
            var isVoid = r < 0x40 && g < 0x40 && b < 0x40;

            if (isVoid) continue;

            landPixels++;
            if (first < 0) first = x;
            last = x;
        }

        var diameterBlocks = (last - first + 1) * scale;

        output.WriteLine($"主岛直径约 {diameterBlocks} 方块（扫描到 {landPixels} 个陆地像素）");

        // 真实末地主岛约 200 格；给一个宽容区间
        Assert.InRange(diameterBlocks, 80, 400);
    }

    [Fact]
    public void FarAwayIsVoid()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");

        // 距原点 300 格处应该是虚空（主岛半径约 100）
        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimEnd, 300, 0, 4, 8, 8);
        Assert.NotNull(rgba);

        var i = (4 * 8 + 4) * 4;
        var isVoid = rgba![i] < 0x40 && rgba[i + 1] < 0x40 && rgba[i + 2] < 0x40;

        output.WriteLine($"距原点 300 格的中心像素 = ({rgba[i]},{rgba[i + 1]},{rgba[i + 2]})，虚空={isVoid}");
        Assert.True(isVoid, "主岛外 300 格应当已经是虚空");
    }
}
