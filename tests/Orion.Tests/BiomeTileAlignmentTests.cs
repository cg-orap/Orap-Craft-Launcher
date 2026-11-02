using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 瓦片对齐的自检：如果用"中心 + 尺寸"分别渲染两块相邻瓦片，
/// 它们的边界列必须和"一次渲染整条宽图"的结果完全一致。
///
/// 这个测试是瓦片渲染能否成立的前提 —— 只要边界不一致，
/// 画面上就一定会出现"一块一块、内容错位"的接缝。
/// </summary>
public sealed class BiomeTileAlignmentTests(ITestOutputHelper output)
{
    private const long Seed = 123456789L;

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void AdjacentTilesMatchASingleWideRender(int scale)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生引擎不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int width = 256;                 // 每块瓦片 256 像素
        var span = width * scale;              // 一块瓦片覆盖的方块数

        // 一次渲染 512 像素宽（= 两块瓦片），中心在 0
        var wide = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, width * 2, 8);
        Assert.NotNull(wide);

        // 左瓦片：中心 -span/2；右瓦片：中心 +span/2
        var left = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, -span / 2, 0, scale, width, 8);
        var right = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, span / 2, 0, scale, width, 8);

        Assert.NotNull(left);
        Assert.NotNull(right);

        // 比较第一行：宽图第 255/256 列 应与 左块最后一列 / 右块第一列 相同
        for (var row = 0; row < 8; row++)
        {
            var wideRow = row * (width * 2) * 4;

            for (var channel = 0; channel < 3; channel++)
            {
                var wideLeftEdge = wide[wideRow + (width - 1) * 4 + channel];
                var leftTileEdge = left[row * width * 4 + (width - 1) * 4 + channel];

                var wideRightEdge = wide[wideRow + width * 4 + channel];
                var rightTileFirst = right[row * width * 4 + channel];

                Assert.Equal(wideLeftEdge, leftTileEdge);
                Assert.Equal(wideRightEdge, rightTileFirst);
            }
        }
    }

    [Fact]
    public void TileOriginIsAlignedToScale()
    {
        // 原生区域起点 = center - (cellsX/2)*scale，必须是 scale 的整数倍，
        // 否则同一列在不同瓦片里会被采到不同的群系格。
        foreach (var scale in new[] { 1, 4, 16, 64 })
        {
            const int width = 256;
            var span = width * scale;

            foreach (var tileIndex in new[] { 0, 1, -1, 7, -3 })
            {
                var center = tileIndex * span + span / 2;
                var origin = center - (width / 2) * scale;

                Assert.Equal(0, origin % scale);
                Assert.Equal(tileIndex * span, origin);
            }
        }
    }
}
