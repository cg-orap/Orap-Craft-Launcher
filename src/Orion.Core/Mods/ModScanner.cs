using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orion.Core.Diagnostics;
using Orion.Core.Instances;

namespace Orion.Core.Mods;

public enum ModSource
{
    Local = 0,
    Modrinth = 1,
    CurseForge = 2,
    Pack = 3,
}

public sealed class ModInfo
{
    public required string FilePath { get; set; }
    public required string FileName { get; set; }

    public string? ModId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Authors { get; set; }

    /// <summary>Detected loader family: fabric, quilt, forge, neoforge or unknown.</summary>
    public string? Loader { get; set; }

    public List<string> Dependencies { get; } = [];
    public bool Enabled { get; set; } = true;
    public long Size { get; set; }
    public ModSource Source { get; set; } = ModSource.Local;

    /// <summary>Project id (Modrinth slug) when known, used for update checks.</summary>
    public string? ProjectId { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name!;

    public override string ToString() => $"{DisplayName} {Version} [{Loader}]";
}

/// <summary>
/// Reads mod metadata out of jar files.
///
/// Supported descriptors (in priority order):
/// <c>fabric.mod.json</c>, <c>quilt.mod.json</c>, <c>META-INF/neoforge.mods.toml</c>,
/// <c>META-INF/mods.toml</c> and the legacy <c>mcmod.info</c>.
/// </summary>
public static class ModScanner
{
    private static readonly string[] EnabledExtensions = [".jar"];
    private static readonly string[] DisabledSuffixes = [".jar.disabled", ".jar.off", ".disabled"];

    public static Task<IReadOnlyList<ModInfo>> ScanAsync(string gameDirectory, CancellationToken ct = default)
        => Task.Run(() => Scan(gameDirectory, ct), ct);

    public static IReadOnlyList<ModInfo> Scan(string gameDirectory, CancellationToken ct = default)
    {
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        var result = new List<ModInfo>();

        if (!Directory.Exists(modsDirectory)) return result;

        foreach (var path in Directory.EnumerateFiles(modsDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            var enabled = EnabledExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

            if (!enabled && !DisabledSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var mod = new ModInfo
            {
                FilePath = path,
                FileName = fileName,
                Enabled = enabled,
            };

            try
            {
                mod.Size = new FileInfo(path).Length;
                ReadDescriptor(mod);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                mod.Description = "无法读取 Mod 元数据: " + ex.Message;
                AppLog.Debug("mods", "descriptor-failed", ("file", fileName), ("error", ex.Message));
            }

            if (string.IsNullOrWhiteSpace(mod.Name)) mod.Name = StripModSuffix(fileName);
            result.Add(mod);
        }

        return result.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static void ReadDescriptor(ModInfo mod)
    {
        using var archive = ZipFile.OpenRead(mod.FilePath);

        var fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using var stream = fabric.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            mod.Loader = "fabric";
            mod.ModId = GetString(root, "id");
            mod.Name = GetString(root, "name");
            mod.Version = GetString(root, "version");
            mod.Description = GetString(root, "description");

            if (root.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                mod.Authors = string.Join(", ", authors.EnumerateArray()
                    .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : GetString(a, "name"))
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in depends.EnumerateObject())
                {
                    if (dependency.Name is "minecraft" or "java") continue;
                    mod.Dependencies.Add(dependency.Name);
                }
            }

            return;
        }

        var quilt = archive.GetEntry("quilt.mod.json");
        if (quilt is not null)
        {
            using var stream = quilt.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var loader = root.TryGetProperty("quilt_loader", out var q) ? q : root;

            mod.Loader = "quilt";
            mod.ModId = GetString(loader, "id");
            mod.Version = GetString(loader, "version");

            if (loader.TryGetProperty("metadata", out var metadata))
            {
                mod.Name = GetString(metadata, "name");
                mod.Description = GetString(metadata, "description");
            }

            return;
        }

        var neoforgeToml = archive.GetEntry("META-INF/neoforge.mods.toml");
        var forgeToml = archive.GetEntry("META-INF/mods.toml");

        var toml = neoforgeToml ?? forgeToml;
        if (toml is not null)
        {
            using var stream = toml.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            mod.Loader = neoforgeToml is not null ? "neoforge" : "forge";
            ParseToml(text, mod);
            return;
        }

        var legacy = archive.GetEntry("mcmod.info");
        if (legacy is not null)
        {
            using var stream = legacy.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            // mcmod.info is either an array or an object with a "modList" array.
            JsonElement entry = default;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) entry = root[0];
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modList", out var list) && list.GetArrayLength() > 0) entry = list[0];

            if (entry.ValueKind == JsonValueKind.Object)
            {
                mod.Loader = "forge";
                mod.ModId = GetString(entry, "modid");
                mod.Name = GetString(entry, "name");
                mod.Version = GetString(entry, "version");
                mod.Description = GetString(entry, "description");
                mod.Authors = GetString(entry, "authorList");
            }
        }
    }

    private static readonly Regex ModIdRegex = new(@"^\s*modId\s*=\s*""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex VersionRegex = new(@"^\s*version\s*=\s*""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex DisplayNameRegex = new(@"^\s*displayName\s*=\s*""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex DescriptionRegex = new(@"^\s*description\s*=\s*""([^""]*)""", RegexOptions.Multiline);
    private static readonly Regex AuthorsRegex = new(@"^\s*authors\s*=\s*""([^""]+)""", RegexOptions.Multiline);
    private static readonly Regex DependencyRegex = new(@"^\s*modId\s*=\s*""([^""]+)""", RegexOptions.Multiline);

    /// <summary>
    /// Just enough TOML to read a Forge/NeoForge <c>mods.toml</c>: the first <c>[[mods]]</c>
    /// block plus its dependency mod ids.
    /// </summary>
    public static void ParseToml(string text, ModInfo mod)
    {
        var modsIndex = text.IndexOf("[[mods]]", StringComparison.OrdinalIgnoreCase);
        var header = modsIndex >= 0 ? text[(modsIndex + "[[mods]]".Length)..] : text;
        var dependenciesIndex = header.IndexOf("[[dependencies", StringComparison.OrdinalIgnoreCase);
        var ownBlock = dependenciesIndex >= 0 ? header[..dependenciesIndex] : header;

        mod.ModId = Match(ModIdRegex, ownBlock);
        mod.Version = Match(VersionRegex, ownBlock);
        mod.Name = Match(DisplayNameRegex, ownBlock);
        mod.Description = Match(DescriptionRegex, ownBlock);
        mod.Authors = Match(AuthorsRegex, ownBlock);

        if (dependenciesIndex >= 0)
        {
            foreach (Match match in DependencyRegex.Matches(header[dependenciesIndex..]))
            {
                var id = match.Groups[1].Value;
                if (id is "minecraft" or "neoforge" or "forge" or "java") continue;
                if (!mod.Dependencies.Contains(id)) mod.Dependencies.Add(id);
            }
        }
    }

    private static string? Match(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string StripModSuffix(string fileName)
    {
        foreach (var suffix in new[] { ".jar.disabled", ".jar.off", ".disabled", ".jar" })
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        }

        return fileName;
    }

    /// <summary>Enables or disables a mod by renaming it, the convention every launcher uses.</summary>
    public static string SetEnabled(ModInfo mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod.FilePath;

        var directory = Path.GetDirectoryName(mod.FilePath)!;
        string newPath;

        if (enabled)
        {
            var baseName = StripModSuffix(mod.FileName);
            newPath = Path.Combine(directory, baseName + ".jar");
        }
        else
        {
            newPath = mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? mod.FilePath
                : mod.FilePath + ".disabled";
        }

        if (string.Equals(newPath, mod.FilePath, StringComparison.OrdinalIgnoreCase)) return mod.FilePath;

        File.Move(mod.FilePath, newPath, overwrite: false);
        mod.FilePath = newPath;
        mod.FileName = Path.GetFileName(newPath);
        mod.Enabled = enabled;
        AppLog.Info("mods", enabled ? "mod-enabled" : "mod-disabled", ("file", mod.FileName));
        return newPath;
    }

    /// <summary>Checks the mod loader family against the instance loader.</summary>
    public static bool IsCompatibleWith(ModInfo mod, LoaderKind loader)
    {
        if (string.IsNullOrWhiteSpace(mod.Loader)) return true;

        return loader switch
        {
            LoaderKind.Vanilla => false,
            LoaderKind.Fabric => mod.Loader is "fabric",
            LoaderKind.Quilt => mod.Loader is "quilt" or "fabric",
            LoaderKind.Forge => mod.Loader is "forge",
            LoaderKind.NeoForge => mod.Loader is "neoforge" or "forge",
            _ => true,
        };
    }
}
