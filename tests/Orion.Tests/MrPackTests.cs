using System.IO.Compression;
using System.Text.Json;
using Orion.Core.Instances;
using Orion.Core.Mods;

namespace Orion.Tests;

/// <summary>Modrinth .mrpack export/import round trip (no network required).</summary>
public sealed class MrPackTests : IDisposable
{
    private readonly string _root;

    public MrPackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orion-mrpack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ExportProducesAValidPackAndSkipsSaves()
    {
        var gameDirectory = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "config"));
        Directory.CreateDirectory(Path.Combine(gameDirectory, "saves", "world"));

        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "mods", "example.jar"), "jar-bytes");
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "config", "example.toml"), "key = 1");
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "saves", "world", "level.dat"), "precious-save");
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "options.txt"), "fov:0.5");

        var instance = new Instance
        {
            Id = "test-instance",
            Name = "测试实例",
            VersionId = "neoforge-21.1.264",
            GameVersion = "1.21.1",
            Loader = LoaderKind.NeoForge,
            LoaderVersion = "21.1.264",
            GameDirectory = gameDirectory,
        };

        var packPath = Path.Combine(_root, "out.mrpack");
        var result = await MrPackExporter.ExportAsync(instance, packPath);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(packPath));
        Assert.True(result.OverridesCopied >= 3, $"only {result.OverridesCopied} files were packed");

        var index = await MrPackInstaller.ReadIndexAsync(packPath);
        Assert.NotNull(index);
        Assert.Equal("1.21.1", index!.Dependencies["minecraft"]);
        Assert.Equal("21.1.264", index.Dependencies["neoforge"]);
        Assert.Equal("测试实例", index.Name);

        using var archive = ZipFile.OpenRead(packPath);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

        Assert.Contains("overrides/mods/example.jar", names);
        Assert.Contains("overrides/config/example.toml", names);
        Assert.Contains("overrides/options.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("saves/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("modrinth.index.json", names);
    }

    [Fact]
    public async Task ExportCanIncludeSavesOnRequest()
    {
        var gameDirectory = Path.Combine(_root, "game2");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "saves", "world"));
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "saves", "world", "level.dat"), "save");

        var instance = new Instance
        {
            Id = "i2",
            Name = "带存档",
            VersionId = "1.21.1",
            GameVersion = "1.21.1",
            GameDirectory = gameDirectory,
        };

        var packPath = Path.Combine(_root, "with-saves.mrpack");
        var result = await MrPackExporter.ExportAsync(instance, packPath, new MrPackExporter.ExportOptions { IncludeSaves = true });

        Assert.True(result.Success);

        using var archive = ZipFile.OpenRead(packPath);
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/') == "overrides/saves/world/level.dat");
    }

    [Fact]
    public async Task ReadsAFabricPackIndex()
    {
        var packPath = Path.Combine(_root, "fabric.mrpack");
        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "2.0.0",
            name = "Fabric Pack",
            dependencies = new Dictionary<string, string>
            {
                ["minecraft"] = "1.20.1",
                ["fabric-loader"] = "0.15.11",
            },
            files = new[]
            {
                new
                {
                    path = "mods/sodium.jar",
                    hashes = new Dictionary<string, string> { ["sha1"] = "abc", ["sha512"] = "def" },
                    downloads = new[] { "https://cdn.modrinth.com/data/x/versions/y/sodium.jar" },
                    fileSize = 123,
                    env = new Dictionary<string, string> { ["client"] = "required", ["server"] = "unsupported" },
                },
            },
        };

        using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("modrinth.index.json");
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, index);
        }

        var parsed = await MrPackInstaller.ReadIndexAsync(packPath);

        Assert.NotNull(parsed);
        Assert.Equal("Fabric Pack", parsed!.Name);
        Assert.Equal("0.15.11", parsed.Dependencies["fabric-loader"]);
        var file = Assert.Single(parsed.Files);
        Assert.Equal("abc", file.Sha1);
        Assert.True(file.ClientRelevant);
    }

    [Fact]
    public async Task ReturnsNullForNonPackZip()
    {
        var path = Path.Combine(_root, "not-a-pack.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync("hello"u8.ToArray());
        }

        Assert.Null(await MrPackInstaller.ReadIndexAsync(path));
        Assert.Null(await MrPackInstaller.ReadIndexAsync(Path.Combine(_root, "missing.mrpack")));
    }
}
