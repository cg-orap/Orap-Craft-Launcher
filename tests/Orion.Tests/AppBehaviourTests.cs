using System.Text.Json;
using Orion.Core.Auth;
using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Launch;
using Orion.Core.Mods;
using Orion.Core.Settings;
using Orion.Core.Util;

namespace Orion.Tests;

/// <summary>Behavioural tests for accounts, instances, imports, mods and diagnostics.</summary>
public sealed class AppBehaviourTests : IDisposable
{
    private readonly string _root;

    public AppBehaviourTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orion-behaviour", Guid.NewGuid().ToString("N"));
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

    [Theory]
    // Independently computed as MD5("OfflinePlayer:<name>") with the version-3 and RFC 4122
    // variant nibbles applied, i.e. exactly what UUID.nameUUIDFromBytes produces on a server.
    [InlineData("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")]
    [InlineData("Tester", "f3d28cb0-7225-3cb1-baeb-2dadd2be89ae")]
    [InlineData("Steve", "5627dd98-e6be-3c21-b8a8-e92344183641")]
    public void OfflineUuidMatchesCanonicalNameUuid(string name, string expected)
    {
        var uuid = OfflineUuid.FromPlayerName(name);
        Assert.Equal(expected, uuid.ToString());
        Assert.Equal(expected.Replace("-", "", StringComparison.Ordinal), uuid.ToString("N"));
    }

    [Fact]
    public void OfflineUuidIsStableAndNameDependent()
    {
        var first = OfflineUuid.FromPlayerName("Steve");
        var second = OfflineUuid.FromPlayerName("Steve");
        var other = OfflineUuid.FromPlayerName("Alex");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);

        var text = first.ToString();
        Assert.Equal('3', text[14]);              // version digit
        Assert.Contains(text[19], "89ab");        // variant digit
    }

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("ab", false)]
    [InlineData("way_too_long_for_minecraft", false)]
    [InlineData("bad name", false)]
    [InlineData("ok_name_1", true)]
    public void OfflineNameValidationMatchesVanillaRules(string name, bool valid)
    {
        Assert.Equal(valid, OfflineAccounts.IsValidName(name));
    }

    [Fact]
    public void OfflineAccountNeverCarriesARealToken()
    {
        var account = OfflineAccounts.Create("Tester");
        Assert.Equal(AccountType.Offline, account.Type);
        Assert.Equal("legacy", account.UserType);
        Assert.Equal("0", account.AccessToken);
        Assert.Null(account.RefreshToken);
    }

    [Fact]
    public async Task AccountStoreEncryptsTokensOnDisk()
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new AccountStore(path);

        var account = new Account
        {
            Id = "msa-1",
            Username = "Notch",
            Uuid = "069a79f444e94726a5befca90e38aaf5",
            Type = AccountType.Microsoft,
            AccessToken = "SUPER-SECRET-ACCESS-TOKEN",
            RefreshToken = "SUPER-SECRET-REFRESH-TOKEN",
            XboxUserId = "1234567890",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        await store.SaveAsync([account]);

        var raw = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("SUPER-SECRET-ACCESS-TOKEN", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER-SECRET-REFRESH-TOKEN", raw, StringComparison.Ordinal);
        Assert.Contains("protectedTokens", raw, StringComparison.Ordinal);

        var reloaded = await new AccountStore(path).LoadAsync();
        var restored = Assert.Single(reloaded);
        Assert.Equal("Notch", restored.Username);
        Assert.Equal("SUPER-SECRET-ACCESS-TOKEN", restored.AccessToken);
        Assert.Equal("SUPER-SECRET-REFRESH-TOKEN", restored.RefreshToken);
        Assert.Equal("1234567890", restored.XboxUserId);
    }

    [Fact]
    public void AccountToStringNeverLeaksTokens()
    {
        var account = OfflineAccounts.Create("Tester");
        account.AccessToken = "token-abc";
        Assert.DoesNotContain("token-abc", account.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://login.live.com/oauth20_desktop.srf?code=M.R3_BAY.abc-123&lc=1033", "M.R3_BAY.abc-123")]
    [InlineData("code=M.R3_BAY.xyz", "M.R3_BAY.xyz")]
    [InlineData("M.R3_BAY.abcdefghij", "M.R3_BAY.abcdefghij")]
    public void AuthorizationCodeExtractionHandlesEveryPasteShape(string input, string expected)
    {
        Assert.Equal(expected, MicrosoftAuthService.TryExtractCode(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url at all")]
    [InlineData("https://login.live.com/oauth20_desktop.srf?error=access_denied")]
    public void AuthorizationCodeExtractionRejectsJunk(string input)
    {
        Assert.Null(MicrosoftAuthService.TryExtractCode(input));
    }

    [Fact]
    public void AuthorizationUriContainsRequiredParameters()
    {
        var service = new MicrosoftAuthService(new HttpClient());
        var uri = service.BuildAuthorizationUri("state-1");

        Assert.Contains("client_id=", uri.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", uri.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=", uri.Query, StringComparison.Ordinal);
        Assert.Contains("scope=service", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstanceCreationIsolatesGameDirectories()
    {
        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        var manager = new InstanceManager(paths);

        var first = await manager.CreateAsync("生存", "1.21.1", "1.21.1");
        var second = await manager.CreateAsync("创造", "1.21.1", "1.21.1");

        Assert.NotEqual(first.GameDirectory, second.GameDirectory);
        Assert.True(Directory.Exists(Path.Combine(first.GameDirectory, "saves")));
        Assert.True(Directory.Exists(Path.Combine(second.GameDirectory, "saves")));

        // A world written into one instance must be invisible to the other.
        var worldFile = Path.Combine(first.GameDirectory, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(worldFile)!);
        await File.WriteAllTextAsync(worldFile, "world");

        Assert.True(File.Exists(worldFile));
        Assert.False(Directory.Exists(Path.Combine(second.GameDirectory, "saves", "world")));

        var loaded = manager.LoadAll();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task RemovingInstanceSendsFilesToTheRecycleBin()
    {
        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        var manager = new InstanceManager(paths);

        var instance = await manager.CreateAsync("要删除的", "1.21.1", "1.21.1");
        var saveFile = Path.Combine(instance.GameDirectory, "saves", "world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(saveFile)!);
        await File.WriteAllTextAsync(saveFile, "precious");

        Assert.True(InstanceManager.HasUserData(instance.GameDirectory));

        var recycled = manager.Remove(instance, out var reported);

        // The folder leaves its location (it now lives in the Recycle Bin), so 移除 stays
        // reversible from Explorer instead of only from a launcher-owned trash folder.
        Assert.NotNull(recycled);
        Assert.NotNull(reported);
        Assert.False(Directory.Exists(paths.InstanceDir(instance.Id)));
        Assert.False(Directory.Exists(reported!), "The instance folder must no longer be in place.");
    }

    [Fact]
    public async Task ImportingMinecraftFolderRegistersEveryVersionAsExternal()
    {
        var source = Path.Combine(_root, "imported-minecraft");
        var versionDir = Path.Combine(source, "versions", "1.20.1-forge-47.2.0");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "1.20.1-forge-47.2.0.json"), """
            {"id":"1.20.1-forge-47.2.0","inheritsFrom":"1.20.1","mainClass":"cpw.mods.modlauncher.Launcher","type":"release"}
            """);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "1.20.1-forge-47.2.0.jar"), "fake");
        Directory.CreateDirectory(Path.Combine(source, "saves", "myworld"));

        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        var manager = new InstanceManager(paths);
        var importer = new InstanceImporter(manager, paths);

        var result = await importer.ImportAsync(source);

        Assert.True(result.Success);
        var instance = Assert.Single(result.Instances);
        Assert.True(instance.IsExternal);
        Assert.Equal(LoaderKind.Forge, instance.Loader);
        Assert.Equal("1.20.1", instance.GameVersion);
        Assert.Equal(Path.GetFullPath(source), instance.GameDirectory);

        // Importing must never write into the source directory.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories),
            f => f.Contains("instance.json", StringComparison.OrdinalIgnoreCase));

        var roots = importer.VersionRootsOf(result.Instances);
        Assert.Contains(Path.Combine(source, "versions"), roots);
    }

    [Fact]
    public async Task ImportingPrismInstanceReadsPackAndMemory()
    {
        var source = Path.Combine(_root, "PrismInstance");
        Directory.CreateDirectory(Path.Combine(source, ".minecraft", "versions"));
        await File.WriteAllTextAsync(Path.Combine(source, "instance.cfg"), """
            InstanceType=OneSix
            name=我的整合包
            MaxMemAlloc=6144
            MinMemAlloc=1024
            JvmArgs=-XX:+UseG1GC
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "mmc-pack.json"), """
            {"formatVersion":1,"components":[
              {"uid":"net.minecraft","version":"1.21.1","important":true},
              {"uid":"net.neoforged","version":"21.1.264"}
            ]}
            """);

        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        var manager = new InstanceManager(paths);
        var importer = new InstanceImporter(manager, paths);

        var result = await importer.ImportAsync(source);

        Assert.True(result.Success);
        var instance = Assert.Single(result.Instances);
        Assert.Equal("我的整合包", instance.Name);
        Assert.Equal(LoaderKind.NeoForge, instance.Loader);
        Assert.Equal("21.1.264", instance.LoaderVersion);
        Assert.Equal(6144, instance.MemoryMb);
        Assert.Equal("-XX:+UseG1GC", instance.JvmArguments);
        Assert.Equal(Path.Combine(source, ".minecraft"), instance.GameDirectory);
    }

    [Fact]
    public void IniFileReadsPrismStyleValues()
    {
        var path = Path.Combine(_root, "instance.cfg");
        File.WriteAllText(path, """
            # comment
            [General]
            name=Test
            MaxMemAlloc = 4096
            """);

        var ini = IniFile.Load(path);
        Assert.Equal("Test", ini.Get("name"));
        Assert.Equal("4096", ini.Get("MaxMemAlloc"));
        Assert.Null(ini.Get("missing"));
    }

    [Fact]
    public async Task SettingsRoundTripThroughDisk()
    {
        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        var store = new SettingsStore(paths);

        var settings = new LauncherSettings
        {
            MemoryMb = 8192,
            MaxConcurrency = 16,
            Source = Core.Net.DownloadSource.BmclApi,
            Theme = "Light",
            ProxyUrl = "http://127.0.0.1:7890",
        };

        await store.SaveAsync(settings);

        var reloaded = new SettingsStore(paths).Load();
        Assert.Equal(8192, reloaded.MemoryMb);
        Assert.Equal(16, reloaded.MaxConcurrency);
        Assert.Equal(Core.Net.DownloadSource.BmclApi, reloaded.Source);
        Assert.Equal("Light", reloaded.Theme);
        Assert.Equal("http://127.0.0.1:7890", reloaded.ProxyUrl);
    }

    [Fact]
    public void SettingsSurviveCorruptFile()
    {
        var paths = new GamePaths(_root);
        paths.EnsureBaseDirectories();
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var settings = new SettingsStore(paths).Load();

        Assert.Equal(4096, settings.MemoryMb); // defaults preserved, no exception
    }

    [Fact]
    public void TomlModMetadataIsParsed()
    {
        var mod = new ModInfo { FilePath = "x.jar", FileName = "x.jar" };

        ModScanner.ParseToml("""
            modLoader="javafml"
            loaderVersion="[1,)"

            [[mods]]
            modId="create"
            version="0.5.1"
            displayName="Create"
            description="A tech mod"

            [[dependencies.create]]
            modId="minecraft"
            mandatory=true

            [[dependencies.create]]
            modId="flywheel"
            """, mod);

        Assert.Equal("create", mod.ModId);
        Assert.Equal("0.5.1", mod.Version);
        Assert.Equal("Create", mod.Name);
        Assert.Equal("A tech mod", mod.Description);
        Assert.Contains("flywheel", mod.Dependencies);
        Assert.DoesNotContain("minecraft", mod.Dependencies);
    }

    [Fact]
    public void ModCompatibilityRespectsLoaderFamilies()
    {
        var fabricMod = new ModInfo { FilePath = "a.jar", FileName = "a.jar", Loader = "fabric" };
        var forgeMod = new ModInfo { FilePath = "b.jar", FileName = "b.jar", Loader = "forge" };
        var neoMod = new ModInfo { FilePath = "c.jar", FileName = "c.jar", Loader = "neoforge" };

        Assert.True(ModScanner.IsCompatibleWith(fabricMod, LoaderKind.Fabric));
        Assert.False(ModScanner.IsCompatibleWith(fabricMod, LoaderKind.Forge));
        Assert.True(ModScanner.IsCompatibleWith(fabricMod, LoaderKind.Quilt));
        Assert.True(ModScanner.IsCompatibleWith(neoMod, LoaderKind.NeoForge));
        Assert.False(ModScanner.IsCompatibleWith(forgeMod, LoaderKind.Vanilla));
    }

    [Fact]
    public void ModDisableRenamesFileAndKeepsItOnDisk()
    {
        var modsDir = Path.Combine(_root, "mods");
        Directory.CreateDirectory(modsDir);
        var jar = Path.Combine(modsDir, "example.jar");
        File.WriteAllText(jar, "jar");

        var mod = new ModInfo { FilePath = jar, FileName = "example.jar", Enabled = true };
        var disabledPath = ModScanner.SetEnabled(mod, false);

        Assert.EndsWith(".jar.disabled", disabledPath, StringComparison.Ordinal);
        Assert.False(File.Exists(jar));
        Assert.True(File.Exists(disabledPath));

        var reenabled = ModScanner.SetEnabled(mod, true);
        Assert.EndsWith("example.jar", reenabled, StringComparison.Ordinal);
        Assert.True(File.Exists(reenabled));
    }

    [Fact]
    public async Task CrashAnalyzerMapsOutOfMemoryAndExitCodes()
    {
        var gameDir = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameDir);

        var analysis = await CrashAnalyzer.AnalyzeAsync(gameDir, 1, [
            "[12:00:00] [main/ERROR]: java.lang.OutOfMemoryError: Java heap space",
        ]);

        Assert.True(analysis.IsCrash);
        Assert.Contains("内存不足", analysis.Summary, StringComparison.Ordinal);
        Assert.NotEmpty(analysis.Evidence);
    }

    [Fact]
    public async Task CrashAnalyzerReadsCrashReportFile()
    {
        var gameDir = Path.Combine(_root, "game");
        var crashDir = Path.Combine(gameDir, "crash-reports");
        Directory.CreateDirectory(crashDir);
        await File.WriteAllTextAsync(Path.Combine(crashDir, "crash-2026-01-01_00.00.00-client.txt"), """
            ---- Minecraft Crash Report ----
            // I let you down. Sorry :(

            Time: 2026-01-01 00:00:00
            Description: Rendering screen

            java.lang.NullPointerException: Cannot invoke "x" because "y" is null
            	at net.minecraft.Foo.bar(Foo.java:1)
            """);

        var analysis = await CrashAnalyzer.AnalyzeAsync(gameDir, 1, []);

        Assert.NotNull(analysis.CrashReportPath);
        Assert.Contains(analysis.Evidence, e => e.Contains("Rendering screen", StringComparison.Ordinal));
        Assert.Contains(analysis.Causes, c => c.Contains("NullPointerException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrashAnalyzerHandlesNormalExit()
    {
        var analysis = await CrashAnalyzer.AnalyzeAsync(_root, 0, ["[12:00:00] [main/INFO]: Stopping!"]);
        Assert.False(analysis.IsCrash);
    }

    [Fact]
    public void JsonUtilIgnoresUnknownFieldsAndAcceptsComments()
    {
        var metadata = JsonUtil.Deserialize<Core.Json.VersionMetadata>("""
            {
              // a comment
              "id": "1.21.1",
              "someFutureField": {"nested": [1,2,3]},
              "minimumLauncherVersion": 21,
            }
            """);

        Assert.NotNull(metadata);
        Assert.Equal("1.21.1", metadata!.Id);
        Assert.Equal(21, metadata.MinimumLauncherVersion);
    }

    [Fact]
    public void NumericVersionComparerOrdersLoaderVersionsCorrectly()
    {
        var versions = new[] { "21.1.9", "21.1.99", "21.1.264", "21.1.100" };
        var ordered = versions.OrderByDescending(v => v, Core.Loaders.NumericVersionComparer.Instance).ToArray();

        Assert.Equal(["21.1.264", "21.1.100", "21.1.99", "21.1.9"], ordered);
    }

    [Fact]
    public void GameLogFileRedactionRemovesAccessToken()
    {
        var commandLine = "java -cp x net.minecraft.client.main.Main --accessToken abcdef123456 --username Steve";
        var redacted = GameLauncher.Redact(commandLine, "abcdef123456");

        Assert.DoesNotContain("abcdef123456", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
        Assert.Contains("--username Steve", redacted, StringComparison.Ordinal);
    }
}
