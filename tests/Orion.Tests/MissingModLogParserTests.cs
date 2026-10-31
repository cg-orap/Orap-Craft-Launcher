using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 从 Forge 报错日志里认出"缺哪个前置"。这段解析错了就补错模组，所以用真实报错格式锁住。
/// </summary>
public sealed class MissingModLogParserTests
{
    [Fact]
    public void ParsesTheClassicForgeMissingDependencyPair()
    {
        var lines = new[]
        {
            "Mod legendarytooltips requires iceberg 1.1.2 or above",
            "Currently, iceberg is not installed",
            "Mod advancementplaques requires iceberg 1.1.44 or above",
            "Currently, iceberg is not installed",
            "Mod itemBorders requires iceberg 1.0.46 or above",
            "Currently, iceberg is not installed",
        };

        var missing = MissingModLogParser.Parse(lines);

        var iceberg = Assert.Single(missing);
        Assert.Equal("iceberg", iceberg.ModId);
        Assert.Equal("legendarytooltips", iceberg.RequiredBy);
    }

    [Fact]
    public void DistinguishesMultipleMissingMods()
    {
        var lines = new[]
        {
            "Mod botanicalmachinery requires libx 1.19.2-4.2.0 or above",
            "Currently, libx is not installed",
            "Mod sandbox requires libx 1.19.2-4.2.5 or above",
            "Currently, libx is not installed",
        };

        var missing = MissingModLogParser.Parse(lines);

        Assert.Single(missing);
        Assert.Equal("libx", missing[0].ModId);
        Assert.Equal("botanicalmachinery", missing[0].RequiredBy);
    }

    [Fact]
    public void IgnoresLoaderAndMinecraftItself()
    {
        var lines = new[]
        {
            "Mod something requires forge 43.3.0 or above",
            "Currently, forge is not installed",
            "Mod something requires minecraft 1.19.2 or above",
            "Currently, minecraft is not installed",
        };

        Assert.Empty(MissingModLogParser.Parse(lines));
    }

    [Fact]
    public void OnlyRequiresWithoutNotInstalledIsNotReported()
    {
        // 只有 "requires" 说明依赖已满足，不该报缺失
        var lines = new[] { "Mod a requires iceberg 1.1.2 or above" };

        Assert.Empty(MissingModLogParser.Parse(lines));
    }

    [Fact]
    public void ReportsUnknownRequesterWhenOnlyTheNotInstalledLineIsPresent()
    {
        var lines = new[] { "Currently, libx is not installed" };

        var missing = MissingModLogParser.Parse(lines);

        Assert.Single(missing);
        Assert.Equal("libx", missing[0].ModId);
    }
}
