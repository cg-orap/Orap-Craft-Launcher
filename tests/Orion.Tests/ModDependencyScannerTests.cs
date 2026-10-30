using System.IO.Compression;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>缺前置检测：从 jar 的 mods.toml 里读依赖，判断有没有装、版本够不够。</summary>
public sealed class ModDependencyScannerTests
{
    private const string TracksToml = """
    modLoader = "javafml"
    loaderVersion = "[1,)"

    [[mods]]
    modId = "tracks"
    version = "1.0.1"

    [[dependencies.tracks]]
        modId = "neoforge"
        mandatory = true
        versionRange = "[21.1.0,)"
        ordering = "NONE"
        side = "BOTH"

    [[dependencies.tracks]]
        modId = "simulated"
        mandatory = true
        versionRange = "[1.0.1,)"
        ordering = "AFTER"
        side = "BOTH"

    [[dependencies.tracks]]
        modId = "jei"
        mandatory = false
        versionRange = "[15,)"
    """;

    [Fact]
    public void TomlParsingPicksModIdVersionAndRequiredDependencies()
    {
        var info = ModDependencyScanner.ParseToml(TracksToml);

        Assert.NotNull(info);
        Assert.Equal("tracks", info!.PrimaryModId);
        Assert.Equal(("tracks", "1.0.1"), info.Mods[0]);

        // neoforge 是内置模块，扫描阶段就过滤掉；jei 是可选依赖
        Assert.Contains(info.Dependencies, d => d.ModId == "simulated" && d.Mandatory);
        Assert.Contains(info.Dependencies, d => d.ModId == "jei" && !d.Mandatory);
    }

    [Fact]
    public void MissingDependencyIsReported()
    {
        var mods = CreateMods(
            ("tracks.jar", TracksToml));

        try
        {
            var scan = ModDependencyScanner.Scan(mods);

            Assert.Equal(1, scan.ModCount);
            var missing = Assert.Single(scan.Missing);
            Assert.Equal("simulated", missing.ModId);
            Assert.Equal("tracks", missing.RequiredBy);
        }
        finally
        {
            TryDelete(mods);
        }
    }

    [Fact]
    public void SatisfiedDependencyIsNotReported()
    {
        var mods = CreateMods(
            ("tracks.jar", TracksToml),
            ("simulated.jar", ModToml("simulated", "1.2.1")));

        try
        {
            var scan = ModDependencyScanner.Scan(mods);
            Assert.Empty(scan.Missing);
        }
        finally
        {
            TryDelete(mods);
        }
    }

    [Fact]
    public void InstalledButTooOldIsStillReported()
    {
        var mods = CreateMods(
            ("tracks.jar", TracksToml),
            ("simulated.jar", ModToml("simulated", "0.9.0")));

        try
        {
            var scan = ModDependencyScanner.Scan(mods);
            var missing = Assert.Single(scan.Missing);
            Assert.Equal("simulated", missing.ModId);
        }
        finally
        {
            TryDelete(mods);
        }
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        var scan = ModDependencyScanner.Scan(@"Z:\nowhere-mods");
        Assert.Empty(scan.Missing);
        Assert.Equal(0, scan.ModCount);
    }

    [Theory]
    [InlineData("1.2.1", "[1.0.1,)", true)]
    [InlineData("1.0.0", "[1.0.1,)", false)]
    [InlineData("1.2.1", "[1.1.3,2.0.0)", true)]
    [InlineData("2.0.0", "[1.1.3,2.0.0)", false)]
    [InlineData("1.3.2", "[1.3.0,)", true)]
    [InlineData("21.1.0", "[21.1.0,)", true)]
    [InlineData("26.3", "[25,)", true)]
    [InlineData("1.0.1", "1.0.1", true)]
    [InlineData("1.0.2", "1.0.1", false)]
    public void VersionRangeMatchesForgeSemantics(string version, string range, bool expected)
    {
        Assert.Equal(expected, VersionRange.Satisfies(version, range));
    }

    [Fact]
    public void RealWorldTomlWithInlineCommentsParses()
    {
        // 真实文件长这样：连表头都带注释，依赖用 type 而不是 mandatory
        const string toml = """
        modLoader = "lowcodefml" #mandatory
        loaderVersion = "[4,)" #mandatory
        license = "Read attached LICENSE.md"
        [[mods]] #mandatory
        modId = "aeronautics_bundled" #mandatory
        version = "1.3.2" #mandatory
        displayName = "Create Aeronautics" #mandatory
        description = \u0027\u0027\u0027bundled JAR\u0027\u0027\u0027 #mandatory
        [[dependencies."aeronautics_bundled"]] #optional
        modId = "neoforge" #mandatory
        type = "required" #mandatory
        versionRange = "[21.1.228,)" #mandatory
        [[dependencies."aeronautics_bundled"]]
        modId = "create"
        type = "required"
        versionRange = "[6,)"
        [[dependencies."aeronautics_bundled"]]
        modId = "jei"
        type = "optional"
        versionRange = "[15,)"
        """;

        var info = ModDependencyScanner.ParseToml(toml);

        Assert.NotNull(info);
        Assert.Equal("aeronautics_bundled", info!.PrimaryModId);
        Assert.Equal("1.3.2", info.Mods[0].Version);
        Assert.Contains(info.Dependencies, d => d.ModId == "neoforge");
        Assert.Contains(info.Dependencies, d => d.ModId == "create" && d.Mandatory);
        Assert.Contains(info.Dependencies, d => d.ModId == "jei" && !d.Mandatory);
    }

    private static string ModToml(string modId, string version) => $"""
    [[mods]]
    modId = "{modId}"
    version = "{version}"
    """;

    private static string CreateMods(params (string Name, string Toml)[] jars)
    {
        var directory = Path.Combine(Path.GetTempPath(), "orap-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        foreach (var (name, toml) in jars)
        {
            using var zip = ZipFile.Open(Path.Combine(directory, name), ZipArchiveMode.Create);
            var entry = zip.CreateEntry("META-INF/neoforge.mods.toml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(toml);
        }

        return directory;
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
}
