using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

public sealed class SimplifiedBiomeMapTests
{
    [Fact]
    public void SameSeedAndPositionAlwaysGivesTheSameBiome()
    {
        for (var i = 0; i < 200; i++)
        {
            var x = i * 137 - 5000;
            var z = i * -211 + 3000;
            Assert.Equal(SimplifiedBiomeMap.Sample(42, x, z), SimplifiedBiomeMap.Sample(42, x, z));
        }

        // 不同种子必须给出不同结果（不是与种子无关的固定图）
        var different = 0;
        for (var i = 0; i < 200; i++)
        {
            if (SimplifiedBiomeMap.Sample(42, i * 64, i * 64) != SimplifiedBiomeMap.Sample(43, i * 64, i * 64)) different++;
        }
        Assert.True(different > 100, $"只有 {different}/200 个采样点随种子变化");
    }

    [Fact]
    public void LargeAreaContainsOceanLandAndMountains()
    {
        var kinds = new HashSet<BiomeKind>();
        var ocean = 0;
        var total = 0;

        for (var z = -6000; z <= 6000; z += 150)
        {
            for (var x = -6000; x <= 6000; x += 150)
            {
                var kind = SimplifiedBiomeMap.Sample(7, x, z);
                kinds.Add(kind);
                total++;
                if (kind is BiomeKind.Ocean or BiomeKind.DeepOcean) ocean++;
            }
        }

        Assert.True(kinds.Count >= 8, $"只出现了 {kinds.Count} 种群系：{string.Join(",", kinds)}");
        Assert.Contains(BiomeKind.Ocean, kinds);
        Assert.True(ocean > total / 20, $"海洋占比太低：{ocean}/{total}");
        Assert.True(ocean < total / 2, $"海洋占比太高：{ocean}/{total}");
    }

    [Fact]
    public void EveryBiomeHasADistinctOpaqueColour()
    {
        var colours = new HashSet<uint>();
        foreach (var kind in Enum.GetValues<BiomeKind>())
        {
            var color = SimplifiedBiomeMap.ColorOf(kind);
            Assert.Equal(0xFFu, color >> 24);
            Assert.True(colours.Add(color), $"{kind} 的颜色与其它群系重复");
            Assert.NotEqual("未知", SimplifiedBiomeMap.DisplayName(kind));
        }
    }

    [Fact]
    public void RenderProducesRgbaBufferOfTheRequestedSize()
    {
        var pixels = SimplifiedBiomeMap.RenderRgba(12345, 0, 0, blocksPerPixel: 8, width: 32, height: 16);
        Assert.Equal(32 * 16 * 4, pixels.Length);

        // 全部不透明，且颜色来自调色板
        var palette = Enum.GetValues<BiomeKind>().Select(SimplifiedBiomeMap.ColorOf).ToHashSet();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 3]);
            var color = 0xFF000000u | ((uint)pixels[i] << 16) | ((uint)pixels[i + 1] << 8) | pixels[i + 2];
            Assert.Contains(color, palette);
        }
    }

    [Fact]
    public void ZoomStepChangesWhatOnePixelCovers()
    {
        var near = SimplifiedBiomeMap.RenderRgba(99, 0, 0, 2, 16, 16);
        var far = SimplifiedBiomeMap.RenderRgba(99, 0, 0, 64, 16, 16);
        Assert.NotEqual(near, far);
    }
}