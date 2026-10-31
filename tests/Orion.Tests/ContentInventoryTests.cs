using System.IO.Compression;
using System.Text;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 资源包 / 光影 / 数据包的扫描与"只改一行"写回。
/// 写回必须做到：除目标行外逐字节不变——options.txt 写坏了游戏会重置全部设置。
/// </summary>
public sealed class ContentInventoryTests
{
    private const string OptionsSample =
        "version:3955\r\n" +
        "renderDistance:12\r\n" +
        "resourcePacks:[\"vanilla\",\"file/Faithful.zip\"]\r\n" +
        "guiScale:3\r\n" +
        "soundCategory_master:0.5\r\n";

    [Fact]
    public async Task EnablingResourcePacksOnlyChangesThatLine()
    {
        using var game = new GameDir();
        game.Options(OptionsSample);
        game.ResourcePackZip("Stardust.zip", "Stardust", "§bStarry sky", 34);
        game.ResourcePackZip("Faithful.zip", "Faithful", "Vanilla-ish", 34);

        await ContentInventoryService.SetResourcePacksAsync(game.Path, ["Stardust.zip", "Faithful.zip"]);

        var text = File.ReadAllText(Path.Combine(game.Path, "options.txt"));
        var lines = text.Split("\r\n");

        Assert.Equal("resourcePacks:[\"vanilla\",\"file/Stardust.zip\",\"file/Faithful.zip\"]", lines[2]);
        Assert.Equal("version:3955", lines[0]);
        Assert.Equal("renderDistance:12", lines[1]);
        Assert.Equal("guiScale:3", lines[3]);
        Assert.Equal("soundCategory_master:0.5", lines[4]);

        // 备份文件保留了改动前的内容
        Assert.True(File.Exists(Path.Combine(game.Path, "options.txt.orap.bak")));
        Assert.Contains("file/Faithful.zip", File.ReadAllText(Path.Combine(game.Path, "options.txt.orap.bak")));
    }

    [Fact]
    public void ScanningOrdersEnabledPacksByTheirPriority()
    {
        using var game = new GameDir();
        game.Options("resourcePacks:[\"vanilla\",\"file/Second.zip\",\"file/First.zip\"]\n");
        game.ResourcePackZip("First.zip", "First pack", null, 34);
        game.ResourcePackZip("Second.zip", "Second pack", null, 34);
        game.ResourcePackZip("Unused.zip", "Not enabled", null, 34);

        var packs = ContentInventoryService.ScanResourcePacks(game.Path);

        Assert.Equal(3, packs.Count);
        Assert.True(packs[0].Enabled);
        Assert.Equal("Second.zip", packs[0].FileName);
        Assert.Equal(0, packs[0].Priority);
        Assert.Equal("First.zip", packs[1].FileName);
        Assert.False(packs[2].Enabled);
        Assert.Equal("Not enabled", packs[2].DisplayName);
    }

    [Fact]
    public void APackWithoutOptionsEntryIsNotEnabled()
    {
        using var game = new GameDir();
        game.ResourcePackZip("Only.zip", "Only", "desc", 22);

        var packs = ContentInventoryService.ScanResourcePacks(game.Path);

        var pack = Assert.Single(packs);
        Assert.False(pack.Enabled);
        Assert.Equal("Only", pack.DisplayName);
        Assert.Equal("desc", pack.Description);
        Assert.Equal("22", pack.Format);
    }

    [Fact]
    public void ColourCodesAreStrippedFromNames()
    {
        using var game = new GameDir();
        game.ResourcePackZip("Colored.zip", "§6§lGolden", "§7grey text", 15);

        var pack = Assert.Single(ContentInventoryService.ScanResourcePacks(game.Path));

        Assert.Equal("Golden", pack.DisplayName);
        Assert.Equal("grey text", pack.Description);
    }

    [Fact]
    public async Task ShaderSelectionRewritesOnlyShaderPackKey()
    {
        using var game = new GameDir();
        game.IrisProperties("""
            #Iris properties
            maxShadowRenderDistance=32
            shaderPack=Complementary.zip
            enableShaders=true
            """);
        game.ShaderPackZip("Bliss.zip");

        await ContentInventoryService.SetShaderPackAsync(game.Path, "Bliss.zip");

        var text = File.ReadAllText(ContentInventoryService.ShaderPropertiesPath(game.Path)!);
        Assert.Contains("shaderPack=Bliss.zip", text);
        Assert.Contains("#Iris properties", text);
        Assert.Contains("maxShadowRenderDistance=32", text);
        Assert.Contains("enableShaders=true", text);
    }

    [Fact]
    public async Task TurningShadersOffWritesTheOffMarker()
    {
        using var game = new GameDir();
        game.IrisProperties("shaderPack=Bliss.zip\n");

        await ContentInventoryService.SetShaderPackAsync(game.Path, null);

        Assert.Equal("shaderPack=(off)", File.ReadAllText(ContentInventoryService.ShaderPropertiesPath(game.Path)!).Trim());
    }

    [Fact]
    public void ScanningShadersMarksTheConfiguredOne()
    {
        using var game = new GameDir();
        game.IrisProperties("shaderPack=Bliss.zip\n");
        game.ShaderPackZip("Bliss.zip");
        game.ShaderPackZip("Complementary.zip");

        var packs = ContentInventoryService.ScanShaderPacks(game.Path);

        Assert.Equal(2, packs.Count);
        Assert.True(packs[0].Enabled);
        Assert.Equal("Bliss.zip", packs[0].FileName);
        Assert.False(packs[1].Enabled);
    }

    [Fact]
    public async Task ShaderWriteIsANoOpWithoutIrisOrOculus()
    {
        using var game = new GameDir();
        game.ShaderPackZip("Bliss.zip");

        await ContentInventoryService.SetShaderPackAsync(game.Path, "Bliss.zip");

        Assert.Null(ContentInventoryService.ShaderPropertiesPath(game.Path));
    }

    [Fact]
    public void DatapacksAreListedPerWorldAndNeverEnabledHere()
    {
        using var game = new GameDir();
        game.Datapack("My World", "terralith.zip");
        game.Datapack("Another", "custom.zip");

        var packs = ContentInventoryService.ScanDatapacks(game.Path);

        Assert.Equal(2, packs.Count);
        Assert.Contains(packs, p => p.WorldName == "My World" && p.FileName == "terralith.zip");
        Assert.Contains(packs, p => p.WorldName == "Another" && p.FileName == "custom.zip");
    }

    [Fact]
    public void MissingDirectoriesSimplyReturnNothing()
    {
        using var game = new GameDir();

        Assert.Empty(ContentInventoryService.ScanResourcePacks(game.Path));
        Assert.Empty(ContentInventoryService.ScanShaderPacks(game.Path));
        Assert.Empty(ContentInventoryService.ScanDatapacks(game.Path));
    }

    [Theory]
    [InlineData("version:3955\r\nresourcePacks:[\"vanilla\"]\r\n", "resourcePacks:[\"vanilla\",\"file/a.zip\"]", "\r\n")]
    [InlineData("version:3955\nresourcePacks:[\"vanilla\"]\n", "resourcePacks:[\"vanilla\",\"file/a.zip\"]", "\n")]
    public void ReplacingAValueKeepsTheOriginalLineEndings(string original, string expectedLine, string newline)
    {
        var updated = OptionText.ReplaceValue(original, "resourcePacks", "[\"vanilla\",\"file/a.zip\"]");
        Assert.Contains(expectedLine, updated);
        Assert.Contains(newline, updated);

        // 逐行数量不变（只是替换，不新增行）
        Assert.Equal(original.Split('\n').Length, updated.Split('\n').Length);
    }

    [Fact]
    public void ReplacingAMissingKeyAppendsExactlyOneLine()
    {
        var updated = OptionText.ReplaceValue("version:3955\n", "resourcePacks", "[\"vanilla\"]");

        Assert.Equal("version:3955\nresourcePacks:[\"vanilla\"]\n", updated);
    }

    [Fact]
    public void ReadingAValueIgnoresSimilarKeys()
    {
        const string text = "shaderPackListSize=10\nshaderPack=Bliss.zip\n";
        Assert.Equal("Bliss.zip", OptionText.ReadValue(text, "shaderPack", separator: '='));
    }

    private sealed class GameDir : IDisposable
    {
        public GameDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orap-content-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Options(string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, "options.txt"), content, new UTF8Encoding(false));

        public void IrisProperties(string content)
        {
            var config = System.IO.Path.Combine(Path, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(System.IO.Path.Combine(config, "iris.properties"), content, new UTF8Encoding(false));
        }

        public void ResourcePackZip(string fileName, string name, string? description, int format) =>
            WritePack(System.IO.Path.Combine(Path, "resourcepacks"), fileName, name, description, format);

        public void ShaderPackZip(string fileName) =>
            WritePack(System.IO.Path.Combine(Path, "shaderpacks"), fileName, fileName, null, null);

        public void Datapack(string world, string fileName) =>
            WritePack(System.IO.Path.Combine(Path, "saves", world, "datapacks"), fileName, fileName, null, null);

        private static void WritePack(string directory, string fileName, string name, string? description, int? format)
        {
            Directory.CreateDirectory(directory);
            using var zip = ZipFile.Open(System.IO.Path.Combine(directory, fileName), ZipArchiveMode.Create);
            var entry = zip.CreateEntry("pack.mcmeta");
            using var writer = new StreamWriter(entry.Open());

            var desc = description is null ? "null" : $"\"{description}\"";
            var fmt = format?.ToString() ?? "null";
            writer.Write("{\"pack\":{\"pack_format\":" + fmt + ",\"description\":" + desc + ",\"name\":\"" + name + "\"}}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
