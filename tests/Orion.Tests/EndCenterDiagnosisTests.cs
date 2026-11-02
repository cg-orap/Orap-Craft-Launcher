using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 末地中心空洞的定位：在末地主岛中心与几个半径上用不同高度采样，
/// 看哪一个高度才是"岛面"。渲染用的是固定 y（1:4 的 15 → 方块 60），
/// 如果岛面不在这个高度，中心就会采到 the_void（看起来就是个空洞）。
/// </summary>
public sealed class EndCenterDiagnosisTests(ITestOutputHelper output)
{
    [Fact]
    public void ProbeEndCenterAtVariousHeights()
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");

        // 末地主岛：中心 (0,0)，半径约 1000 格
        var points = new (int X, int Z)[]
        {
            (0, 0), (16, 0), (64, 0), (220, 0), (500, 0), (900, 0), (1100, 0),
        };

        var heights = new[] { 4, 8, 16, 32, 48, 60, 64, 80, 100, 120 };

        foreach (var (x, z) in points)
        {
            var line = $"({x,4},{z,4}) : ";

            foreach (var y in heights)
            {
                var id = CubiomesNative.BiomeAt(seed: 1, version, CubiomesNative.DimEnd, 4, x, y, z);
                var name = CubiomesNative.BiomeName(version, id);
                line += $"y{y}={name} ";
            }

            output.WriteLine(line);
        }
    }

    [Fact]
    public void RenderedEndShowsVoidAtCenter()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int size = 64;
        const int scale = 16;

        // 渲染一片覆盖中心的地图，看中心像素是什么颜色
        var rgba = CubiomesNative.RenderRgba(1, version, CubiomesNative.DimEnd, 0, 0, scale, size, size);
        Assert.NotNull(rgba);

        var center = (size / 2 * size + size / 2) * 4;
        output.WriteLine($"中心像素 = ({rgba![center]},{rgba[center + 1]},{rgba[center + 2]})" +
                         $"（the_void 是 0,0,0；the_end 是 128,128,255）");

        var id = CubiomesNative.BiomeAt(1, version, CubiomesNative.DimEnd, 4, 0, 60, 0);
        output.WriteLine($"中心单点(方块 0,60,0) = {CubiomesNative.BiomeName(version, id)} (id={id})");
    }
}
