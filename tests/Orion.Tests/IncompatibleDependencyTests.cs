using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 补前置不能把整合包补坏：如果候选文件声明了与已装模组不兼容，就必须跳过。
/// 真实案例：自动补进来的 colorwheel 与 irisfly 冲突，游戏直接崩。
/// </summary>
public sealed class IncompatibleDependencyTests
{
    private const string Toml = """
        modLoader = "javafml"

        [[mods]]
        modId = "colorwheel"
        version = "2.4.0"

        [[dependencies.colorwheel]]
        modId = "irisfly"
        mandatory = false
        type = "incompatible"
        versionRange = "*"

        [[dependencies.colorwheel]]
        modId = "neoforge"
        mandatory = true
        type = "required"
        versionRange = "[21.1.0,)"
        """;

    [Fact]
    public void ParsesIncompatibleDeclarations()
    {
        var ids = ModDependencyScanner.ParseIncompatible(Toml);

        Assert.Single(ids);
        Assert.Equal("irisfly", ids[0]);
    }

    [Fact]
    public void IgnoresRequiredDependencies()
    {
        var ids = ModDependencyScanner.ParseIncompatible(Toml);

        Assert.DoesNotContain("neoforge", ids);
        Assert.DoesNotContain("minecraft", ids);
    }

    [Fact]
    public void EmptyTomlHasNoIncompatibilities()
    {
        Assert.Empty(ModDependencyScanner.ParseIncompatible(""));
        Assert.Empty(ModDependencyScanner.ParseIncompatible("[[mods]]\nmodId = \"x\"\n"));
    }
}
