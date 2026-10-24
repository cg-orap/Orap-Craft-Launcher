using Orion.Core.Json;

namespace Orion.Core.Game;

/// <summary>One concrete file a version needs on disk.</summary>
public sealed record LibraryFile
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string AbsolutePath { get; init; }
    public required IReadOnlyList<Uri> Urls { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }

    /// <summary>True when the jar carries platform native libraries.</summary>
    public bool IsNative { get; init; }

    /// <summary>True when the jar belongs on the classpath.</summary>
    public bool OnClasspath { get; init; } = true;

    public IReadOnlyList<string>? ExtractExclude { get; init; }
}

public sealed class LibraryPlan
{
    public List<LibraryFile> Classpath { get; } = [];
    public List<LibraryFile> Natives { get; } = [];

    /// <summary>Every file that must exist on disk.</summary>
    public IEnumerable<LibraryFile> AllFiles => Classpath.Concat(Natives).DistinctBy(f => f.AbsolutePath, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Turns metadata <c>libraries</c> entries into concrete files.
///
/// Three metadata shapes have to be supported:
/// * official Mojang metadata: explicit <c>downloads.artifact</c> (path/sha1/size/url) and,
///   for versions before 1.19, an <c>natives</c> map plus <c>downloads.classifiers</c>;
/// * 1.19+ official metadata: native jars are ordinary library entries whose Maven
///   classifier is <c>natives-&lt;os&gt;[-&lt;arch&gt;]</c>. Their rules only constrain the OS, so
///   the launcher must also match the CPU architecture itself — otherwise a 64-bit JVM
///   would load 32-bit or ARM native libraries from the classpath;
/// * Maven-style metadata (Fabric/Quilt/some Forge libraries) that only carries
///   <c>name</c> plus a repository <c>url</c>; the path is derived from the coordinate.
/// </summary>
public static class LibraryPlanner
{
    public static LibraryPlan Plan(
        GamePaths paths,
        IEnumerable<LibraryEntry>? libraries,
        RuleContext context,
        Func<Uri, IReadOnlyList<Uri>>? mirror = null)
    {
        var plan = new LibraryPlan();
        if (libraries is null) return plan;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            if (library is null) continue;
            if (!RuleEvaluator.IsAllowed(library.Rules, context)) continue;

            var coordinate = library.Coordinate;

            // ---- legacy "natives" map (Minecraft <= 1.18) ------------------------
            var legacyClassifier = LegacyNativeClassifier(library, context.OsName);
            if (legacyClassifier is not null && coordinate is { } legacyCoord)
            {
                DownloadInfo? info = null;
                string? relativeLegacy = null;

                if (library.Downloads?.Classifiers is { Count: > 0 } classifiers && classifiers.TryGetValue(legacyClassifier, out var found))
                {
                    info = found;
                    relativeLegacy = found.Path;
                }

                relativeLegacy ??= legacyCoord.WithClassifier(legacyClassifier).ToRepositoryPath();

                if (seen.Add(relativeLegacy))
                {
                    var urls = ResolveUrls(info?.Url, library.Url, relativeLegacy, mirror);
                    plan.Natives.Add(new LibraryFile
                    {
                        Name = $"{library.Name}:{legacyClassifier}",
                        RelativePath = relativeLegacy,
                        AbsolutePath = ToAbsolute(paths, relativeLegacy),
                        Urls = urls,
                        Sha1 = info?.Sha1 ?? library.Sha1,
                        Sha256 = info?.Sha256,
                        Size = info?.Size ?? library.Size ?? 0,
                        IsNative = true,
                        OnClasspath = false,
                        ExtractExclude = library.Extract?.Exclude,
                    });
                }

                // The legacy natives jar may still declare a regular artifact; fall through
                // so it is handled as well (some libraries do both).
            }

            if (coordinate is null) continue;

            var isArchSpecificNative = coordinate.Value.Classifier is { } classifier
                                       && NativeClassifier.MatchesCurrentArchitecture(classifier, context.OsName, context.OsArch);

            // A classifier that names another OS/architecture is not part of this launch.
            if (coordinate.Value.Classifier is { } otherClassifier
                && NativeClassifier.IsNativeClassifier(otherClassifier)
                && !isArchSpecificNative)
            {
                continue;
            }

            // ---- main artifact -------------------------------------------------
            var relativePath = library.Downloads?.Artifact?.Path;
            if (string.IsNullOrWhiteSpace(relativePath))
                relativePath = coordinate.Value.WithClassifier(coordinate.Value.Classifier).ToRepositoryPath();

            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            if (!seen.Add(relativePath)) continue;

            var artifactUrls = ResolveUrls(library.Downloads?.Artifact?.Url, library.Url, relativePath, mirror);

            var file = new LibraryFile
            {
                Name = library.Name ?? relativePath,
                RelativePath = relativePath,
                AbsolutePath = ToAbsolute(paths, relativePath),
                Urls = artifactUrls,
                Sha1 = library.Downloads?.Artifact?.Sha1 ?? library.Sha1,
                Sha256 = library.Downloads?.Artifact?.Sha256,
                Size = library.Downloads?.Artifact?.Size ?? library.Size ?? 0,
                IsNative = isArchSpecificNative,
                OnClasspath = true,
                ExtractExclude = library.Extract?.Exclude,
            };

            plan.Classpath.Add(file);
            if (isArchSpecificNative) plan.Natives.Add(file);
        }

        return plan;
    }

    /// <summary>The classifier declared by the legacy <c>natives</c> map, with <c>${arch}</c> resolved.</summary>
    public static string? LegacyNativeClassifier(LibraryEntry library, string osName)
    {
        if (library.Natives is null || library.Natives.Count == 0) return null;
        if (!library.Natives.TryGetValue(osName, out var classifier) || string.IsNullOrWhiteSpace(classifier)) return null;

        var bits = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X86 ? "32" : "64";
        return classifier.Replace("${arch}", bits, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToAbsolute(GamePaths paths, string relativePath)
        => Path.Combine(paths.LibrariesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<Uri> ResolveUrls(string? explicitUrl, string? repositoryBase, string? relativePath, Func<Uri, IReadOnlyList<Uri>>? mirror)
    {
        var urls = new List<Uri>();

        void Add(Uri uri)
        {
            var candidates = mirror?.Invoke(uri) ?? [uri];
            foreach (var candidate in candidates)
            {
                if (!urls.Contains(candidate)) urls.Add(candidate);
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitUrl) && Uri.TryCreate(explicitUrl, UriKind.Absolute, out var direct))
        {
            Add(direct);
        }
        else if (!string.IsNullOrWhiteSpace(repositoryBase) && !string.IsNullOrWhiteSpace(relativePath) &&
                 Uri.TryCreate(repositoryBase, UriKind.Absolute, out var baseUri) &&
                 Uri.TryCreate(baseUri, relativePath, out var combined))
        {
            Add(combined);
        }

        return urls;
    }
}

/// <summary>Architecture matching for <c>natives-*</c> Maven classifiers.</summary>
public static class NativeClassifier
{
    private const string Prefix = "natives-";

    public static bool IsNativeClassifier(string classifier)
        => classifier.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="classifier"/> targets the current OS and CPU architecture.
    /// Understands the suffixes Mojang uses: <c>-x86</c>, <c>-x86_64</c>, <c>-arm64</c>,
    /// <c>-aarch_64</c>, <c>-i386</c> and the macOS-only <c>-patch</c> helper jar.
    /// </summary>
    public static bool MatchesCurrentArchitecture(string classifier, string osName, string? osArch)
    {
        if (!IsNativeClassifier(classifier)) return true;

        var suffix = classifier[Prefix.Length..];
        if (!suffix.StartsWith(osName, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = suffix[osName.Length..];
        if (rest.Length == 0) return true;

        var architecture = Normalize(osArch);
        return rest.ToLowerInvariant() switch
        {
            "-patch" => osName.Equals("osx", StringComparison.OrdinalIgnoreCase),
            "-x86" or "-i386" => architecture == "x86",
            "-x86_64" or "-amd64" => architecture == "x86_64",
            "-arm64" or "-aarch_64" => architecture == "arm64",
            _ => false,
        };
    }

    private static string Normalize(string? osArch) => (osArch ?? RuleContext.RuntimeArchitecture()).ToLowerInvariant() switch
    {
        "x64" or "amd64" or "x86_64" => "x86_64",
        "arm64" or "aarch64" or "aarch_64" => "arm64",
        "x86" or "i386" => "x86",
        var other => other,
    };
}
