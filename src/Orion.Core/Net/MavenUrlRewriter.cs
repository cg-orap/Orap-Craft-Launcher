namespace Orion.Core.Net;

/// <summary>
/// Builds alternative download URLs for Maven artifacts.
///
/// Some repositories (notably <c>maven.neoforged.net</c>) are unreachable from some networks,
/// which makes the official Forge/NeoForge installer fail before it can do any work. Because
/// every artifact carries a published SHA-1, fetching the identical bytes from a mirror is a
/// pure transport substitution — it never changes what is installed and never bypasses an
/// ownership or licence check.
///
/// The original URL is always kept (first for official sources) so a mirror is only used when
/// the canonical host fails.
/// </summary>
public static class MavenUrlRewriter
{
    private const string CreeperHost = "https://maven.creeperhost.net";
    private const string BmclMaven = "https://bmclapi2.bangbang93.com/maven";

    /// <summary>True when this URL belongs to a known Maven façade that has mirrors.</summary>
    public static bool IsMavenHost(string host) => host.ToLowerInvariant() is
        "maven.neoforged.net" or "maven.minecraftforge.net" or "libraries.minecraft.net" or "maven.fabricmc.net";

    /// <summary>
    /// Candidate URLs for one artifact, canonical URL first, mirrors afterwards.
    /// </summary>
    public static IReadOnlyList<Uri> Candidates(Uri original)
    {
        var result = new List<Uri> { original };
        var host = original.Host.ToLowerInvariant();
        var path = original.AbsolutePath;

        void Add(string baseUrl, string relativePath)
        {
            if (Uri.TryCreate(baseUrl.TrimEnd('/') + relativePath, UriKind.Absolute, out var candidate) &&
                !result.Contains(candidate))
            {
                result.Add(candidate);
            }
        }

        switch (host)
        {
            case "maven.neoforged.net":
            {
                var relative = StripPrefixes(path, "/releases", "/extra");
                Add(CreeperHost, relative);
                Add(BmclMaven, relative);
                break;
            }

            case "maven.minecraftforge.net":
            case "libraries.minecraft.net":
            case "maven.fabricmc.net":
            {
                Add(BmclMaven, path);
                if (host is "libraries.minecraft.net") Add(CreeperHost, path);
                break;
            }
        }

        return result;
    }

    private static string StripPrefixes(string path, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path[prefix.Length..];
        }

        return path;
    }
}
