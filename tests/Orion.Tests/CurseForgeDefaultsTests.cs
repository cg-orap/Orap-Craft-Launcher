using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 内置 CurseForge Key 的解析规则：用户填的优先，没填回退到内置，都没有则为 null。
/// 这三条决定"分享给别人后能不能直接用"，所以必须有测试锁住。
/// </summary>
public sealed class CurseForgeDefaultsTests
{
    [Fact]
    public void UserKeyWinsOverBuiltIn()
    {
        Assert.Equal("user-key", CurseForgeDefaults.Resolve("user-key", "built-in-key"));
    }

    [Fact]
    public void FallsBackToBuiltInWhenUserKeyIsBlank()
    {
        Assert.Equal("built-in-key", CurseForgeDefaults.Resolve("", "built-in-key"));
        Assert.Equal("built-in-key", CurseForgeDefaults.Resolve(null, "built-in-key"));
        Assert.Equal("built-in-key", CurseForgeDefaults.Resolve("   ", "built-in-key"));
    }

    [Fact]
    public void ReturnsNullWhenNeitherIsProvided()
    {
        Assert.Null(CurseForgeDefaults.Resolve(null, null));
        Assert.Null(CurseForgeDefaults.Resolve("  ", ""));
    }

    [Fact]
    public void TrimsBothSides()
    {
        Assert.Equal("user-key", CurseForgeDefaults.Resolve("  user-key  ", "built-in-key"));
        Assert.Equal("built-in-key", CurseForgeDefaults.Resolve(null, "  built-in-key  "));
    }
}
