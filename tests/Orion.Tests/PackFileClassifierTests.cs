using System.IO.Compression;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 整合包里的文件分类：资源包/光影被扔进 mods 会让 NeoForge 直接判定 mod loading 失败
/// （"File mods\xxx.jar is not a valid mod file"），所以按内容归类是硬要求。
/// </summary>
public sealed class PackFileClassifierTests
{
    [Fact]
    public void NeoForgeModIsAMod()
    {
        using var zip = new TempZip(new()
        {
            ["META-INF/neoforge.mods.toml"] = "modLoader=\"javafml\"",
            ["com/example/Mod.class"] = "x",
        });

        Assert.Equal(PackFileKind.Mod, PackFileClassifier.Classify(zip.Path));
        Assert.Equal("mods", PackFileClassifier.FolderFor(PackFileKind.Mod));
    }

    [Fact]
    public void JarJarOnlyJarStillCountsAsAMod()
    {
        // Kotlin for Forge 这类"外层只有 jarjar"的 jar 仍然是模组，不能挪走
        using var zip = new TempZip(new()
        {
            ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0",
            ["META-INF/jarjar/metadata.json"] = "{\"jars\":[]}",
        });

        Assert.NotEqual(PackFileKind.ResourcePack, PackFileClassifier.Classify(zip.Path));
        Assert.NotEqual(PackFileKind.ShaderPack, PackFileClassifier.Classify(zip.Path));
    }

    [Fact]
    public void ResourcePackGoesToResourcePacks()
    {
        using var zip = new TempZip(new()
        {
            ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":34}}",
            ["assets/minecraft/lang/zh_cn.json"] = "{}",
        });

        Assert.Equal(PackFileKind.ResourcePack, PackFileClassifier.Classify(zip.Path));
        Assert.Equal("resourcepacks", PackFileClassifier.FolderFor(PackFileKind.ResourcePack));
    }

    [Theory]
    [InlineData("shaders/final.fsh")]
    [InlineData("shaderFile_1/shaderFile_L.placebo")]
    [InlineData("shaderpacks/x.xml")]
    public void ShaderPacksGoToShaderPacks(string entry)
    {
        using var zip = new TempZip(new()
        {
            [entry] = "x",
            ["README.txt"] = "x",
        });

        Assert.Equal(PackFileKind.ShaderPack, PackFileClassifier.Classify(zip.Path));
        Assert.Equal("shaderpacks", PackFileClassifier.FolderFor(PackFileKind.ShaderPack));
    }

    [Fact]
    public void RouteMovesTheFileAndFixesTheExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "orap-route-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);

        try
        {
            var path = Path.Combine(mods, "curseforge-123-456.jar");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(zip, "pack.mcmeta", "{}");
                Write(zip, "assets/x/lang.json", "{}");
            }

            var routed = PackFileClassifier.Route(path, root, out var kind);

            Assert.Equal(PackFileKind.ResourcePack, kind);
            Assert.EndsWith(".zip", routed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("resourcepacks", routed, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(routed));
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class TempZip : IDisposable
    {
        public TempZip(Dictionary<string, string> entries)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-zip-" + Guid.NewGuid().ToString("N") + ".jar");
            using var zip = ZipFile.Open(Path, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}