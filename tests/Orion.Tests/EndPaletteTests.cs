using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 末地配色：主岛要和外岛同色（末地石），虚空要明显区别于陆地区域。
///
/// 之前的配色是 cubiomes 默认：the_end=0x8080FF 浅紫、small_end_islands=0x4B4BAB 深紫，
/// 主岛看起来像一块空洞、四周像一圈围栏。现在按"看得懂"的方式重上色。
/// </summary>
public sealed class EndPaletteTests(ITestOutputHelper output)
{
    private const long Seed = 1;

    [Fact]
    public void CentralIslandIsEndStoneColored()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");

        var theEnd = CubiomesNative.BiomeColor(9);
        var highlands = CubiomesNative.BiomeColor(42);
        var voidIslands = CubiomesNative.BiomeColor(40);

        Assert.NotNull(theEnd);
        Assert.NotNull(highlands);
        Assert.NotNull(voidIslands);

        output.WriteLine($"the_end        = ({theEnd!.Value.R},{theEnd.Value.G},{theEnd.Value.B})");
        output.WriteLine($"end_highlands  = ({highlands!.Value.R},{highlands.Value.G},{highlands.Value.B})");
        output.WriteLine($"small_end_islands = ({voidIslands!.Value.R},{voidIslands.Value.G},{voidIslands.Value.B})");

        // 主岛必须是亮色（末地石），不能是原来那种浅紫
        Assert.True(theEnd.Value.R > 0xB0 && theEnd.Value.G > 0xA0,
            "主岛应该用末地石亮色，而不是原来的浅紫");

        // 虚空必须足够暗，和陆地拉开差距
        var voidBrightness = (voidIslands.Value.R + voidIslands.Value.G + voidIslands.Value.B) / 3;
        Assert.True(voidBrightness < 0x40, $"虚空颜色太亮（{voidBrightness}），看起来会像陆地");

        // 主岛亮度要明显高于虚空
        var landBrightness = (theEnd.Value.R + theEnd.Value.G + theEnd.Value.B) / 3;
        Assert.True(landBrightness - voidBrightness > 0x80, "主岛与虚空的亮度差不够明显");

        // 主岛与外岛应当接近（都是末地石）
        var highlandBrightness = (highlands.Value.R + highlands.Value.G + highlands.Value.B) / 3;
        Assert.True(Math.Abs(landBrightness - highlandBrightness) < 0x40, "主岛与外岛颜色差异过大");
    }

    [Fact]
    public void RenderedEndCenterUsesEndStoneColor()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int size = 32;

        var rgba = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimEnd, 0, 0, 16, size, size);
        Assert.NotNull(rgba);

        var center = (size / 2 * size + size / 2) * 4;
        var r = rgba![center];
        var g = rgba[center + 1];
        var b = rgba[center + 2];

        output.WriteLine($"末地中心渲染像素 = ({r},{g},{b})");
        Assert.True(r > 0xB0 && g > 0xA0, "中心应当渲染成末地石色");
    }
}
