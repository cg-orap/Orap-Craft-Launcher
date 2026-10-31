namespace Orion.Core.Game;

/// <summary>
/// Resolves every on-disk location the launcher uses.
///
/// Layout (the "shared" model, similar in spirit to Prism's instances but with our own
/// structure):
/// <code>
/// &lt;root&gt;/
///   assets/            shared game assets (objects/, indexes/, log_configs/)
///   libraries/         shared java libraries
///   versions/          shared version metadata and client jars
///   runtime/           java runtimes managed by the launcher (optional)
///   instances/&lt;id&gt;/    one isolated instance
///     instance.json
///     minecraft/       the game directory of that instance (saves, mods, config ...)
///   cache/             http metadata cache
///   logs/              launcher logs
///   accounts.json      encrypted account store
/// </code>
/// Sharing assets/libraries/versions keeps disk usage low while every instance still
/// owns its save data, so two instances can never overwrite each other's worlds.
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root directory is required.", nameof(root));
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string VersionsDir => Path.Combine(Root, "versions");
    public string LibrariesDir => Path.Combine(Root, "libraries");
    public string AssetsDir => Path.Combine(Root, "assets");
    public string AssetObjectsDir => Path.Combine(AssetsDir, "objects");
    public string AssetIndexesDir => Path.Combine(AssetsDir, "indexes");
    public string AssetVirtualDir => Path.Combine(AssetsDir, "virtual");
    public string LogConfigsDir => Path.Combine(AssetsDir, "log_configs");
    public string RuntimeDir => Path.Combine(Root, "runtime");
    public string InstancesDir => Path.Combine(Root, "instances");
    public string CacheDir => Path.Combine(Root, "cache");

    /// <summary>默认下载目录（用户没自定义时用这里）。</summary>
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string LogsDir => Path.Combine(Root, "logs");
    public string AccountsFile => Path.Combine(Root, "accounts.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ManifestCacheFile => Path.Combine(CacheDir, "version_manifest_v2.json");

    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);
    public string VersionJsonPath(string versionId) => Path.Combine(VersionsDir, versionId, versionId + ".json");
    public string VersionJarPath(string versionId) => Path.Combine(VersionsDir, versionId, versionId + ".jar");
    public string VersionNativesDir(string versionId) => Path.Combine(VersionsDir, versionId, "natives");

    public string InstanceDir(string instanceId) => Path.Combine(InstancesDir, instanceId);

    public void EnsureBaseDirectories()
    {
        foreach (var dir in new[] { Root, VersionsDir, LibrariesDir, AssetsDir, AssetObjectsDir, AssetIndexesDir, LogConfigsDir, RuntimeDir, InstancesDir, CacheDir, LogsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Turns a possibly relative path into an absolute one below <see cref="Root"/>.</summary>
    public string Resolve(string path) => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Root, path));
}
