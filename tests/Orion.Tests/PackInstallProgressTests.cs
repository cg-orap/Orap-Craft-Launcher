using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>安装器输出 → 进度条百分比：必须单调、可识别百分比与 x/y 两种写法。</summary>
public sealed class PackInstallProgressTests
{
    [Theory]
    [InlineData("下载模组 12/64", 0.62, 0.95)]
    [InlineData("正在运行安装器 50%", 0.58, 0.62)]
    [InlineData("整合包已安装：X", 1.0, 1.0)]
    public void KnownPhasesMapIntoTheirBand(string line, double min, double max)
    {
        var fraction = PackInstallProgress.FromLine(line);
        Assert.InRange(fraction, min, max);
    }

    [Theory]
    [InlineData("随便一句日志")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownLinesReturnMinusOne(string? line)
    {
        Assert.Equal(-1, PackInstallProgress.FromLine(line));
    }

    [Fact]
    public void ProgressNeverGoesBackwards()
    {
        string[] lines =
        [
            "读取 CurseForge 清单：X",
            "正在安装 1.19.2-forge-43.4.0…",
            "正在预下载安装器依赖",
            "正在运行安装器 20%",
            "下载模组 10/50",
            "下载模组 50/50",
            "复制 overrides（3 个文件）",
            "整合包已安装：X",
        ];

        var previous = -1.0;
        foreach (var line in lines)
        {
            var fraction = PackInstallProgress.FromLine(line);
            if (fraction < 0) continue;
            Assert.True(fraction >= previous, $"进度回退：{line} → {fraction} < {previous}");
            previous = fraction;
        }

        Assert.Equal(1.0, previous);
    }
}