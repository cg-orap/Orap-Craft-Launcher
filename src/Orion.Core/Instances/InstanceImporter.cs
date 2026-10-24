using System.Text.Json;
using Orion.Core.Diagnostics;

namespace Orion.Core.Instances;

/// <summary>
/// Imports instances that already exist on disk.
///
/// Supported layouts:
/// * a plain <c>.minecraft</c> directory (vanilla, PCL2 and HMCL all use it) — every
///   directory under <c>versions/</c> that contains a version JSON becomes an instance;
/// * a Prism Launcher / MultiMC instance directory (<c>instance.cfg</c> + <c>mmc-pack.json</c>).
///
/// Nothing is written into the imported directory. Imported instances are registered as
/// <see cref="Instance.IsExternal"/> so an uninstall can never delete someone else's files.
/// </summary>
public sealed class InstanceImporter
{
    private readonly InstanceManager _instances;
    private readonly Game.GamePaths _paths;

    public InstanceImporter(InstanceManager instances, Game.GamePaths paths)
    {
        _instances = instances;
        _paths = paths;
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken ct = default)
    {
        var result = new ImportResult();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            result.Warnings.Add($"目录不存在: {path}");
            return result;
        }

        var full = Path.GetFullPath(path);

        try
        {
            if (IsPrismInstance(full))
            {
                result.DetectedSource = "Prism Launcher / MultiMC";
                await ImportPrismAsync(full, result, ct).ConfigureAwait(false);
                return result;
            }

            var minecraftRoot = ResolveMinecraftRoot(full);
            if (minecraftRoot is not null)
            {
                result.DetectedSource = DetectMinecraftRootFlavour(minecraftRoot);
                await ImportMinecraftRootAsync(minecraftRoot, result, ct).ConfigureAwait(false);
                return result;
            }

            result.Warnings.Add("无法识别的目录结构：既没有 versions/ 版本目录，也没有 Prism 的 instance.cfg。");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("import", "failed", ex, ("path", full));
            result.Warnings.Add("导入失败: " + ex.Message);
            return result;
        }
    }

    /// <summary>Returns the importable version roots so the caller can register them for launching.</summary>
    public IReadOnlyList<string> VersionRootsOf(IEnumerable<Instance> instances)
        => instances.Where(i => i.IsExternal)
            .Select(i => Path.Combine(i.GameDirectory, "versions"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsPrismInstance(string directory)
        => File.Exists(Path.Combine(directory, "instance.cfg"))
           && (File.Exists(Path.Combine(directory, "mmc-pack.json")) || Directory.Exists(Path.Combine(directory, ".minecraft")));

    private static string? ResolveMinecraftRoot(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, "versions"))) return directory;

        var nested = Path.Combine(directory, ".minecraft");
        if (Directory.Exists(Path.Combine(nested, "versions"))) return nested;

        // The user picked a single version folder; go up one level if that looks right.
        var parent = Directory.GetParent(directory);
        if (parent is not null && Directory.Exists(Path.Combine(parent.FullName, "versions"))) return parent.FullName;

        return null;
    }

    private static string DetectMinecraftRootFlavour(string root)
    {
        if (Directory.Exists(Path.Combine(root, "PCL"))) return "PCL2 (.minecraft)";
        if (File.Exists(Path.Combine(root, "hmcl.json")) || File.Exists(Path.Combine(root, "config.json")))
            return "HMCL (.minecraft)";
        return "vanilla .minecraft";
    }

    private async Task ImportMinecraftRootAsync(string root, ImportResult result, CancellationToken ct)
    {
        var versionsDirectory = Path.Combine(root, "versions");
        var existing = _instances.LoadAll().Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var versionDirectory in Directory.EnumerateDirectories(versionsDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var versionId = Path.GetFileName(versionDirectory);
            var jsonPath = Path.Combine(versionDirectory, versionId + ".json");

            if (!File.Exists(jsonPath))
            {
                var anyJson = Directory.EnumerateFiles(versionDirectory, "*.json").FirstOrDefault();
                if (anyJson is null) continue;
                jsonPath = anyJson;
                versionId = Path.GetFileNameWithoutExtension(anyJson);
            }

            Json.VersionMetadata? metadata;
            try
            {
                metadata = await Util.JsonUtil.DeserializeFileAsync<Json.VersionMetadata>(jsonPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                result.Warnings.Add($"跳过无法解析的版本 {versionId}: {ex.Message}");
                continue;
            }

            if (metadata is null || (string.IsNullOrWhiteSpace(metadata.MainClass) && string.IsNullOrWhiteSpace(metadata.InheritsFrom)))
            {
                result.Warnings.Add($"跳过 {versionId}: 不是可启动的版本。");
                continue;
            }

            var id = Instance.MakeId(versionId, versionId);
            if (!existing.Add(id)) continue;

            var (loader, loaderVersion, baseVersion) = DetectLoader(metadata, versionId);
            var settings = ReadPerVersionSettings(versionDirectory);

            var instance = new Instance
            {
                Id = id,
                Name = settings.Name ?? versionId,
                VersionId = versionId,
                GameVersion = baseVersion,
                Loader = loader,
                LoaderVersion = loaderVersion,
                GameDirectory = root,
                IsExternal = true,
                MemoryMb = settings.MemoryMb,
                JavaPath = settings.JavaPath,
                JvmArguments = settings.JvmArguments,
                CreatedAt = DateTimeOffset.Now,
            };

            await _instances.SaveAsync(instance, ct).ConfigureAwait(false);
            result.Instances.Add(instance);
            AppLog.Info("import", "version-imported", ("version", versionId), ("loader", loader.ToString()), ("external", true));
        }

        if (result.Instances.Count == 0)
        {
            result.Warnings.Add("该 .minecraft 目录中没有找到可导入的版本。");
        }
    }

    private async Task ImportPrismAsync(string directory, ImportResult result, CancellationToken ct)
    {
        var gameDirectory = Path.Combine(directory, ".minecraft");
        if (!Directory.Exists(gameDirectory))
        {
            result.Warnings.Add("Prism 实例缺少 .minecraft 目录。");
            return;
        }

        var packPath = Path.Combine(directory, "mmc-pack.json");
        string minecraftVersion = "";
        string? loaderVersion = null;
        var loader = LoaderKind.Vanilla;

        if (File.Exists(packPath))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(packPath, ct).ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
                {
                    foreach (var component in components.EnumerateArray())
                    {
                        var uid = component.TryGetProperty("uid", out var u) ? u.GetString() : null;
                        var version = component.TryGetProperty("version", out var v) ? v.GetString() : null;
                        if (uid is null) continue;

                        switch (uid)
                        {
                            case "net.minecraft":
                                minecraftVersion = version ?? "";
                                break;
                            case "net.neoforged":
                                loader = LoaderKind.NeoForge;
                                loaderVersion = version;
                                break;
                            case "net.minecraftforge":
                                loader = LoaderKind.Forge;
                                loaderVersion = version;
                                break;
                            case "org.quiltmc.quilt-loader":
                                loader = LoaderKind.Quilt;
                                loaderVersion = version;
                                break;
                            case "net.fabricmc.fabric-loader":
                                loader = LoaderKind.Fabric;
                                loaderVersion = version;
                                break;
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                result.Warnings.Add("mmc-pack.json 解析失败: " + ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            result.Warnings.Add("无法从 mmc-pack.json 读取 Minecraft 版本。");
            return;
        }

        // Prism keeps the loader profile inside its own versions directory.
        var versionsRoot = Path.Combine(gameDirectory, "versions");
        var versionId = Directory.Exists(versionsRoot)
            ? Directory.EnumerateDirectories(versionsRoot)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null &&
                                        (name.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase)))
              ?? minecraftVersion
            : minecraftVersion;

        var cfg = IniFile.Load(Path.Combine(directory, "instance.cfg"));
        var name = cfg.Get("name") ?? Path.GetFileName(directory);

        var instance = new Instance
        {
            Id = Instance.MakeId(name, versionId),
            Name = name,
            VersionId = versionId,
            GameVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            GameDirectory = gameDirectory,
            IsExternal = true,
            MemoryMb = int.TryParse(cfg.Get("MaxMemAlloc"), out var maxMem) ? maxMem : null,
            MinMemoryMb = int.TryParse(cfg.Get("MinMemAlloc"), out var minMem) ? minMem : null,
            JavaPath = cfg.Get("JavaPath"),
            JvmArguments = cfg.Get("JvmArgs"),
            IconKey = cfg.Get("iconKey"),
            CreatedAt = DateTimeOffset.Now,
        };

        await _instances.SaveAsync(instance, ct).ConfigureAwait(false);
        result.Instances.Add(instance);
        AppLog.Info("import", "prism-instance", ("name", name), ("version", versionId));
    }

    /// <summary>Reads per-version settings written by PCL2 (<c>Setup.ini</c>) when present.</summary>
    private static (string? Name, int? MemoryMb, string? JavaPath, string? JvmArguments) ReadPerVersionSettings(string versionDirectory)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(versionDirectory, "PCL", "Setup.ini"),
                     Path.Combine(versionDirectory, "Setup.ini"),
                     Path.Combine(versionDirectory, "instance.cfg"),
                 })
        {
            if (!File.Exists(candidate)) continue;

            var ini = IniFile.Load(candidate);
            int? memory = null;

            foreach (var key in new[] { "Ram", "MaxMemAlloc", "Memory" })
            {
                if (int.TryParse(ini.Get(key), out var value) && value > 0)
                {
                    memory = value;
                    break;
                }
            }

            var java = ini.Get("Java") ?? ini.Get("JavaPath");
            var jvm = ini.Get("Jvm") ?? ini.Get("JvmArgs");
            var name = ini.Get("Name");

            if (memory is not null || java is not null || jvm is not null)
            {
                return (name, memory, java, jvm);
            }
        }

        return (null, null, null, null);
    }

    private static (LoaderKind Loader, string? LoaderVersion, string GameVersion) DetectLoader(Json.VersionMetadata metadata, string versionId)
    {
        var id = versionId.ToLowerInvariant();

        LoaderKind Kind()
        {
            if (id.Contains("neoforge")) return LoaderKind.NeoForge;
            if (id.Contains("forge")) return LoaderKind.Forge;
            if (id.Contains("quilt")) return LoaderKind.Quilt;
            if (id.Contains("fabric")) return LoaderKind.Fabric;
            return LoaderKind.Vanilla;
        }

        var loader = Kind();
        var baseVersion = metadata.InheritsFrom;

        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            // Older launchers store the vanilla version in the version id, e.g.
            // "1.20.1-forge-47.2.0" or "fabric-loader-0.15.0-1.20.1".
            var match = System.Text.RegularExpressions.Regex.Match(versionId, @"\d+\.\d+(\.\d+)?");
            baseVersion = match.Success ? match.Value : versionId;
        }

        string? loaderVersion = null;
        if (loader == LoaderKind.NeoForge)
        {
            var match = System.Text.RegularExpressions.Regex.Match(id, @"neoforge-([\d.]+)");
            if (match.Success) loaderVersion = match.Groups[1].Value;
        }
        else if (loader == LoaderKind.Forge)
        {
            var match = System.Text.RegularExpressions.Regex.Match(versionId, @"-(\d+\.\d+(?:\.\d+)?-\d+\.\d+\.\d+[\w.-]*)");
            if (match.Success) loaderVersion = match.Groups[1].Value;
            else
            {
                var simple = System.Text.RegularExpressions.Regex.Match(id, @"forge-([\d.]+)");
                if (simple.Success) loaderVersion = simple.Groups[1].Value;
            }
        }
        else if (loader is LoaderKind.Fabric or LoaderKind.Quilt)
        {
            var match = System.Text.RegularExpressions.Regex.Match(id, @"loader-([\d.]+)");
            if (match.Success) loaderVersion = match.Groups[1].Value;
        }

        return (loader, loaderVersion, baseVersion!);
    }
}

/// <summary>Minimal INI reader (Prism <c>instance.cfg</c>, PCL2 <c>Setup.ini</c>).</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;

                var index = line.IndexOf('=');
                if (index <= 0) continue;

                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();
                ini._values[key] = value;
            }
        }
        catch (IOException)
        {
        }

        return ini;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
