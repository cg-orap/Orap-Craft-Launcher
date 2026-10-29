using Orion.Core.Java;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 「需要 Java 运行时来运行安装器」的根因是安装整合包时没挑 Java。这条规则要能按 MC 版本选对主版本。
/// </summary>
public sealed class InstallerJavaSelectionTests
{
    [Theory]
    [InlineData("1.20.1", 17)]
    [InlineData("1.19.2", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.17.1", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.21", 21)]
    [InlineData("1.21.1", 21)]
    [InlineData("26.3", 25)]
    public void RequiredJavaFollowsTheMinecraftVersion(string version, int expectedMajor)
    {
        Assert.Equal(expectedMajor, JavaVersionRules.RequiredMajorFor(version));
    }

    [Fact]
    public void UnknownVersionFallsBackTo17()
    {
        Assert.Equal(17, JavaVersionRules.RequiredMajorFor(null));
        Assert.Equal(17, JavaVersionRules.RequiredMajorFor(""));
    }

    [Fact]
    public void PhaseProgressIsMonotonicAndClamped()
    {
        var lines = new[]
        {
            "正在安装 1.20.1-forge-47.2.0…",
            "正在预下载安装器依赖",
            "下载模组 12/64",
            "整合包已安装：X",
        };

        var previous = -1.0;
        foreach (var line in lines)
        {
            var fraction = PackInstallProgress.FromLine(line);
            if (fraction < 0) continue;                       // 未识别的行不推进
            Assert.InRange(fraction, 0, 1);
            Assert.True(fraction >= previous, $"进度回退了：{line} → {fraction}");
            previous = fraction;
        }
    }
}