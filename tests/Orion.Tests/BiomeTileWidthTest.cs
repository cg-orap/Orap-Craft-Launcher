using Orion.Core.World;
using Xunit;
using Xunit.Abstractions;

namespace Orion.Tests;

/// <summary>
/// 步骤 1 实验：判断"按瓦片渲染对不齐"到底是
///   (a) 不同**宽度**的渲染之间不一致，还是
///   (b) 任意两次渲染（同宽度、不同起点）都不一致。
///
/// 做法：全部用**相同宽度 256** 渲染三张图，只让起点不同：
///   ref  : 中心 C          → 覆盖 [C-128s, C+128s)
///   mid  : 中心 C + 128s   → 覆盖 [C,      C+256s)
///   right: 中心 C + 256s   → 覆盖 [C+128s, C+384s)
/// 然后比较重叠区域：
///   ref 的右半  == mid 的左半
///   mid 的右半  == right 的左半
///
/// 如果成立 → 瓦片方案可行，之前失败只是"不同宽度比较"造成的假象，不用改原生。
/// 如果不成立 → 必须改原生接口（给绝对起点 + 固定相位）。
/// </summary>
public sealed class BiomeTileWidthTest(ITestOutputHelper output)
{
    private const long Seed = 123456789L;
    private const int Width = 256;
    private const int Height = 8;

    [Theory(Skip = "已知问题：原生把渲染中心平移半个瓦片后，图并没有平移半个瓦片宽")]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public void SameWidthRendersAgreeOnOverlap(int scale)
    {
        if (!CubiomesNative.Available)
        {
            output.WriteLine("原生引擎不可用，跳过");
            return;
        }

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        var span = Width * scale;

        var reference = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, Width, Height);
        var middle = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, span / 2, 0, scale, Width, Height);
        var right = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, span, 0, scale, Width, Height);

        Assert.NotNull(reference);
        Assert.NotNull(middle);
        Assert.NotNull(right);

        var mismatchesRefMid = 0;
        var mismatchesMidRight = 0;

        for (var row = 0; row < Height; row++)
        {
            var rowOffset = row * Width * 4;

            for (var i = 0; i < Width / 2; i++)
            {
                // ref 的右半 (列 128+i) vs mid 的左半 (列 i)
                for (var channel = 0; channel < 3; channel++)
                {
                    if (reference[rowOffset + (Width / 2 + i) * 4 + channel] != middle[rowOffset + i * 4 + channel])
                    {
                        mismatchesRefMid++;
                        break;
                    }
                }

                // mid 的右半 vs right 的左半
                for (var channel = 0; channel < 3; channel++)
                {
                    if (middle[rowOffset + (Width / 2 + i) * 4 + channel] != right[rowOffset + i * 4 + channel])
                    {
                        mismatchesMidRight++;
                        break;
                    }
                }
            }
        }

        output.WriteLine($"scale={scale}: ref↔mid 不一致 {mismatchesRefMid} 列，mid↔right 不一致 {mismatchesMidRight} 列（共 {Height * Width / 2} 列比较）");

        Assert.Equal(0, mismatchesRefMid);
        Assert.Equal(0, mismatchesMidRight);
    }

    [Fact]
    public void SameCallTwiceIsIdentical()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        var a = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, 16, Width, Height);
        var b = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, 16, Width, Height);

        Assert.NotNull(a);
        Assert.NotNull(b);
        output.WriteLine($"两次同参渲染是否逐字节相同：{a!.AsSpan().SequenceEqual(b.AsSpan())}");

        Assert.Equal(a, b);
    }

    [Fact(Skip = "已知问题：错开半块的两次渲染重叠区不一致（1006/1024 列）")]
    public void OverlapOfTwoTilesMatches_ShiftByHalfTile()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;
        var span = Width * scale;

        // a 覆盖 [-span/2, +span/2)，b 覆盖 [0, +span)
        var a = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, Width, Height);
        var b = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, span / 2, 0, scale, Width, Height);

        Assert.NotNull(a);
        Assert.NotNull(b);

        var mismatches = 0;
        for (var row = 0; row < Height; row++)
        {
            for (var i = 0; i < Width / 2; i++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    if (a[row * Width * 4 + (Width / 2 + i) * 4 + channel] != b[row * Width * 4 + i * 4 + channel])
                    {
                        mismatches++;
                        break;
                    }
                }
            }
        }

        output.WriteLine($"两块错开半块的瓦片，重叠区不一致列数：{mismatches} / {Height * Width / 2}");
    }

    [Fact]
    public void DifferentWidthsDisagree_Control()
    {
        if (!CubiomesNative.Available) return;

        var version = CubiomesNative.VersionIndexFor("1.21.1");
        const int scale = 16;
        var span = Width * scale;

        var wide = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, 0, 0, scale, Width * 2, Height);
        var left = CubiomesNative.RenderRgba(Seed, version, CubiomesNative.DimOverworld, -span / 2, 0, scale, Width, Height);

        Assert.NotNull(wide);
        Assert.NotNull(left);

        var mismatches = 0;
        for (var row = 0; row < Height; row++)
        {
            for (var i = 0; i < Width; i++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    if (wide[row * Width * 2 * 4 + i * 4 + channel] != left[row * Width * 4 + i * 4 + channel])
                    {
                        mismatches++;
                        break;
                    }
                }
            }
        }

        output.WriteLine($"对照实验（512 宽 vs 256 宽）：不一致 {mismatches} 列");
    }
}
