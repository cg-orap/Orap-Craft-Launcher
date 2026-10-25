using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Orion.Core.Diagnostics;
using Orion.Core.Util;

using Orion.Core.Game;

namespace Orion.Core.Instances;

public enum LoaderKind
{
    Vanilla = 0,
    Forge = 1,
    NeoForge = 2,
    Fabric = 3,
    Quilt = 4,
}

/// <summary>
/// One isolated game instance.
///
/// Instances always own their own game directory (saves, mods, options.txt) while sharing
/// the downloaded assets/libraries/versions. Imported instances may point at a directory
/// that belongs to another launcher; those are marked <see cref="IsExternal"/> and the
/// launcher never deletes or rewrites their files.
/// </summary>
public sealed class Instance
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("loader")] public LoaderKind Loader { get; set; }
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }

    /// <summary>Absolute path to the game directory (saves/, mods/, options.txt live here).</summary>
    [JsonPropertyName("gameDirectory")] public string GameDirectory { get; set; } = "";

    [JsonPropertyName("isExternal")] public bool IsExternal { get; set; }

    [JsonPropertyName("memoryMb")] public int? MemoryMb { get; set; }
    [JsonPropertyName("minMemoryMb")] public int? MinMemoryMb { get; set; }
    [JsonPropertyName("jvmArguments")] public string? JvmArguments { get; set; }
    [JsonPropertyName("gameArguments")] public string? GameArguments { get; set; }
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; } = 854;
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; } = 480;
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    [JsonPropertyName("iconKey")] public string? IconKey { get; set; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("lastPlayedAt")] public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore] public string? SourcePath { get; set; }

    public string DisplayLoader => Loader switch
    {
        LoaderKind.Vanilla => "原版",
        LoaderKind.Forge => $"Forge {LoaderVersion}".Trim(),
        LoaderKind.NeoForge => $"NeoForge {LoaderVersion}".Trim(),
        LoaderKind.Fabric => $"Fabric {LoaderVersion}".Trim(),
        LoaderKind.Quilt => $"Quilt {LoaderVersion}".Trim(),
        _ => Loader.ToString(),
    };

    public string Subtitle => $"{GameVersion} · {DisplayLoader}";

    public int EffectiveMemory => MemoryMb ?? 4096;

    public static string MakeId(string name, string versionId)
    {
        var baseText = string.IsNullOrWhiteSpace(name) ? versionId : name;
        var sanitized = Regex.Replace(baseText, @"[^\w\u4e00-\u9fa5.-]+", "-").Trim('-');
        if (sanitized.Length == 0) sanitized = "instance";
        if (sanitized.Length > 40) sanitized = sanitized[..40];

        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{sanitized}-{suffix}".ToLowerInvariant();
    }
}

public sealed class ImportResult
{
    public List<Instance> Instances { get; } = [];
    public List<string> Warnings { get; } = [];
    public string DetectedSource { get; set; } = "unknown";
    public bool Success => Instances.Count > 0;
}

/// <summary>Creates, loads, updates and safely removes instances.</summary>
public sealed class InstanceManager
{
    private readonly Game.GamePaths _paths;

    public InstanceManager(Game.GamePaths paths) => _paths = paths;

    public string InstanceDirectory(string id) => _paths.InstanceDir(id);
    public string SettingsPath(string id) => Path.Combine(_paths.InstanceDir(id), "instance.json");

    public IReadOnlyList<Instance> LoadAll()
    {
        var result = new List<Instance>();
        if (!Directory.Exists(_paths.InstancesDir)) return result;

        foreach (var directory in Directory.EnumerateDirectories(_paths.InstancesDir))
        {
            var file = Path.Combine(directory, "instance.json");
            if (!File.Exists(file)) continue;

            try
            {
                var json = File.ReadAllText(file);
                var instance = JsonUtil.Deserialize<Instance>(json);
                if (instance is null || string.IsNullOrWhiteSpace(instance.Id)) continue;
                if (string.IsNullOrWhiteSpace(instance.GameDirectory))
                    instance.GameDirectory = Path.Combine(directory, "minecraft");
                instance.SourcePath = directory;
                result.Add(instance);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
            {
                AppLog.Warn("instances", "unreadable", ("path", file), ("error", ex.Message));
            }
        }

        return result.OrderByDescending(i => i.LastPlayedAt ?? i.CreatedAt).ToArray();
    }

    public async Task SaveAsync(Instance instance, CancellationToken ct = default)
    {
        var directory = InstanceDirectory(instance.Id);
        Directory.CreateDirectory(directory);
        instance.SourcePath = directory;
        await JsonUtil.WriteFileAsync(SettingsPath(instance.Id), instance, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a new isolated instance with its own game directory.</summary>
    public async Task<Instance> CreateAsync(
        string name,
        string versionId,
        string gameVersion,
        LoaderKind loader = LoaderKind.Vanilla,
        string? loaderVersion = null,
        CancellationToken ct = default)
    {
        var id = Instance.MakeId(name, versionId);
        var directory = InstanceDirectory(id);
        var gameDirectory = Path.Combine(directory, "minecraft");

        var instance = new Instance
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? versionId : name,
            VersionId = versionId,
            GameVersion = gameVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            GameDirectory = gameDirectory,
            CreatedAt = DateTimeOffset.Now,
        };

        Directory.CreateDirectory(gameDirectory);

        // A fresh, isolated game directory: the folders Minecraft and modders expect.
        foreach (var sub in new[] { "mods", "saves", "resourcepacks", "shaderpacks", "config", "logs", "screenshots" })
        {
            Directory.CreateDirectory(Path.Combine(gameDirectory, sub));
        }

        await SaveAsync(instance, ct).ConfigureAwait(false);
        AppLog.Info("instances", "created", ("id", id), ("version", versionId), ("loader", loader.ToString()));
        return instance;
    }

    /// <summary>
    /// Removes an instance. The instance folder is moved to <c>&lt;root&gt;/trash</c> instead of
    /// being deleted, so a mis-click can never destroy a world.
    /// </summary>
    public string? Remove(Instance instance, out string? trashPath)
    {
        trashPath = null;
        var directory = instance.SourcePath ?? InstanceDirectory(instance.Id);

        if (instance.IsExternal)
        {
            // Never touch files that belong to another launcher.
            if (File.Exists(SettingsPath(instance.Id))) File.Delete(SettingsPath(instance.Id));
            var externalDir = InstanceDirectory(instance.Id);
            if (Directory.Exists(externalDir)) Directory.Delete(externalDir, recursive: true);
            AppLog.Info("instances", "unregistered-external", ("id", instance.Id));
            return null;
        }

        if (!Directory.Exists(directory))
        {
            instance.SourcePath = null;
            return null;
        }

        try
        {
            // Send the whole folder to the Windows Recycle Bin so a mis-click stays reversible from
            // Explorer, which is what users expect from 移除.
            Game.RecycleBin.RemoveDirectory(directory);
            trashPath = directory;   // reported to the UI: this folder now sits in the Recycle Bin
            AppLog.Info("instances", "recycled", ("id", instance.Id), ("path", directory));
            return directory;
        }
        catch (IOException ex)
        {
            AppLog.Warn("instances", "recycle-failed", ("id", instance.Id), ("error", ex.Message));
            throw new IOException("无法移除版本（可能仍被占用）。请关闭游戏后重试，文件不会被删除。", ex);
        }
    }

    /// <summary>Directories that hold the isolated save data, used when warning the user.</summary>
    public static bool HasUserData(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory)) return false;

        foreach (var sub in new[] { "saves", "mods", "resourcepacks", "shaderpacks", "config" })
        {
            var path = Path.Combine(gameDirectory, sub);
            if (!Directory.Exists(path)) continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(path).Any()) return true;
            }
            catch (IOException)
            {
            }
        }

        return false;
    }
}
