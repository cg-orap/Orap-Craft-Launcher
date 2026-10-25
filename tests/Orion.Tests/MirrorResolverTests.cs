using Orion.Core.Net;

namespace Orion.Tests;

/// <summary>
/// Guards the mirror ordering. Ordering official first looked harmless but a throttled (not failing)
/// official host kept answering at a few KB/s, so the mirror fallback never fired and downloads
/// crawled at single-digit KB/s.
/// </summary>
public sealed class MirrorResolverTests
{
    private static MirrorResolver Resolver(DownloadSource source) =>
        new(new NetworkOptions { Source = source });

    [Theory]
    [InlineData(DownloadSource.Official)]
    [InlineData(DownloadSource.BmclApi)]
    public void AssetAndLibraryUrlsPreferTheMirror(DownloadSource source)
    {
        var resolver = Resolver(source);

        var candidates = resolver.Resolve(new Uri("https://resources.download.minecraft.net/ab/abcdef0123456789"));
        Assert.Equal(2, candidates.Count);
        Assert.Contains("bmclapi2.bangbang93.com", candidates[0].Host);
        Assert.Equal("resources.download.minecraft.net", candidates[1].Host);

        var libraries = resolver.Resolve(new Uri("https://libraries.minecraft.net/org/ow2/asm/asm/9.6/asm-9.6.jar"));
        Assert.Equal(2, libraries.Count);
        Assert.Contains("bmclapi2.bangbang93.com", libraries[0].Host);
        Assert.Equal("libraries.minecraft.net", libraries[1].Host);
    }

    [Fact]
    public void OfficialUrlStaysAvailableAsFallbackForUnmappedHosts()
    {
        var resolver = Resolver(DownloadSource.BmclApi);
        var unmapped = new Uri("https://example.invalid/some/file.jar");

        var candidates = resolver.Resolve(unmapped);

        Assert.Single(candidates);
        Assert.Equal(unmapped, candidates[0]);
    }

    [Fact]
    public void MirrorKeepsTheOriginalPathShape()
    {
        var resolver = Resolver(DownloadSource.BmclApi);
        var asset = resolver.Resolve(new Uri("https://resources.download.minecraft.net/ab/abcdef0123456789"));

        Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef0123456789", asset[0].ToString());
    }
}
