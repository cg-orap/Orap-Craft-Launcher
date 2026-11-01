using System.Security.Cryptography;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 下载后的校验：CurseForge 元数据给了 sha1 和大小，就必须核对。
/// 半截文件 / HTML 错误页如果能留在 mods 里，游戏启动时才会炸——那时用户根本看不出原因。
/// </summary>
public sealed class ResolvedFileVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orap-verify-" + Guid.NewGuid().ToString("N"));

    public ResolvedFileVerificationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public void MatchingSizeAndHashPasses()
    {
        var path = WriteFile([1, 2, 3, 4, 5]);
        var file = new ResolvedCurseForgeFile(1, 2, "a.jar", Sha1Of(path), 5, null);

        Assert.True(file.Matches(new FileInfo(path)));
    }

    [Fact]
    public void WrongSizeFails()
    {
        var path = WriteFile([1, 2, 3]);
        var file = new ResolvedCurseForgeFile(1, 2, "a.jar", null, 99, null);

        Assert.False(file.Matches(new FileInfo(path)));
    }

    [Fact]
    public void WrongHashFails()
    {
        var path = WriteFile([1, 2, 3]);
        var file = new ResolvedCurseForgeFile(1, 2, "a.jar", new string('A', 40), 3, null);

        Assert.False(file.Matches(new FileInfo(path)));
    }

    [Fact]
    public void WithoutMetadataOnlyExistenceIsChecked()
    {
        var path = WriteFile([9]);
        var file = new ResolvedCurseForgeFile(1, 2, "a.jar", null, 0, null);

        Assert.True(file.Matches(new FileInfo(path)));
        Assert.False(file.Matches(new FileInfo(Path.Combine(_root, "missing.jar"))));
    }

    private string WriteFile(byte[] content)
    {
        var path = Path.Combine(_root, "file-" + Guid.NewGuid().ToString("N") + ".jar");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
