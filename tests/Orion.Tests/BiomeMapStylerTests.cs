using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 群系图质感：程序化贴图 + 群系描边。
/// 关键约束：
/// · 必须确定性（同输入同输出，否则拖动时画面会闪）；
/// · 不能改变群系位置（描边只压暗边界像素，不移动边界）；
/// · 平均亮度不能明显偏移（否则整张图会变暗/发白）。
/// </summary>
public sealed class BiomeMapStylerTests
{
    private static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var buffer = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            buffer[i * 4 + 0] = r;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = b;
            buffer[i * 4 + 3] = 255;
        }

        return buffer;
    }

    [Fact]
    public void StylizeIsDeterministic()
    {
        var a = Solid(32, 32, 120, 180, 90);
        var b = Solid(32, 32, 120, 180, 90);

        BiomeMapStyler.Stylize(a, 32, 32, 0, 0, 4);
        BiomeMapStyler.Stylize(b, 32, 32, 0, 0, 4);

        Assert.Equal(a, b);
    }

    [Fact]
    public void BordersAreDarkenedOnlyAtBiomeEdges()
    {
        // 左半绿色、右半蓝色：分界线左右各一列会被压暗，内部不受影响
        const int width = 32;
        const int height = 8;
        var buffer = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var grass = x < width / 2;

                buffer[i + 0] = (byte)(grass ? 120 : 40);
                buffer[i + 1] = (byte)(grass ? 180 : 70);
                buffer[i + 2] = (byte)(grass ? 90 : 200);
                buffer[i + 3] = 255;
            }
        }

        var original = (byte[])buffer.Clone();

        BiomeMapStyler.Stylize(buffer, width, height, 0, 0, 4, textures: false, borders: true);

        // 远处的内部像素完全不变
        var inside = (0 * width + 2) * 4;
        Assert.Equal(original[inside], buffer[inside]);
        Assert.Equal(original[inside + 1], buffer[inside + 1]);

        // 边界两侧至少有一列被压暗
        var edgeLeft = (0 * width + (width / 2 - 1)) * 4;
        var edgeRight = (0 * width + (width / 2)) * 4;
        var darkened = buffer[edgeLeft] < original[edgeLeft] || buffer[edgeRight] < original[edgeRight];

        Assert.True(darkened, "群系边界应该被压暗");
    }

    [Fact]
    public void TexturesDoNotShiftAverageBrightnessFar()
    {
        var buffer = Solid(64, 64, 100, 160, 80);
        var original = (byte[])buffer.Clone();

        BiomeMapStyler.Stylize(buffer, 64, 64, 0, 0, 4, textures: true, borders: false);

        double sumBefore = 0, sumAfter = 0;
        for (var i = 0; i < original.Length; i += 4)
        {
            sumBefore += original[i + 1];
            sumAfter += buffer[i + 1];
        }

        var ratio = sumAfter / sumBefore;

        Assert.InRange(ratio, 0.85, 1.15);
    }

    [Fact]
    public void DistinctBiomesGetDistinctTextures()
    {
        // 水面（高蓝）与雪原（很亮）应该产生不同的纹理结果
        var water = Solid(16, 16, 40, 90, 190);
        var snow = Solid(16, 16, 240, 245, 250);

        BiomeMapStyler.Stylize(water, 16, 16, 0, 0, 4, textures: true, borders: false);
        BiomeMapStyler.Stylize(snow, 16, 16, 0, 0, 4, textures: true, borders: false);

        Assert.NotEqual(water, snow);
    }

    [Fact]
    public void StylizeToleratesTinyAndShortBuffers()
    {
        var tiny = Solid(1, 1, 10, 20, 30);
        BiomeMapStyler.Stylize(tiny, 1, 1, 0, 0, 1);

        var shortBuffer = new byte[8];
        BiomeMapStyler.Stylize(shortBuffer, 64, 64, 0, 0, 1);
    }

    [Fact]
    public void StylizeLeavesTransparentPixelsAlone()
    {
        var buffer = Solid(8, 8, 100, 100, 100);
        buffer[3] = 0;   // 第一个像素全透明

        BiomeMapStyler.Stylize(buffer, 8, 8, 0, 0, 4);

        Assert.Equal(0, buffer[3]);
    }
}
