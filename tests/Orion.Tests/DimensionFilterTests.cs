using Orion.Core.World;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 维度过滤：确保"下界只出现下界的东西、末地只出现末地的东西、主世界不出现另两个维度"。
/// 这段逻辑之前写在 ViewModel 里出过两次错（主世界分支写成全留、边枚举边改集合），
/// 现在挪到 Core 并用测试钉住。
/// </summary>
public sealed class DimensionFilterTests
{
    [Theory]
    [InlineData("plains", true)]
    [InlineData("ocean", true)]
    [InlineData("forest", true)]
    [InlineData("mushroom_fields", true)]
    [InlineData("nether_wastes", false)]
    [InlineData("soul_sand_valley", false)]
    [InlineData("crimson_forest", false)]
    [InlineData("warped_forest", false)]
    [InlineData("basalt_deltas", false)]
    [InlineData("the_end", false)]
    [InlineData("end_highlands", false)]
    [InlineData("end_barrens", false)]
    [InlineData("small_end_islands", false)]
    [InlineData("the_void", false)]
    public void OverworldExcludesNetherAndEndBiomes(string name, bool expected)
        => Assert.Equal(expected, DimensionFilter.IsBiomeInDimension(name, DimensionFilter.Overworld));

    [Theory]
    [InlineData("nether_wastes", true)]
    [InlineData("soul_sand_valley", true)]
    [InlineData("crimson_forest", true)]
    [InlineData("warped_forest", true)]
    [InlineData("basalt_deltas", true)]
    [InlineData("plains", false)]
    [InlineData("the_end", false)]
    [InlineData("end_barrens", false)]
    public void NetherKeepsOnlyNetherBiomes(string name, bool expected)
        => Assert.Equal(expected, DimensionFilter.IsBiomeInDimension(name, DimensionFilter.Nether));

    [Theory]
    [InlineData("the_end", true)]
    [InlineData("end_highlands", true)]
    [InlineData("end_midlands", true)]
    [InlineData("small_end_islands", true)]
    [InlineData("the_void", true)]
    [InlineData("plains", false)]
    [InlineData("nether_wastes", false)]
    public void EndKeepsOnlyEndBiomes(string name, bool expected)
        => Assert.Equal(expected, DimensionFilter.IsBiomeInDimension(name, DimensionFilter.End));

    [Theory]
    [InlineData("village", DimensionFilter.Overworld, true)]
    [InlineData("stronghold", DimensionFilter.Overworld, true)]
    [InlineData("fortress", DimensionFilter.Overworld, false)]
    [InlineData("bastion", DimensionFilter.Overworld, false)]
    [InlineData("ruined_portal_nether", DimensionFilter.Overworld, false)]
    [InlineData("end_city", DimensionFilter.Overworld, false)]
    [InlineData("end_ship", DimensionFilter.Overworld, false)]
    [InlineData("fortress", DimensionFilter.Nether, true)]
    [InlineData("bastion", DimensionFilter.Nether, true)]
    [InlineData("ruined_portal_nether", DimensionFilter.Nether, true)]
    [InlineData("village", DimensionFilter.Nether, false)]
    [InlineData("end_city", DimensionFilter.Nether, false)]
    [InlineData("end_city", DimensionFilter.End, true)]
    [InlineData("end_ship", DimensionFilter.End, true)]
    [InlineData("end_gateway", DimensionFilter.End, true)]
    [InlineData("fortress", DimensionFilter.End, false)]
    [InlineData("village", DimensionFilter.End, false)]
    public void StructuresFollowDimension(string key, int dimension, bool expected)
        => Assert.Equal(expected, DimensionFilter.IsStructureInDimension(key, dimension));

    [Fact]
    public void NullOrEmptyIsRejected()
    {
        Assert.False(DimensionFilter.IsBiomeInDimension(null, DimensionFilter.Overworld));
        Assert.False(DimensionFilter.IsBiomeInDimension("  ", DimensionFilter.Overworld));
        Assert.False(DimensionFilter.IsStructureInDimension(null, DimensionFilter.Nether));
    }
}
