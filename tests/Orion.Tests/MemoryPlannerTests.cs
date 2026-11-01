using Orion.Core.Game;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 自动分配内存：要"舍得给"但不能把系统压死。
/// 规则（按玩家反馈调过）：基线看物理内存，按模组数量加成，
/// 只给系统留 2 GB，且不超过物理内存的 90%。
/// </summary>
public sealed class MemoryPlannerTests
{
    [Theory]
    [InlineData(4 * 1024, 2560)]
    [InlineData(8 * 1024, 5120)]
    [InlineData(16 * 1024, 8192)]
    [InlineData(32 * 1024, 10240)]
    public void MachineBaselineScalesWithRam(int totalMb, int expected)
    {
        Assert.Equal(expected, MemoryPlanner.RecommendForMachine(totalMb));
    }

    [Fact]
    public void BigPackGetsMoreThanSmallPack()
    {
        var small = MemoryPlanner.RecommendForPack(16 * 1024, 20);
        var big = MemoryPlanner.RecommendForPack(16 * 1024, 400);

        Assert.True(big > small, $"大整合包应拿更多内存：{big} vs {small}");
    }

    [Fact]
    public void LargePackOnSixteenGigGetsAtLeastTenGig()
    {
        // 玩家反馈：495 模组在 16 GB 机器上，7.5 GB 进主菜单就快满了
        var value = MemoryPlanner.RecommendForPack(16 * 1024, 495);

        Assert.True(value >= 10 * 1024, $"给得太少：{value} MB");
    }

    [Fact]
    public void NeverExceedsNinetyPercentOfTheMachine()
    {
        var value = MemoryPlanner.RecommendForPack(16 * 1024, 500);

        Assert.True(value <= 16 * 1024 * 0.9, $"超过物理内存 90%：{value}");
        Assert.True(value <= 16 * 1024 - 2048, $"没给系统留出 2 GB：{value}");
    }

    [Fact]
    public void SmallMachineStillGetsSomethingUsable()
    {
        var value = MemoryPlanner.RecommendForPack(4 * 1024, 300);

        Assert.True(value >= 2048, $"太小了：{value}");
        Assert.True(value <= 4 * 1024 - 2048, $"4 GB 机器给太多：{value}");
    }

    [Fact]
    public void ValuesAreRoundedToHalfGigabytes()
    {
        foreach (var mods in new[] { 10, 120, 260, 420 })
        {
            Assert.Equal(0, MemoryPlanner.RecommendForPack(24 * 1024, mods) % 512);
        }
    }

    [Theory]
    [InlineData("zh_cn")]
    [InlineData("en_us")]
    [InlineData("ZH_TW")]
    public void KnownLanguageCodesSurvive(string code)
    {
        Assert.Equal(code.ToLowerInvariant(), GameOptionsTemplate.NormaliseLang(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("中文")]
    public void BadLanguageCodesFallBackToChinese(string code)
    {
        Assert.Equal("zh_cn", GameOptionsTemplate.NormaliseLang(code));
    }

    [Fact]
    public void LanguageEndsUpInOptionsKeys()
    {
        var template = new GameOptionsTemplate { Lang = "en_us" };
        var keys = template.ToKeyValues();

        Assert.Equal("en_us", keys["lang"]);
    }
}
