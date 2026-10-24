using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Install;
using Orion.Core.Net;

namespace Orion.Core.Loaders;

/// <summary>
/// Creates the loader provider for each supported mod loader and exposes a single
/// <see cref="InstallAsync"/> entry point used by the GUI and the CLI.
/// </summary>
public sealed class LoaderService
{
    private readonly Dictionary<LoaderKind, ILoaderProvider> _providers;

    public LoaderService(
        HttpClient http,
        GamePaths paths,
        VersionRepository repository,
        VersionInstallService installService,
        MirrorResolver mirrors,
        DownloadEngine downloader)
    {
        _providers = new Dictionary<LoaderKind, ILoaderProvider>
        {
            [LoaderKind.Fabric] = new FabricProvider(http, paths, repository, installService, mirrors),
            [LoaderKind.Quilt] = new QuiltProvider(http, paths, repository, installService, mirrors),
            [LoaderKind.NeoForge] = new NeoForgeProvider(http, paths, repository, installService, mirrors, downloader),
            [LoaderKind.Forge] = new ForgeProvider(http, paths, repository, installService, mirrors, downloader),
        };
    }

    public static IReadOnlyList<LoaderKind> SupportedLoaders { get; } =
        [LoaderKind.Vanilla, LoaderKind.NeoForge, LoaderKind.Fabric, LoaderKind.Forge, LoaderKind.Quilt];

    public ILoaderProvider? Get(LoaderKind kind) => _providers.GetValueOrDefault(kind);

    public Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(LoaderKind kind, string minecraftVersion, CancellationToken ct = default)
    {
        var provider = Get(kind);
        return provider is null
            ? Task.FromResult<IReadOnlyList<LoaderVersionInfo>>([])
            : provider.GetVersionsAsync(minecraftVersion, ct);
    }

    public Task<LoaderInstallResult> InstallAsync(LoaderKind kind, LoaderInstallRequest request, CancellationToken ct = default)
    {
        var provider = Get(kind);
        return provider is null
            ? Task.FromResult(new LoaderInstallResult { Success = false, VersionId = "", Error = $"不支持 {kind}。" })
            : provider.InstallAsync(request, ct);
    }

    /// <summary>Picks the newest stable loader version that matches a Minecraft version.</summary>
    public async Task<string?> ResolveLatestLoaderVersionAsync(LoaderKind kind, string minecraftVersion, CancellationToken ct = default)
    {
        var versions = await GetVersionsAsync(kind, minecraftVersion, ct).ConfigureAwait(false);
        if (versions.Count == 0) return null;

        return versions.FirstOrDefault(v => v.Stable)?.Version ?? versions[0].Version;
    }

    public static string DisplayName(LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "原版",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        _ => kind.ToString(),
    };

    /// <summary>True when the loader id declares support for the given Minecraft version.</summary>
    public static bool IsCompatible(LoaderKind kind, string minecraftVersion, string? loaderVersion)
    {
        if (kind == LoaderKind.Vanilla) return true;

        return kind switch
        {
            // NeoForge >= 1.20.2 encodes the Minecraft version in its own version number.
            LoaderKind.NeoForge => loaderVersion is not null &&
                                   (loaderVersion.StartsWith(string.Join('.', minecraftVersion.Split('.').Skip(1).Take(2)) + ".", StringComparison.Ordinal)
                                    || loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.Ordinal)),
            LoaderKind.Forge => loaderVersion is null || loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.Ordinal) || loaderVersion.Contains(minecraftVersion, StringComparison.Ordinal),
            _ => true,
        };
    }
}
