namespace Orion.Core.Net;

public enum DownloadSource
{
    /// <summary>Mojang / Microsoft official services only.</summary>
    Official = 0,

    /// <summary>BMCLAPI mirror first, official services as fallback.</summary>
    BmclApi = 1,

    /// <summary>A user supplied mirror base URL first, official services as fallback.</summary>
    Custom = 2,
}

/// <summary>
/// Maps official download URLs onto an optional mirror.
///
/// Mirrors are strictly an alternative <em>transport</em> for the same content: everything
/// is still verified against the SHA-1 digests published by Mojang / the loader project,
/// and no mirror is used to bypass authentication, ownership checks or rate limits.
/// </summary>
public sealed class MirrorResolver
{
    public const string BmclApiBase = "https://bmclapi2.bangbang93.com";

    private readonly NetworkOptions _options;

    public MirrorResolver(NetworkOptions options) => _options = options;

    public bool IsMirrorEnabled => _options.Source != DownloadSource.Official;

    /// <summary>
    /// Returns the ordered candidate URLs for an official URL. The first entry is tried
    /// first; the remaining entries are used for retries and fallbacks.
    /// </summary>
    public IReadOnlyList<Uri> Resolve(Uri official)
    {
        if (!IsMirrorEnabled) return [official];

        var mirror = TryMap(official);
        if (mirror is null) return [official];

        return [mirror, official];
    }

    public IReadOnlyList<Uri> Resolve(params Uri[] official)
    {
        var list = new List<Uri>(official.Length + 1);
        foreach (var uri in official)
        {
            foreach (var candidate in Resolve(uri))
            {
                if (!list.Contains(candidate)) list.Add(candidate);
            }
        }

        return list;
    }

    private Uri? TryMap(Uri official)
    {
        var baseUrl = _options.Source switch
        {
            DownloadSource.BmclApi => BmclApiBase,
            DownloadSource.Custom => _options.CustomMirrorBase,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return null;

        var host = official.Host.ToLowerInvariant();
        var path = official.AbsolutePath;

        // Asset objects: <mirror>/assets/<ab>/<hash>
        if (host is "resources.download.minecraft.net")
            return Combine(baseUri, "/assets" + path);

        // Maven repositories. NeoForge's canonical repository is /releases plus /extra;
        // mirrors expose both below a single /maven prefix.
        if (host is "libraries.minecraft.net")
            return Combine(baseUri, "/maven" + path);

        if (host is "maven.fabricmc.net" or "maven.minecraftforge.net" or "maven.quiltmc.org")
            return Combine(baseUri, "/maven" + path);

        if (host is "maven.neoforged.net")
        {
            var trimmed = path;
            foreach (var prefix in new[] { "/releases", "/extra" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed[prefix.Length..];
                    break;
                }
            }

            return Combine(baseUri, "/maven" + trimmed);
        }

        // Version manifest, version metadata and piston-data objects keep their path.
        if (host is "launchermeta.mojang.com" or "piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com")
            return Combine(baseUri, path);

        if (host is "meta.fabricmc.net")
            return Combine(baseUri, "/fabric-meta" + path);

        if (host is "meta.quiltmc.org")
            return Combine(baseUri, "/quilt-meta" + path);

        if (host is "files.minecraftforge.net")
            return Combine(baseUri, "/forge" + path);

        return null;
    }

    private static Uri? Combine(Uri baseUri, string path)
    {
        var prefix = baseUri.AbsoluteUri.TrimEnd('/');
        return Uri.TryCreate(prefix + path, UriKind.Absolute, out var result) ? result : null;
    }
}
