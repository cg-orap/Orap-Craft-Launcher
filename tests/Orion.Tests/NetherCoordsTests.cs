using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

public sealed class NetherCoordsTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(800, -800, 100, -100)]
    [InlineData(1000, 1000, 125, 125)]
    [InlineData(-1, -1, -1, -1)]      // 负数向下取整
    [InlineData(-8, 8, -1, 1)]
    [InlineData(7, -7, 0, -1)]
    public void OverworldToNetherDividesByEight(int x, int z, int expectedX, int expectedZ)
    {
        Assert.Equal((expectedX, expectedZ), MapViewportMath.ToNether(x, z));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(125, -100, 1000, -800)]
    [InlineData(-1, 1, -8, 8)]
    public void NetherToOverworldMultipliesByEight(int x, int z, int expectedX, int expectedZ)
    {
        Assert.Equal((expectedX, expectedZ), MapViewportMath.ToOverworld(x, z));
    }

    [Fact]
    public void RoundTripStaysWithinOneBlock()
    {
        foreach (var (x, z) in new[] { (12345, -6789), (-100, 100), (7, -7), (0, 0) })
        {
            var nether = MapViewportMath.ToNether(x, z);
            var back = MapViewportMath.ToOverworld(nether.X, nether.Z);
            Assert.InRange(Math.Abs(back.X - x), 0, 7);
            Assert.InRange(Math.Abs(back.Z - z), 0, 7);
        }
    }
}