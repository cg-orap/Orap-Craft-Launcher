using System.Text.Json;
using Orion.Core.Auth;
using Orion.Core.Game;
using Orion.Core.Java;
using Orion.Core.Json;
using Orion.Core.Launch;
using Orion.Core.Util;

namespace Orion.Tests;

/// <summary>Pure-logic tests for the metadata rules that decide how a version launches.</summary>
public sealed class VersionLogicTests
{
    private static VersionMetadata Parse(string json)
        => JsonSerializer.Deserialize<VersionMetadata>(json, JsonUtil.Options)
           ?? throw new InvalidOperationException("bad fixture");

    private static readonly RuleContext Windows64 = new()
    {
        OsName = "windows",
        OsVersion = "10.0.22631.0",
        OsArch = "x86_64",
        Features = new Dictionary<string, bool>(),
    };

    [Fact]
    public void NoRulesMeansAllowed()
    {
        Assert.True(RuleEvaluator.IsAllowed(null, Windows64));
        Assert.True(RuleEvaluator.IsAllowed([], Windows64));
    }

    [Fact]
    public void AllowRuleForOtherOsIsDenied()
    {
        var metadata = Parse("""
            {"id":"x","libraries":[{"name":"a:b:1","rules":[{"action":"allow","os":{"name":"osx"}}]}]}
            """);
        Assert.False(RuleEvaluator.IsAllowed(metadata.Libraries![0].Rules, Windows64));
    }

    [Fact]
    public void LaterDenyRuleOverridesEarlierAllow()
    {
        var rules = Parse("""
            {"name":"a","libraries":[{"name":"x:y:1","rules":[
              {"action":"allow"},
              {"action":"deny","os":{"name":"windows"}}
            ]}]}
            """).Libraries![0].Rules;

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows64));
    }

    [Fact]
    public void FeatureRulesMatchOnlyWhenFeatureIsSet()
    {
        var rules = Parse("""
            {"name":"a","libraries":[{"name":"x:y:1","rules":[{"action":"allow","features":{"is_demo_user":true}}]}]}
            """).Libraries![0].Rules;

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows64));

        var demo = new RuleContext
        {
            OsName = "windows",
            OsVersion = "10.0",
            OsArch = "x86_64",
            Features = new Dictionary<string, bool> { ["is_demo_user"] = true },
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, demo));
    }

    [Fact]
    public void OsVersionRegexIsEvaluated()
    {
        var rules = Parse("""
            {"name":"a","libraries":[{"name":"x:y:1","rules":[{"action":"allow","os":{"name":"windows","version":"^10\\."}}]}]}
            """).Libraries![0].Rules;

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows64));
    }

    [Fact]
    public void MalformedVersionRegexDoesNotThrow()
    {
        var rules = Parse("""
            {"name":"a","libraries":[{"name":"x:y:1","rules":[{"action":"allow","os":{"name":"windows","version":"([a-"}}]}]}
            """).Libraries![0].Rules;

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows64));
    }

    [Theory]
    [InlineData("org.lwjgl:lwjgl:3.3.3", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("net.fabricmc:sponge-mixin:0.15.3+mixin.0.8.7", "net/fabricmc/sponge-mixin/0.15.3+mixin.0.8.7/sponge-mixin-0.15.3+mixin.0.8.7.jar")]
    public void MavenCoordinateProducesRepositoryPath(string name, string expected)
    {
        Assert.True(MavenCoordinate.TryParse(name, out var coordinate));
        Assert.Equal(expected, coordinate.ToRepositoryPath());
    }

    [Fact]
    public void MavenCoordinateParsesExtension()
    {
        Assert.True(MavenCoordinate.TryParse("net.neoforged:neoform:1.21.1-20240808.144430@zip", out var coordinate));
        Assert.Equal("zip", coordinate.Extension);
        Assert.Equal("net/neoforged/neoform/1.21.1-20240808.144430/neoform-1.21.1-20240808.144430.zip", coordinate.ToRepositoryPath());
    }

    [Theory]
    [InlineData("natives-windows", true)]
    [InlineData("natives-windows-x86", false)]
    [InlineData("natives-windows-arm64", false)]
    [InlineData("natives-linux", false)]
    [InlineData("natives-macos", false)]
    public void NativeClassifierFiltersByArchitecture(string classifier, bool expected)
    {
        Assert.Equal(expected, NativeClassifier.MatchesCurrentArchitecture(classifier, "windows", "x86_64"));
    }

    [Fact]
    public void NativeClassifierAcceptsArm64AndX86Suffixes()
    {
        Assert.True(NativeClassifier.MatchesCurrentArchitecture("natives-windows-arm64", "windows", "arm64"));
        Assert.True(NativeClassifier.MatchesCurrentArchitecture("natives-windows-x86", "windows", "x86"));
        Assert.False(NativeClassifier.MatchesCurrentArchitecture("natives-windows-x86", "windows", "arm64"));
    }

    [Fact]
    public void NonNativeClassifierIsAlwaysAccepted()
    {
        Assert.True(NativeClassifier.MatchesCurrentArchitecture("sources", "windows", "x86_64"));
    }

    [Fact]
    public void LibraryPlannerSkipsForeignArchitectureNativesAndKeepsTheRightOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-planner", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);

        // Modern (1.19+) shape: natives are ordinary library entries whose classifier encodes
        // the OS/architecture and whose rules only filter the OS.
        var metadata = Parse("""
            {"id":"1.21.1","libraries":[
              {"name":"org.lwjgl:lwjgl:3.3.3",
               "downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar","url":"https://libraries.minecraft.net/x.jar","sha1":"aa","size":1}}},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar","url":"https://libraries.minecraft.net/y.jar","sha1":"bb","size":2}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-x86","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-x86.jar","url":"https://libraries.minecraft.net/z.jar","sha1":"cc","size":3}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-windows-arm64","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-arm64.jar","url":"https://libraries.minecraft.net/v.jar","sha1":"ee","size":5}},"rules":[{"action":"allow","os":{"name":"windows"}}]},
              {"name":"org.lwjgl:lwjgl:3.3.3:natives-linux","downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar","url":"https://libraries.minecraft.net/w.jar","sha1":"dd","size":4}}}
            ]}
            """);

        var plan = LibraryPlanner.Plan(paths, metadata.Libraries, Windows64);

        Assert.Contains(plan.Classpath, f => f.RelativePath.EndsWith("lwjgl-3.3.3.jar", StringComparison.Ordinal));
        Assert.Contains(plan.Classpath, f => f.RelativePath.EndsWith("natives-windows.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Classpath, f => f.RelativePath.Contains("natives-windows-x86", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Classpath, f => f.RelativePath.Contains("natives-windows-arm64", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Classpath, f => f.RelativePath.Contains("natives-linux", StringComparison.Ordinal));
        Assert.Contains(plan.Natives, f => f.RelativePath.EndsWith("natives-windows.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryPlannerUsesLegacyNativesMapForOldVersions()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-planner", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);

        var metadata = Parse("""
            {"id":"1.12.2","libraries":[
              {"name":"org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
               "natives":{"windows":"natives-windows","linux":"natives-linux","osx":"natives-osx"},
               "extract":{"exclude":["META-INF/"]},
               "downloads":{"classifiers":{
                  "natives-windows":{"path":"org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-windows.jar","url":"https://libraries.minecraft.net/nw.jar","sha1":"11","size":10},
                  "natives-linux":{"path":"org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-linux.jar","url":"https://libraries.minecraft.net/nl.jar","sha1":"22","size":11}
               }}}
            ]}
            """);

        var plan = LibraryPlanner.Plan(paths, metadata.Libraries, Windows64);

        var native = Assert.Single(plan.Natives);
        Assert.EndsWith("-natives-windows.jar", native.RelativePath, StringComparison.Ordinal);
        Assert.False(native.OnClasspath);
        Assert.Contains("META-INF/", native.ExtractExclude!);
    }

    [Fact]
    public void LibraryPlannerResolvesMavenStyleEntriesWithoutDownloadsBlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-planner", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);

        var metadata = Parse("""
            {"id":"fabric-loader-0.16.5-1.21.1","libraries":[
              {"name":"org.ow2.asm:asm:9.6","url":"https://maven.fabricmc.net/","sha1":"aa205cf0a06dbd8e04ece91c0b37c3f5d567546a","size":123598}
            ]}
            """);

        var plan = LibraryPlanner.Plan(paths, metadata.Libraries, Windows64);

        var file = Assert.Single(plan.Classpath);
        Assert.EndsWith("asm-9.6.jar", file.RelativePath, StringComparison.Ordinal);
        Assert.Equal("aa205cf0a06dbd8e04ece91c0b37c3f5d567546a", file.Sha1);
        Assert.Equal("https://maven.fabricmc.net/org/ow2/asm/asm/9.6/asm-9.6.jar", file.Urls[0].ToString());
    }

    [Fact]
    public void MergeConcatenatesArgumentsRootFirstAndOverridesScalars()
    {
        var child = Parse("""
            {"id":"neoforge-21.1.264","inheritsFrom":"1.21.1","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
             "arguments":{"game":["--fml.mcVersion","1.21.1"],"jvm":["-DignoreList=x"]},
             "libraries":[{"name":"net.neoforged:neoforge:21.1.264:client"}]}
            """);

        var parent = Parse("""
            {"id":"1.21.1","mainClass":"net.minecraft.client.main.Main",
             "arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]},
             "assets":"17","libraries":[{"name":"com.mojang:brigadier:1.0.18"}]}
            """);

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
        Assert.Equal("17", merged.Assets);

        // Root arguments first, then the child's.
        Assert.Equal("--username", merged.Arguments!.Game![0].Values[0]);
        Assert.Equal("--fml.mcVersion", merged.Arguments.Game![2].Values[0]);
        Assert.Equal("-cp", merged.Arguments.Jvm![0].Values[0]);
        Assert.Equal("-DignoreList=x", merged.Arguments.Jvm![2].Values[0]);

        // Both library sets survive.
        Assert.Contains(merged.Libraries!, l => l.Name == "com.mojang:brigadier:1.0.18");
        Assert.Contains(merged.Libraries!, l => l.Name == "net.neoforged:neoforge:21.1.264:client");
    }

    [Fact]
    public void MergeReplacesIdenticalLibraryCoordinateInPlace()
    {
        var child = Parse("""{"id":"c","libraries":[{"name":"org.ow2.asm:asm:9.10.1"}]}""");
        var parent = Parse("""{"id":"p","libraries":[{"name":"org.ow2.asm:asm:9.3"},{"name":"other:lib:1"}]}""");

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal(2, merged.Libraries!.Count);
        Assert.Equal("org.ow2.asm:asm:9.10.1", merged.Libraries![0].Name);
        Assert.Equal("other:lib:1", merged.Libraries![1].Name);
    }

    [Fact]
    public void MergeKeepsLegacyMinecraftArgumentsReplacedNotJoined()
    {
        var child = Parse("""{"id":"c","minecraftArguments":"--child"}""");
        var parent = Parse("""{"id":"p","minecraftArguments":"--parent"}""");

        var merged = VersionResolver.Merge([child, parent]);
        Assert.Equal("--child", merged.MinecraftArguments);
    }

    [Fact]
    public void ArgumentNodeConverterReadsStringsObjectsAndArrays()
    {
        var metadata = Parse("""
            {"id":"x","arguments":{"game":["--a",{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}"]}],"jvm":[]}}
            """);

        var game = metadata.Arguments!.Game!;
        Assert.Equal(2, game.Count);
        Assert.Equal("--a", game[0].Values[0]);
        Assert.NotNull(game[1].Rules);
        Assert.Equal(2, game[1].Values.Count);
    }

    [Fact]
    public void SubstituteReplacesKnownTokensAndReportsUnknownOnes()
    {
        var variables = new Dictionary<string, string> { ["auth_player_name"] = "Steve", ["version_name"] = "1.21.1" };
        var warnings = new List<string>();

        var result = LaunchPlanner.Substitute("--username ${auth_player_name} --version ${version_name} --x ${nope}", variables, warnings);

        Assert.Equal("--username Steve --version 1.21.1 --x ", result);
        Assert.Single(warnings);
    }

    [Fact]
    public void SplitArgumentsHonoursQuotes()
    {
        var parts = LaunchPlanner.SplitArguments("""-Dfoo="a b" --bar baz""");
        Assert.Equal(["-Dfoo=a b", "--bar", "baz"], parts);
    }

    [Fact]
    public void LogFormatterTurnsLog4jXmlIntoReadableLines()
    {
        var formatter = new GameLogFormatter();
        var lines = new List<string>();

        lines.AddRange(formatter.Process("""<log4j:Event logger="fgo" timestamp="1700000000000" level="INFO" thread="Render thread">"""));
        lines.AddRange(formatter.Process("""  <log4j:Message><![CDATA[Setting user: Tester]]></log4j:Message>"""));
        lines.AddRange(formatter.Process("</log4j:Event>"));

        var line = Assert.Single(lines);
        Assert.Contains("Setting user: Tester", line, StringComparison.Ordinal);
        Assert.Contains("[Render thread/INFO]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LogFormatterPassesPlainTextThrough()
    {
        var formatter = new GameLogFormatter();
        var lines = formatter.Process("Exception in thread main java.lang.RuntimeException");
        Assert.Single(lines);
        Assert.StartsWith("Exception in thread main", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LogFormatterHandlesMultiLineCdata()
    {
        var formatter = new GameLogFormatter();
        var output = new List<string>();
        output.AddRange(formatter.Process("""<log4j:Event logger="x" timestamp="1" level="ERROR" thread="main">"""));
        output.AddRange(formatter.Process("<log4j:Message><![CDATA[line one"));
        output.AddRange(formatter.Process("line two]]></log4j:Message>"));
        output.AddRange(formatter.Process("</log4j:Event>"));

        Assert.Single(output);
        Assert.Contains("line two", output[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerBuildsMemoryAndClasspathArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-launch", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);
        paths.EnsureBaseDirectories();

        var metadata = Parse("""
            {"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","assets":"17",
             "assetIndex":{"id":"17"},
             "arguments":{"game":["--username","${auth_player_name}","--version","${version_name}","--gameDir","${game_directory}"],
                          "jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}"]},
             "libraries":[]}
            """);

        // The client jar must exist or the planner only warns.
        var jarPath = paths.VersionJarPath("1.21.1");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        File.WriteAllBytes(jarPath, [0x50, 0x4B, 0x03, 0x04]);

        var resolved = new ResolvedVersion
        {
            Id = "1.21.1",
            Metadata = metadata,
            Chain = ["1.21.1"],
            RootVersionId = "1.21.1",
            ClientJarVersionId = "1.21.1",
            AssetIndexId = "17",
        };

        var account = OfflineAccounts.Create("Tester");
        var java = new JavaRuntime
        {
            ExecutablePath = Path.Combine(root, "java.exe"),
            Home = root,
            MajorVersion = 21,
            Version = "21.0.4",
            Is64Bit = true,
            Arch = "x86_64",
        };

        var plan = LaunchPlanner.Build(paths, new LaunchRequest
        {
            Version = resolved,
            Account = account,
            Java = java,
            GameDirectory = Path.Combine(root, "game"),
            MemoryMb = 2048,
            MinMemoryMb = 512,
        });

        Assert.Contains("-Xmx2048M", plan.Arguments);
        Assert.Contains("-Xms512M", plan.Arguments);
        Assert.Contains("-cp", plan.Arguments);
        Assert.Equal("net.minecraft.client.main.Main", plan.MainClass);
        Assert.Contains("--username", plan.Arguments);
        Assert.Contains("Tester", plan.Arguments);
        Assert.Contains(plan.Classpath.Split(Path.PathSeparator), part => part.EndsWith("1.21.1.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Arguments, a => a.Contains("${", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannerOmitsLauncherJarWhenLoaderOwnsTheGameJar()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-launch", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);
        paths.EnsureBaseDirectories();

        var metadata = Parse("""
            {"id":"neoforge-21.1.264","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
             "arguments":{"game":[],"jvm":["-DignoreList=client-extra,${version_name}.jar","-p","x","--add-modules","ALL-MODULE-PATH"]},
             "libraries":[]}
            """);

        var resolved = new ResolvedVersion
        {
            Id = "neoforge-21.1.264",
            Metadata = metadata,
            Chain = ["neoforge-21.1.264", "1.21.1"],
            RootVersionId = "1.21.1",
            ClientJarVersionId = "1.21.1",
            AssetIndexId = "17",
        };

        Assert.True(LaunchPlanner.LoaderManagesGameJar(resolved));

        var jar = LaunchPlanner.SelectClientJar(paths, resolved, []);
        Assert.Null(jar);
    }

    [Fact]
    public void PlannerKeepsInheritedJarForFabricStyleProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-launch", Guid.NewGuid().ToString("N"));
        var paths = new GamePaths(root);
        paths.EnsureBaseDirectories();

        var jarPath = paths.VersionJarPath("1.21.1");
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        File.WriteAllBytes(jarPath, [0x50, 0x4B, 0x03, 0x04]);

        var resolved = new ResolvedVersion
        {
            Id = "fabric-loader-0.16.5-1.21.1",
            Metadata = Parse("""{"id":"fabric","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","arguments":{"jvm":["-cp","${classpath}"],"game":[]}}"""),
            Chain = ["fabric-loader-0.16.5-1.21.1", "1.21.1"],
            RootVersionId = "1.21.1",
            ClientJarVersionId = "1.21.1",
            AssetIndexId = "17",
        };

        var jar = LaunchPlanner.SelectClientJar(paths, resolved, []);
        Assert.NotNull(jar);
        Assert.EndsWith("1.21.1.jar", jar!, StringComparison.Ordinal);
    }
}
