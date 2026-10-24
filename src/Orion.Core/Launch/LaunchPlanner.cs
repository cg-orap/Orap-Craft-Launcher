using Orion.Core.Auth;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Java;
using Orion.Core.Json;

namespace Orion.Core.Launch;

public sealed class LaunchRequest
{
    public required ResolvedVersion Version { get; init; }
    public required Account Account { get; init; }
    public required JavaRuntime Java { get; init; }

    /// <summary>The isolated game directory of the instance (saves, mods, options ...).</summary>
    public required string GameDirectory { get; init; }

    public string? PlayerNameOverride { get; init; }
    public string? PlayerUuidOverride { get; init; }

    public int MemoryMb { get; init; } = 4096;
    public int MinMemoryMb { get; init; } = 512;

    public string? ExtraJvmArguments { get; init; }
    public string? ExtraGameArguments { get; init; }

    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
    public bool Fullscreen { get; init; }
    public bool DemoMode { get; init; }
    public bool HasQuickPlaySupport { get; init; }
    public string? ServerAddress { get; init; }
    public int? ServerPort { get; init; }

    public string LauncherName { get; init; } = "OrionLauncher";
    public string LauncherVersion { get; init; } = "0.1.0";

    public IReadOnlyDictionary<string, bool>? ExtraFeatures { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraVariables { get; init; }
}

public sealed class LaunchPlan
{
    public required string JavaPath { get; init; }
    public required string MainClass { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Classpath { get; init; }
    public required string NativesDirectory { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Human readable command line for the log/diagnostics view (tokens are redacted).</summary>
    public string ToDisplayString(string? redactToken = null)
    {
        var parts = Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
        return $"\"{JavaPath}\" " + string.Join(' ', parts);
    }
}

/// <summary>
/// Builds the JVM command line from merged version metadata.
///
/// This type is pure (no I/O, no process start) so the argument generation rules are
/// unit-testable — that is where most launcher compatibility bugs live.
/// </summary>
public static class LaunchPlanner
{
    public static LaunchPlan Build(GamePaths paths, LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(request);

        var version = request.Version;
        var warnings = new List<string>();
        var gameDirectory = Path.GetFullPath(request.GameDirectory);
        Directory.CreateDirectory(gameDirectory);

        // ----- rules context -------------------------------------------------
        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = request.DemoMode,
            ["has_custom_resolution"] = request.WindowWidth is > 0 && request.WindowHeight is > 0,
            ["has_quick_plays_support"] = request.HasQuickPlaySupport || !string.IsNullOrWhiteSpace(request.ServerAddress),
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false,
        };

        if (request.ExtraFeatures is not null)
        {
            foreach (var (key, value) in request.ExtraFeatures) features[key] = value;
        }

        var context = new RuleContext
        {
            OsName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsArch = NormalizeJavaArch(request.Java.Arch),
            Features = features,
        };

        // ----- classpath -----------------------------------------------------
        var libraryPlan = LibraryPlanner.Plan(paths, version.Metadata.Libraries, context);
        var classpathEntries = new List<string>();

        foreach (var file in libraryPlan.Classpath)
        {
            if (!File.Exists(file.AbsolutePath))
            {
                warnings.Add($"缺少依赖库: {file.RelativePath}");
            }

            classpathEntries.Add(file.AbsolutePath);
        }

        var clientJar = SelectClientJar(paths, version, warnings);
        if (clientJar is not null) classpathEntries.Add(clientJar);

        var classpath = string.Join(Path.PathSeparator, classpathEntries);
        var nativesDirectory = paths.VersionNativesDir(version.Id);
        Directory.CreateDirectory(nativesDirectory);

        // ----- variables -----------------------------------------------------
        var playerName = string.IsNullOrWhiteSpace(request.PlayerNameOverride)
            ? request.Account.Username
            : request.PlayerNameOverride;

        var playerUuid = string.IsNullOrWhiteSpace(request.PlayerUuidOverride)
            ? request.Account.EffectiveUuid
            : request.PlayerUuidOverride;

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = playerName,
            ["auth_uuid"] = playerUuid.Replace("-", "", StringComparison.Ordinal),
            ["auth_access_token"] = request.Account.AccessToken ?? "0",
            ["auth_session"] = request.Account.Type == AccountType.Microsoft
                ? $"token:{request.Account.AccessToken}"
                : "-",
            ["auth_xuid"] = request.Account.XboxUserId ?? "",
            ["user_type"] = request.Account.UserType,
            ["user_properties"] = "{}",
            ["clientid"] = "",
            ["version_name"] = version.Id,
            ["version_type"] = version.Metadata.Type ?? "release",
            ["game_directory"] = gameDirectory,
            ["game_assets"] = Path.Combine(paths.AssetVirtualDir, version.AssetIndexId),
            ["assets_root"] = paths.AssetsDir,
            ["assets_index_name"] = version.AssetIndexId,
            ["natives_directory"] = nativesDirectory,
            ["launcher_name"] = request.LauncherName,
            ["launcher_version"] = request.LauncherVersion,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = paths.LibrariesDir,
            ["resolution_width"] = (request.WindowWidth ?? 854).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resolution_height"] = (request.WindowHeight ?? 480).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["quickPlayPath"] = Path.Combine(gameDirectory, "quickPlay", "quickPlay.log"),
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = BuildQuickPlayAddress(request.ServerAddress, request.ServerPort),
            ["quickPlayRealms"] = "",
        };

        if (request.ExtraVariables is not null)
        {
            foreach (var (key, value) in request.ExtraVariables) variables[key] = value;
        }

        // ----- JVM arguments -------------------------------------------------
        var jvmArguments = new List<string>
        {
            $"-Xms{Math.Max(256, request.MinMemoryMb)}M",
            $"-Xmx{Math.Max(512, request.MemoryMb)}M",
        };

        if (!string.IsNullOrWhiteSpace(request.ExtraJvmArguments))
        {
            jvmArguments.AddRange(SplitArguments(request.ExtraJvmArguments));
        }

        var mainClass = version.Metadata.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
            throw new InvalidOperationException($"版本 {version.Id} 未声明 mainClass，无法启动。");

        var jvmFromJson = ExpandNodes(version.Metadata.Arguments?.Jvm, context, variables, warnings);

        if (jvmFromJson.Count == 0)
        {
            // Versions before 1.13 carry no arguments block; supply the historical defaults.
            jvmArguments.Add($"-Djava.library.path={nativesDirectory}");
            jvmArguments.Add("-cp");
            jvmArguments.Add(classpath);
        }
        else
        {
            // Replace any literal placeholder that survived substitution.
            jvmArguments.AddRange(jvmFromJson);
        }

        if (!string.IsNullOrWhiteSpace(request.Account.SkinModel))
        {
            // Cosmetic only; the client ignores unknown properties.
            AppLogDebug("skin-model", request.Account.SkinModel!);
        }

        // ----- logging configuration ----------------------------------------
        var loggingFile = version.Metadata.Logging?.Client?.File;
        var loggingArgument = version.Metadata.Logging?.Client?.Argument;
        if (loggingFile is not null && !string.IsNullOrWhiteSpace(loggingArgument))
        {
            var configPath = Path.Combine(paths.LogConfigsDir, loggingFile.Id);
            if (File.Exists(configPath))
            {
                // The argument is inserted before the main class.
                jvmArguments.Add(loggingArgument.Replace("${path}", configPath, StringComparison.Ordinal));
            }
            else
            {
                warnings.Add($"缺少日志配置 {loggingFile.Id}（游戏仍可启动）。");
            }
        }

        // ----- game arguments ------------------------------------------------
        var gameArguments = new List<string>();

        if (version.Metadata.Arguments?.Game is { Count: > 0 } gameNodes)
        {
            gameArguments.AddRange(ExpandNodes(gameNodes, context, variables, warnings));
        }
        else if (!string.IsNullOrWhiteSpace(version.Metadata.MinecraftArguments))
        {
            gameArguments.AddRange(SplitArguments(version.Metadata.MinecraftArguments));
        }

        if (gameArguments.Count == 0)
            warnings.Add("未能生成游戏参数，该版本可能无法正常启动。");

        // Legacy token replacement inside minecraftArguments strings.
        for (var i = 0; i < gameArguments.Count; i++)
        {
            gameArguments[i] = Substitute(gameArguments[i], variables, warnings);
        }

        if (request.Fullscreen) gameArguments.Add("--fullscreen");

        if (!string.IsNullOrWhiteSpace(request.ExtraGameArguments))
        {
            gameArguments.AddRange(SplitArguments(request.ExtraGameArguments));
        }

        var all = new List<string>(jvmArguments.Count + gameArguments.Count + 1);
        all.AddRange(jvmArguments);
        all.Add(mainClass);
        all.AddRange(gameArguments);

        return new LaunchPlan
        {
            JavaPath = request.Java.ExecutablePath,
            MainClass = mainClass,
            Arguments = all,
            WorkingDirectory = gameDirectory,
            Classpath = classpath,
            NativesDirectory = nativesDirectory,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Decides which client JAR belongs on the classpath.
    ///
    /// Modern Forge/NeoForge (1.17+) do something unusual: the patched game jar is supplied by
    /// the loader itself and the version JSON passes
    /// <c>-DignoreList=client-extra,${version_name}.jar</c>, which tells the launcher that its own
    /// client jar must not take part in module resolution. Putting the vanilla jar on the
    /// classpath anyway produces two java modules exporting the same package
    /// (<c>ResolutionException: Modules minecraft and _1._21._1 export package ...</c>), so it is
    /// omitted exactly when the metadata asks for it. Fabric/Quilt and pre-1.17 Forge still need
    /// the inherited jar.
    /// </summary>
    public static string? SelectClientJar(GamePaths paths, ResolvedVersion version, List<string> warnings)
    {
        var ownJar = paths.VersionJarPath(version.Id);
        if (File.Exists(ownJar)) return ownJar;

        if (LoaderManagesGameJar(version) || ProvidesClientJarThroughLibraries(version))
        {
            AppLog.Debug("launch", "client-jar-owned-by-loader", ("version", version.Id));
            return null;
        }

        var inherited = paths.VersionJarPath(version.ClientJarVersionId);
        if (File.Exists(inherited)) return inherited;

        warnings.Add($"缺少客户端 JAR: {inherited}");
        return null;
    }

    /// <summary>
    /// True when the loader replaces the game jar with its own artifact and explicitly asks the
    /// launcher to ignore its jar (<c>-DignoreList=...,${version_name}.jar</c>).
    /// </summary>
    public static bool LoaderManagesGameJar(ResolvedVersion version)
    {
        var jvm = version.Metadata.Arguments?.Jvm;
        if (jvm is null) return false;

        foreach (var node in jvm)
        {
            foreach (var value in node.Values)
            {
                if (value.Contains("-DignoreList=", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("${version_name}.jar", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when the loader replaces the game jar with its own patched artifact
    /// (<c>net.minecraft:client</c> or a Forge/NeoForge <c>:client</c>/<c>:srg</c> classifier).
    /// </summary>
    public static bool ProvidesClientJarThroughLibraries(ResolvedVersion version)
    {
        if (!version.IsInherited || version.Metadata.Libraries is null) return false;

        foreach (var library in version.Metadata.Libraries)
        {
            if (library.Name is null) continue;

            if (library.Name.StartsWith("net.minecraft:client:", StringComparison.OrdinalIgnoreCase)) return true;

            var coordinate = library.Coordinate;
            if (coordinate is { } c &&
                c.Group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase) &&
                c.Classifier is "client" or "srg")
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildQuickPlayAddress(string? address, int? port)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        return port is > 0 ? $"{address}:{port}" : address;
    }

    private static string NormalizeJavaArch(string? arch) => (arch ?? "").ToLowerInvariant() switch
    {
        "amd64" or "x64" or "x86_64" => "x86_64",
        "arm64" or "aarch64" => "arm64",
        "x86" or "i386" => "x86",
        var other => other,
    };

    private static List<string> ExpandNodes(
        IReadOnlyList<ArgumentNode>? nodes,
        RuleContext context,
        Dictionary<string, string> variables,
        List<string> warnings)
    {
        var result = new List<string>();
        if (nodes is null) return result;

        foreach (var node in nodes)
        {
            if (!RuleEvaluator.IsAllowed(node.Rules, context)) continue;

            foreach (var value in node.Values)
            {
                var expanded = Substitute(value, variables, warnings);
                if (!string.IsNullOrEmpty(expanded)) result.Add(expanded);
            }
        }

        return result;
    }

    /// <summary>Replaces <c>${name}</c> tokens; unknown tokens are reported and blanked.</summary>
    public static string Substitute(string input, IReadOnlyDictionary<string, string> variables, List<string>? warnings = null)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("${", StringComparison.Ordinal)) return input;

        var result = new System.Text.StringBuilder(input.Length + 32);
        var index = 0;

        while (index < input.Length)
        {
            var start = input.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(input, index, input.Length - index);
                break;
            }

            result.Append(input, index, start - index);
            var end = input.IndexOf('}', start);
            if (end < 0)
            {
                result.Append(input, start, input.Length - start);
                break;
            }

            var key = input[(start + 2)..end];
            if (variables.TryGetValue(key, out var value))
            {
                result.Append(value);
            }
            else
            {
                warnings?.Add($"未知的启动变量: ${{{key}}}");
            }

            index = end + 1;
        }

        return result.ToString();
    }

    /// <summary>Splits a command line into arguments, honouring double quotes.</summary>
    public static List<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static void AppLogDebug(string category, string message)
        => Diagnostics.AppLog.Debug("launch", message, ("category", category));
}
