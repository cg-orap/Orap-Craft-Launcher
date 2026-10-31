using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 版本范围的两条现实规则：
/// · 裸露版本号（4.7.5.1 / 1 / 2.0.0-beta.1）不算"范围"，不能拿来判定"缺前置"；
/// · 真的范围（[5.3.0,)、>=1.1.20 <1.2.0）仍然要按 Maven 语义比较。
/// 这条搞错就会出现"模组明明装了，启动前却弹窗说 12 个前置没补上"。
/// </summary>
public sealed class VersionRangeUnconstrainedTests
{
    [Theory]
    [InlineData("4.7.5.1")]
    [InlineData("1")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("")]
    [InlineData(null)]
    public void BareVersionsAreUnconstrained(string? range)
    {
        Assert.True(VersionRange.IsUnconstrained(range));
    }

    [Theory]
    [InlineData("[5.3.0,)")]
    [InlineData("[1.21.1-1.8.0,)")]
    [InlineData(">=")]
    [InlineData(">=1.1.20 <1.2.0")]
    public void RealRangesAreConstrained(string range)
    {
        Assert.False(VersionRange.IsUnconstrained(range));
    }
}