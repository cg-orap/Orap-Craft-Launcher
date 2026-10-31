using Orion.Core;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 便携模式（exe 旁边放 portable.txt）必须真的生效。
/// 这里锁住"数据根目录判定"的两个关键行为：portable.txt 存在 → exe 旁的 data；
/// 不存在 → %APPDATA% 下的数据目录（且不会因为改名丢掉旧目录）。
/// </summary>
public sealed class PortableModeTests : IDisposable
{
    private readonly string _tempExeDir;

    public PortableModeTests()
    {
        _tempExeDir = Path.Combine(Path.GetTempPath(), "orap-portable-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempExeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempExeDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public void PortableFileBesideTheExecutableSelectsTheLocalDataFolder()
    {
        File.WriteAllText(Path.Combine(_tempExeDir, "portable.txt"), "");

        var root = LauncherContext.ResolveRootForTests(_tempExeDir);

        Assert.Equal(Path.Combine(_tempExeDir, "data"), root);
    }

    [Fact]
    public void WithoutPortableFileItFallsBackToAppData()
    {
        var root = LauncherContext.ResolveRootForTests(_tempExeDir);

        // 不应落在 exe 目录里
        Assert.DoesNotContain(_tempExeDir, root, StringComparison.OrdinalIgnoreCase);

        // 应当是一个以 OrapCraftLauncher 或（兼容旧名）OrionLauncher 结尾的目录
        var name = Path.GetFileName(root);
        Assert.True(name is "OrapCraftLauncher" or "OrionLauncher", $"unexpected root: {root}");
    }
}
