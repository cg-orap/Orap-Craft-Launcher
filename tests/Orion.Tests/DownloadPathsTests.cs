using Orion.Core.Game;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 下载目录解析：空白用默认、绝对路径直接用、相对路径相对数据根目录、非法路径退回默认。
/// 这几条决定"设置里改下载目录"会不会把下载弄坏。
/// </summary>
public sealed class DownloadPathsTests : IDisposable
{
    private readonly string _root;
    private readonly GamePaths _paths;

    public DownloadPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orap-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new GamePaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响到断言 */ }
    }

    [Fact]
    public void BlankUsesTheDefaultDownloadsFolder()
    {
        Assert.Equal(Path.Combine(_root, "downloads"), DownloadPaths.Resolve(_paths, null));
        Assert.Equal(Path.Combine(_root, "downloads"), DownloadPaths.Resolve(_paths, "   "));
        Assert.True(DownloadPaths.IsDefault(_paths, ""));
    }

    [Fact]
    public void AbsolutePathIsUsedAsIs()
    {
        var custom = Path.Combine(Path.GetTempPath(), "orap-custom-downloads");

        Assert.Equal(Path.GetFullPath(custom), DownloadPaths.Resolve(_paths, custom));
        Assert.False(DownloadPaths.IsDefault(_paths, custom));
    }

    [Fact]
    public void RelativePathResolvesAgainstTheDataRoot()
    {
        Assert.Equal(Path.Combine(_root, "我的下载"), DownloadPaths.Resolve(_paths, "我的下载"));
    }

    [Fact]
    public void TrimsSurroundingSpaces()
    {
        var custom = Path.Combine(Path.GetTempPath(), "orap-trim-test");
        Assert.Equal(Path.GetFullPath(custom), DownloadPaths.Resolve(_paths, $"  {custom}  "));
    }
}
