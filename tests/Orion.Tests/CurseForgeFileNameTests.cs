using System.Reflection;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 下载文件的命名：既不能丢扩展名，也不能给 .jar 再补 .zip。
/// 之前会存成 "Patchouli-1.19.2-77.jar.zip"，装进 mods 之后游戏根本不加载——
/// 用户看到的就是"装了没生效"。
/// </summary>
public sealed class CurseForgeFileNameTests
{
    [Theory]
    [InlineData("Patchouli-1.19.2-77.jar", "Patchouli-1.19.2-77.jar")]
    [InlineData("Better-MC-BMC5-v54.zip", "Better-MC-BMC5-v54.zip")]
    [InlineData("pack", "pack.zip")]
    [InlineData("weird<name>.jar", "weird_name_.jar")]
    public void KeepsTheDownloadedExtension(string input, string expected)
    {
        Assert.Equal(expected, Sanitize(input));
    }

    [Fact]
    public void FallsBackWhenTheNameIsMissing()
    {
        Assert.Equal("fallback.zip", Sanitize(""));
    }

    private static string Sanitize(string fileName)
    {
        var method = typeof(CurseForgeClient).GetMethod("Sanitize",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var args = new object[] { fileName, "fallback.zip" };
        return (string)method.Invoke(null, args)!;
    }
}
