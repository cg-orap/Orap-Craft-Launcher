using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 隔离"同 modId 但版本不对"的旧 jar：补对版本之后，旧的那份会让 Forge 报重复 mod。
/// </summary>
public sealed class StaleModFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orap-stale-" + Guid.NewGuid().ToString("N"));

    public StaleModFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public void SelectsOnlySameModIdFilesOtherThanTheNewOne()
    {
        var mods = new[]
        {
            Path.Combine(_root, "Iceberg-26.3-forge-1.4.3.1.jar"),
            Path.Combine(_root, "Iceberg-1.19.2-forge-1.2.0.jar"),
            Path.Combine(_root, "some-other-mod.jar"),
        };

        var stale = StaleModFiles.SelectStale(mods, "iceberg", mods[1]);

        var only = Assert.Single(stale);
        Assert.Contains("26.3", only);
    }

    [Fact]
    public void QuarantineMovesTheFileUnderTheInstanceDirectory()
    {
        var file = Path.Combine(_root, "LibX-1.20.1-5.0.12.jar");
        File.WriteAllText(file, "jar");

        var moved = StaleModFiles.Quarantine(_root, file, new DateTimeOffset(2026, 10, 31, 19, 0, 0, TimeSpan.Zero));

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(moved));
        Assert.Contains(".orap-quarantine", moved);
        Assert.Contains("20261031-190000", moved);
    }

    [Fact]
    public void SelectStaleIsCaseInsensitive()
    {
        var mods = new[] { Path.Combine(_root, "LIBX-old.jar"), Path.Combine(_root, "libx-new.jar") };

        var stale = StaleModFiles.SelectStale(mods, "libx", mods[1]);

        Assert.Single(stale);
        Assert.Contains("LIBX-old", stale[0]);
    }
}
