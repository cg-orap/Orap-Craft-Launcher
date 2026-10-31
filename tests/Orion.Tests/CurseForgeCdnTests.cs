using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// ForgeCDN 直链拼法。没有文件名就完全拼不出链接——这是"同一个整合包 PCL 能装、我们装不上"的关键。
/// </summary>
public sealed class CurseForgeCdnTests
{
    [Fact]
    public void BuildsBothCdnHostsWithTheBucketLayout()
    {
        var urls = CurseForgeCdn.Urls(4176000, "patchouli-1.19.2-77.jar");

        Assert.Equal(2, urls.Count);
        Assert.All(urls, u => Assert.Contains("/files/4176/0/patchouli-1.19.2-77.jar", u));
        Assert.Contains(urls, u => u.Contains("mediafilez.forgecdn.net"));
        Assert.Contains(urls, u => u.Contains("edge.forgecdn.net"));
    }

    [Fact]
    public void ReturnsNothingWhenTheFileNameIsUnknown()
    {
        Assert.Empty(CurseForgeCdn.Urls(123, null));
        Assert.Empty(CurseForgeCdn.Urls(123, "   "));
    }

    [Fact]
    public void StripsQuotesAndDirectoriesFromTheFileName()
    {
        Assert.Equal("mod.jar", CurseForgeCdn.NormalizeFileName("\"mods/mod.jar\""));
        Assert.Equal("mod.jar", CurseForgeCdn.NormalizeFileName("mod.jar"));
        Assert.Null(CurseForgeCdn.NormalizeFileName(""));
    }

    [Fact]
    public void FileIdsUnderOneThousandUseBucketZero()
    {
        var urls = CurseForgeCdn.Urls(999, "small.jar");

        Assert.All(urls, u => Assert.Contains("/files/0/999/small.jar", u));
    }

    [Fact]
    public void EncodesSpacesInTheFileName()
    {
        var urls = CurseForgeCdn.Urls(1000, "my mod.jar");

        Assert.All(urls, u => Assert.Contains("my%20mod.jar", u));
    }
}
