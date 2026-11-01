using Orion.Core.Game;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 自动分配内存：给多少要"够用但不吃满机器"。
/// 规则与主流启动器一致：按物理内存 + 模组数量，留 2 GB 给系统，不超过物理内存一半。
/// </summary>
public sealed class MemoryPlannerTests
{
    [Theory]
    [InlineData(4 * 1024, 2048)]
    [InlineData(8 * 1024, 4096)]
    [InlineData(16 * 1024, 6144)]
    [InlineData(32 * 1024, 8192)]
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
    public void NeverExceedsHalfTheMachine()
    {
        // 16 GB 机器上即使 500 个模组也不能超过 8 GB，并且要给系统留 2 GB
        var value = MemoryPlanner.RecommendForPack(16 * 1024, 500);

        Assert.True(value <= 16 * 1024 / 2, $"超过物理内存一半：{value}");
        Assert.True(value <= 16 * 1024 - 2048, $"没给系统留出余量：{value}");
    }

    [Fact]
    public void SmallMachineStillGetsSomethingUsable()
    {
        var value = MemoryPlanner.RecommendForPack(4 * 1024, 300);

        Assert.True(value >= 1024, $"太小了：{value}");
        Assert.True(value <= 2048, $"4 GB 机器给太多：{value}");
    }

    [Fact]
    public void ValuesAreRoundedToHalfGigabytes()
    {
        var value = MemoryPlanner.RecommendForPack(24 * 1024, 180);

        Assert.Equal(0, value % 512);
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
