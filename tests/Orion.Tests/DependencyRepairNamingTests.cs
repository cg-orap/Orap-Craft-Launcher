using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 补前置时的"名字换算"：
/// · modId 和 CurseForge 的 slug 经常不一致（geckolib3 → geckolib），否则会一个都补不上；
/// · 但 jar 里声明的 modId 只允许"结尾数字"这种极小差异，避免装进无关模组。
/// </summary>
public sealed class DependencyRepairNamingTests
{
    [Fact]
    public void SlugCandidatesCoverTheRealWorldMismatches()
    {
        Assert.Contains("geckolib", ModDependencyRepair.SlugCandidates("geckolib3"));
        Assert.Contains("cucumber", ModDependencyRepair.SlugCandidates("cucumber_library"));
        Assert.Contains("patchouli", ModDependencyRepair.SlugCandidates("patchouli"));
    }

    [Fact]
    public void SlugCandidatesAreDeduplicatedAndKeepTheOriginalFirst()
    {
        var candidates = ModDependencyRepair.SlugCandidates("balm");

        Assert.Equal("balm", candidates[0]);
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SameModAcceptsTheTrailingDigitDifference()
    {
        Assert.True(ModDependencyRepair.SameMod("geckolib", "geckolib3"));
        Assert.True(ModDependencyRepair.SameMod("geckolib3", "geckolib"));
        Assert.True(ModDependencyRepair.SameMod("Patchouli", "patchouli"));
    }

    [Fact]
    public void SameModRejectsUnrelatedMods()
    {
        Assert.False(ModDependencyRepair.SameMod("cucumber", "patchouli"));
        Assert.False(ModDependencyRepair.SameMod("iceberg", "libx"));
    }
}
