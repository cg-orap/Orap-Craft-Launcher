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

    [Fact]
    public void SelectorPrefersTheMatchingMajorThenNewer()
    {
        var java17 = new JavaRuntime
        {
            ExecutablePath = @"C:\jdk-17\bin\java.exe",
            Home = @"C:\jdk-17",
            MajorVersion = 17,
            Version = "17.0.11",
            Is64Bit = true,
        };

        var java21 = new JavaRuntime
        {
            ExecutablePath = @"C:\jdk-21\bin\java.exe",
            Home = @"C:\jdk-21",
            MajorVersion = 21,
            Version = "21.0.4",
            Is64Bit = true,
        };

        // 1.20.1 需要 17：必须选中 17，而不是 21
        Assert.Same(java17, JavaDetector.SelectFor(17, [java21, java17]));

        // 只有 21 时退而求其次（比没有强，但优先 17）
        Assert.Same(java21, JavaDetector.SelectFor(17, [java21]));

        // 一个都没有 → null，调用方必须报错，不能把 null 传给安装器
        // （这正是「加载器安装失败: 需要 Java 运行时来运行安装器」的根因）
        Assert.Null(JavaDetector.SelectFor(17, []));
    }
}
