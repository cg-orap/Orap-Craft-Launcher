using System.IO.Compression;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// "all" 发行包（外层没有 mods.toml，真模组在 META-INF/jarjar/*.jar 里）也要算已安装。
/// 真实案例：kotlinforforge-5.12.0-all.jar —— 不读内嵌 jar 就会永远报"缺 kotlinforforge"。
/// </summary>
public sealed class NestedOnlyModTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orap-jarjar-" + Guid.NewGuid().ToString("N"));

    public NestedOnlyModTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public void JarWithOnlyNestedModsCountsAsInstalled()
    {
        var mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(mods);

        // 外层：只有 jarjar 内嵌 jar，没有 mods.toml
        var inner = Path.Combine(_root, "inner.jar");
        WriteJar(inner, ("META-INF/mods.toml",
            "[[mods]]\nmodId=\"kotlinforforge\"\nversion=\"5.12.0\"\n"));

        var outer = Path.Combine(mods, "kotlinforforge-5.12.0-all.jar");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("META-INF/jarjar/thedarkcolour.kffmod-5.12.0.jar");
            using var stream = entry.Open();
            using var source = File.OpenRead(inner);
            source.CopyTo(stream);
        }

        var scan = ModDependencyScanner.Scan(mods);

        Assert.Equal(1, scan.ModCount);
        Assert.Empty(scan.Missing);
    }

    private static void WriteJar(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
