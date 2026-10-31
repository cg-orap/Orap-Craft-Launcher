using System.IO.Compression;
using Orion.Core.Instances;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 冲突检测的判定逻辑。每个用例都造真实的 jar（zip + mods.toml），
/// 因为误报的代价很高——真实实例 177 个 jar 必须零 Error。
/// </summary>
public sealed class ModConflictScannerTests
{
    private const string Toml = """
        modLoader = "javafml"
        [[mods]] #mandatory
        modId = "{0}" #mandatory
        version = "{1}" #mandatory
        """;

    private const string TomlWithDeps = """
        [[mods]]
        modId = "{0}"
        version = "{1}"

        [[dependencies.{0}]]
        modId = "{2}"
        type = "required"
        versionRange = "{3}"
        """;

    [Fact]
    public void DuplicateModIdAcrossTwoJarsIsAnError()
    {
        using var dir = new ModsDir();
        dir.Jar("create-0.5.1.jar", Toml, "create", "0.5.1");
        dir.Jar("create-0.6.0.jar", Toml, "create", "0.6.0");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c1 = Assert.Single(conflicts.Where(c => c.Code == "C1"));
        Assert.Equal(ConflictSeverity.Error, c1.Severity);
        Assert.Contains("create", c1.Title);
        Assert.Equal(2, c1.Files.Count);
    }

    [Fact]
    public void SameModIdInsideOneBundledJarIsNotAConflict()
    {
        using var dir = new ModsDir();
        // bundled 发行版：外层 aeronautics_bundled，内嵌自带同名模块
        dir.BundledJar("create-aeronautics-bundled-1.21.1-1.3.2.jar",
            ("aeronautics_bundled", "1.3.2"),
            ("simulated", "1.3.2"), ("offroad", "1.3.2"), ("aeronautics", "1.3.2"));

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        Assert.DoesNotContain(conflicts, c => c.Code == "C1");
    }

    [Fact]
    public void TwoVersionsOfTheSameJarNameIsAnError()
    {
        using var dir = new ModsDir();
        dir.Jar("jei-1.0.0.jar", Toml, "jei_old", "1.0.0");
        dir.Jar("jei-2.0.0.jar", Toml, "jei_new", "2.0.0");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c2 = Assert.Single(conflicts.Where(c => c.Code == "C2"));
        Assert.Equal(ConflictSeverity.Error, c2.Severity);
        Assert.Equal(2, c2.Files.Count);
    }

    [Fact]
    public void FabricOnlyJarInNeoForgeInstanceIsAnError()
    {
        using var dir = new ModsDir();
        dir.FabricJar("sodium-0.5.jar", "sodium", "0.5");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c3 = Assert.Single(conflicts.Where(c => c.Code == "C3"));
        Assert.Equal(ConflictSeverity.Error, c3.Severity);
        Assert.Contains("sodium-0.5.jar", c3.Files);
    }

    [Fact]
    public void ForgeJarInFabricInstanceIsAnError()
    {
        using var dir = new ModsDir();
        dir.Jar("jei-19.0.0.jar", Toml, "jei", "19.0.0");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.Fabric, "1.21.1");

        Assert.Contains(conflicts, c => c.Code == "C3" && c.Files.Contains("jei-19.0.0.jar"));
    }

    [Fact]
    public void InstalledVersionOutsideRequiredRangeIsAnError()
    {
        using var dir = new ModsDir();
        dir.Jar("lib-1.0.0.jar", Toml, "testlib", "1.0.0");
        dir.JarWithDeps("addon-1.0.jar", "addon", "1.0", "testlib", "[2.0,)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c4 = Assert.Single(conflicts.Where(c => c.Code == "C4"));
        Assert.Equal(ConflictSeverity.Error, c4.Severity);
        Assert.Contains("testlib", c4.Title);
        Assert.Contains("addon-1.0.jar", c4.Files);
    }

    [Fact]
    public void DeclarationOfIncompatibilityIsAWarning()
    {
        using var dir = new ModsDir();
        dir.Jar("badmod-1.0.jar", Toml, "badmod", "1.0");
        dir.JarWithIncompatible("guarding-1.0.jar", "guarding", "1.0", "badmod", "[0,)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c5 = Assert.Single(conflicts.Where(c => c.Code == "C5"));
        Assert.Equal(ConflictSeverity.Warning, c5.Severity);
        Assert.Contains("badmod", c5.Title);
    }

    [Fact]
    public void IncompatibleDeclarationForAbsentModIsNotReported()
    {
        using var dir = new ModsDir();
        dir.JarWithIncompatible("guarding-1.0.jar", "guarding", "1.0", "someone_else", "[0,)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        Assert.DoesNotContain(conflicts, c => c.Code == "C5");
    }

    [Fact]
    public void SharedMixinConfigIsOnlyAWarning()
    {
        using var dir = new ModsDir();
        dir.Jar("a-1.0.jar", Toml, "mod_a", "1.0", extraEntries: ["shared.mixins.json"]);
        dir.Jar("b-1.0.jar", Toml, "mod_b", "1.0", extraEntries: ["shared.mixins.json"]);

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c6 = Assert.Single(conflicts.Where(c => c.Code == "C6"));
        Assert.Equal(ConflictSeverity.Warning, c6.Severity);
        Assert.False(c6.IsBlocking);
    }

    [Fact]
    public void CoreLibraryRangesWithoutIntersectionAreAWarning()
    {
        using var dir = new ModsDir();
        dir.Jar("lib-1.5.0.jar", Toml, "kotlinforforge", "1.5.0");
        dir.JarWithDeps("old-1.0.jar", "old_mod", "1.0", "kotlinforforge", "[1.0,2.0)");
        dir.JarWithDeps("new-1.0.jar", "new_mod", "1.0", "kotlinforforge", "[2.0,3.0)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        var c7 = Assert.Single(conflicts.Where(c => c.Code == "C7"));
        Assert.Equal(ConflictSeverity.Warning, c7.Severity);
        Assert.Contains("kotlinforforge", c7.Title);
    }

    [Fact]
    public void OverlappingRangesDoNotWarn()
    {
        using var dir = new ModsDir();
        dir.Jar("lib-2.5.0.jar", Toml, "kotlinforforge", "2.5.0");
        dir.JarWithDeps("a-1.0.jar", "mod_a", "1.0", "kotlinforforge", "[1.0,3.0)");
        dir.JarWithDeps("b-1.0.jar", "mod_b", "1.0", "kotlinforforge", "[2.0,4.0)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        Assert.DoesNotContain(conflicts, c => c.Code == "C7");
    }

    [Fact]
    public void HealthyDirectoryReportsNothing()
    {
        using var dir = new ModsDir();
        dir.Jar("lib-2.5.0.jar", Toml, "testlib", "2.5.0");
        dir.JarWithDeps("addon-1.0.jar", "addon", "1.0", "testlib", "[2.0,)");
        dir.JarWithDeps("other-1.0.jar", "other", "1.0", "testlib", "[2.0,3.0)");

        var conflicts = ModConflictScanner.Scan(dir.Path, LoaderKind.NeoForge, "1.21.1");

        Assert.Empty(conflicts);
    }

    [Fact]
    public void MissingDirectoryReturnsNothing()
    {
        Assert.Empty(ModConflictScanner.Scan(@"Z:\no-such-mods-dir", LoaderKind.NeoForge, "1.21.1"));
    }

    [Theory]
    [InlineData("jei-19.0.0.jar", "jei")]
    [InlineData("create-aeronautics-bundled-1.21.1-1.3.2.jar", "create-aeronautics-bundled")]
    [InlineData("mod.jar", "mod")]
    [InlineData("NoVersionYet.jar", "NoVersionYet")]
    public void BaseNameStripsTheVersion(string fileName, string expected) =>
        Assert.Equal(expected, ModConflictScanner.BaseName(fileName));

    [Theory]
    [InlineData("[1.0,2.0)", "[2.0,3.0)", false)]
    [InlineData("[1.0,)", "[2.0,)", true)]
    [InlineData("[1.0,1.5]", "[1.4,2.0)", true)]
    [InlineData("[1.0,1.5]", "[1.5,2.0]", true)]
    [InlineData("[1.0,1.4)", "[1.5,2.0]", false)]
    public void VersionRangeOverlapIsComputable(string left, string right, bool expected) =>
        Assert.Equal(expected, VersionRange.Overlaps(left, right));

    private sealed class ModsDir : IDisposable
    {
        public ModsDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-conflict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Jar(string fileName, string template, string modId, string version, string[]? extraEntries = null) =>
            Write(fileName, string.Format(template, modId, version), extraEntries);

        public void JarWithDeps(string fileName, string modId, string version, string depId, string range) =>
            Write(fileName, string.Format(TomlWithDeps, modId, version, depId, range), null);

        public void JarWithIncompatible(string fileName, string modId, string version, string depId, string range) =>
            Write(fileName, $"""
                [[mods]]
                modId = "{modId}"
                version = "{version}"

                [[dependencies.{modId}]]
                modId = "{depId}"
                type = "incompatible"
                versionRange = "{range}"
                """, null);

        public void FabricJar(string fileName, string modId, string version)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write($$"""{"schemaVersion":1,"id":"{{modId}}","version":"{{version}}"}""");
        }

        /// <summary>外层一个 modId，JarJar 里再塞几个（bundled 发行版的样子）。</summary>
        public void BundledJar(string fileName, params (string Id, string Version)[] modules)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            var outer = zip.CreateEntry("META-INF/neoforge.mods.toml");
            using (var writer = new StreamWriter(outer.Open()))
            {
                writer.Write(string.Format(Toml, modules[0].Id, modules[0].Version));
            }

            foreach (var (id, version) in modules.Skip(1))
            {
                using var buffer = new MemoryStream();
                using (var nested = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var toml = nested.CreateEntry("META-INF/neoforge.mods.toml");
                    using var writer = new StreamWriter(toml.Open());
                    writer.Write(string.Format(Toml, id, version));
                }

                var entry = zip.CreateEntry($"META-INF/jarjar/{id}-{version}.jar");
                using var stream = entry.Open();
                buffer.Position = 0;
                buffer.CopyTo(stream);
            }
        }

        private void Write(string fileName, string toml, string[]? extraEntries)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            var entry = zip.CreateEntry("META-INF/neoforge.mods.toml");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(toml);
            }

            foreach (var extra in extraEntries ?? [])
            {
                var extraEntry = zip.CreateEntry(extra);
                using var writer = new StreamWriter(extraEntry.Open());
                writer.Write("{}");
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
