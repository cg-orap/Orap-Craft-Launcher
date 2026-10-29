using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

public sealed class SeedMapTests
{
    /// <summary>
    /// 黄金值来自真实 JDK：用 java.util.Random + 同一公式在 -8..8 的区块范围内枚举得到。
    /// 只要公式或随机数实现有任何一位偏差，这里的集合就对不上。
    /// </summary>
    [Theory]
    [InlineData(42L, 31, "(0,0)", "(-1,-8)", "(8,7)")]
    [InlineData(1L, 42, "(-1,-1)", "(3,8)", "(-8,-6)")]
    [InlineData(-123456789L, 34, "(-4,-8)", "(3,-3)", "(-1,8)")]
    [InlineData(987654321987L, 26, "(0,-8)", "(-5,-2)", "(-3,8)")]
    public void SlimeChunksMatchRealJava(long seed, int expectedCount, string mustContainA, string mustContainB, string mustContainC)
    {
        var chunks = SeedMap.SlimeChunks(seed, -8, -8, 8, 8);
        Assert.Equal(expectedCount, chunks.Count);

        var set = chunks.Select(c => $"({c.X},{c.Z})").ToHashSet();
        foreach (var expected in new[] { mustContainA, mustContainB, mustContainC })
        {
            Assert.Contains(expected, set);
        }
    }

    [Fact]
    public void KnownCoordinatesForSeed42()
    {
        Assert.True(SeedMap.IsSlimeChunk(42, 0, 0));
        Assert.True(SeedMap.IsSlimeChunk(42, -1, -8));
        Assert.False(SeedMap.IsSlimeChunk(42, 0, 1) && !SeedMap.IsSlimeChunk(42, 0, 0));
        Assert.True(SeedMap.IsSlimeChunk(42, 0, 1));
    }

    [Fact]
    public void NegativeSeedsAndFarChunksDoNotThrow()
    {
        // 原版公式里有 int 乘法溢出，远区块也必须照原样溢出而不是抛异常。
        _ = SeedMap.IsSlimeChunk(long.MinValue, 1_000_000, -1_000_000);
        _ = SeedMap.IsSlimeChunk(long.MaxValue, -46340, 46340);
        Assert.InRange(SeedMap.SlimeChunks(12345, -64, -64, 64, 64).Count, 1, 65 * 65);
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("  -7 ", -7L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void ParseNumericSeeds(string text, long expected)
    {
        Assert.True(SeedMap.TryParseSeed(text, out var seed));
        Assert.Equal(expected, seed);
    }

    [Fact]
    public void ParseTextSeedUsesJavaHashCode()
    {
        // "abc".hashCode() == 96354，与原版对文本种子的处理一致。
        Assert.True(SeedMap.TryParseSeed("abc", out var seed));
        Assert.Equal(96354L, seed);

        Assert.False(SeedMap.TryParseSeed("   ", out _));
    }

    [Fact]
    public void DescribeMentionsBlocksAndSlime()
    {
        var text = SeedMap.Describe(3, -2, slime: true);
        Assert.Contains("48", text);      // 3 * 16
        Assert.Contains("-32", text);     // -2 * 16
        Assert.Contains("史莱姆", text);
    }
}