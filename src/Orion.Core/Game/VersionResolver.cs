using Orion.Core.Diagnostics;
using Orion.Core.Json;

namespace Orion.Core.Game;

/// <summary>A version after "inheritsFrom" inheritance has been resolved and merged.</summary>
public sealed class ResolvedVersion
{
    public required string Id { get; init; }

    /// <summary>The merged metadata document that should be used for launching.</summary>
    public required VersionMetadata Metadata { get; init; }

    /// <summary>Version ids from the requested version up to the vanilla root.</summary>
    public required IReadOnlyList<string> Chain { get; init; }

    public required string RootVersionId { get; init; }

    /// <summary>The version whose <c>downloads.client</c> jar is used (the vanilla root for modded versions).</summary>
    public required string ClientJarVersionId { get; init; }

    public required string AssetIndexId { get; init; }

    public bool IsInherited => Chain.Count > 1;

    public int RequiredJavaMajorVersion => Metadata.JavaVersion?.MajorVersion ?? 8;

    public override string ToString() => $"{Id} (root={RootVersionId}, java={RequiredJavaMajorVersion})";
}

/// <summary>
/// Resolves <c>inheritsFrom</c> chains (vanilla &lt;- Forge/NeoForge/Fabric profiles) and
/// merges the documents using the semantics the official launcher expects:
///
/// * scalar fields are overridden by the child;
/// * <c>libraries</c> are merged by Maven coordinate, the child replacing an identical
///   coordinate in place and appending anything new;
/// * <c>arguments.game</c> / <c>arguments.jvm</c> are concatenated root-first so that
///   parent arguments (e.g. <c>-cp</c>) keep working;
/// * <c>minecraftArguments</c> (used by versions older than 1.13) is replaced, not joined.
/// </summary>
public sealed class VersionResolver
{
    private const int MaxChainDepth = 20;

    private readonly VersionRepository _repository;

    public VersionResolver(VersionRepository repository) => _repository = repository;

    public async Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken ct = default)
    {
        var chain = new List<VersionMetadata>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var currentId = versionId;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(currentId))
                throw new InvalidOperationException($"版本继承链存在循环: {currentId}");

            var metadata = await _repository.GetVersionAsync(currentId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(metadata.Id)) metadata.Id = currentId;
            chain.Add(metadata);

            if (chain.Count > MaxChainDepth)
                throw new InvalidOperationException($"版本继承链过深 (>{MaxChainDepth}): {versionId}");

            var parent = metadata.InheritsFrom;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, currentId, StringComparison.OrdinalIgnoreCase))
                break;

            currentId = parent;
        }

        var root = chain[^1];
        var merged = Merge(chain);

        // Which client JAR to put on the classpath:
        // 1. an explicit legacy "jar" field wins (Forge/OptiFine profiles),
        // 2. otherwise the first version in the chain that ships real client download data.
        var explicitJar = chain.Select(m => m.Jar).FirstOrDefault(j => !string.IsNullOrWhiteSpace(j));
        var clientJarVersionId = explicitJar
                                 ?? chain.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Downloads?.Client?.Url))?.Id
                                 ?? root.Id;

        var assetIndexId = merged.AssetIndex?.Id;

        if (string.IsNullOrWhiteSpace(assetIndexId)) assetIndexId = merged.Assets;
        if (string.IsNullOrWhiteSpace(assetIndexId))
        {
            // Very old versions predate the asset index and use the 1.6 "legacy" layout.
            assetIndexId = "legacy";
            merged.Assets ??= "legacy";
        }

        AppLog.Debug("versions", "resolved",
            ("id", versionId),
            ("chain", string.Join(" <- ", chain.Select(c => c.Id))),
            ("libraries", merged.Libraries?.Count ?? 0),
            ("java", merged.JavaVersion?.MajorVersion ?? 8));

        return new ResolvedVersion
        {
            Id = versionId,
            Metadata = merged,
            Chain = chain.Select(c => c.Id).ToArray(),
            RootVersionId = root.Id,
            ClientJarVersionId = clientJarVersionId,
            AssetIndexId = assetIndexId,
        };
    }

    /// <summary>Deduplication key: <c>group:artifact[:classifier]</c>, falling back to the raw name.</summary>
    private static string LibraryKey(LibraryEntry library)
    {
        if (library.Coordinate is { } coordinate)
            return coordinate.Classifier is null
                ? $"{coordinate.Group}:{coordinate.Artifact}"
                : $"{coordinate.Group}:{coordinate.Artifact}:{coordinate.Classifier}";

        return library.Name ?? "";
    }

    /// <summary>
    /// Merges a chain ordered child-first (index 0 is the version being launched).
    /// Exposed for unit tests: the merge is pure and has no I/O.
    /// </summary>
    public static VersionMetadata Merge(IReadOnlyList<VersionMetadata> childFirstChain)
    {
        if (childFirstChain.Count == 0) throw new ArgumentException("The chain must contain at least one version.", nameof(childFirstChain));

        var child = childFirstChain[0];
        var merged = new VersionMetadata { Id = child.Id };

        var libraries = new List<LibraryEntry>();
        var gameArgs = new List<ArgumentNode>();
        var jvmArgs = new List<ArgumentNode>();

        // Walk root -> child so that children override parents.
        for (var i = childFirstChain.Count - 1; i >= 0; i--)
        {
            var layer = childFirstChain[i];

            if (!string.IsNullOrWhiteSpace(layer.MainClass)) merged.MainClass = layer.MainClass;
            if (!string.IsNullOrWhiteSpace(layer.Type)) merged.Type = layer.Type;
            if (layer.AssetIndex is not null) merged.AssetIndex = layer.AssetIndex;
            if (!string.IsNullOrWhiteSpace(layer.Assets)) merged.Assets = layer.Assets;
            if (!string.IsNullOrWhiteSpace(layer.MinecraftArguments)) merged.MinecraftArguments = layer.MinecraftArguments;
            if (layer.JavaVersion is not null) merged.JavaVersion = layer.JavaVersion;
            if (layer.Logging is not null) merged.Logging = layer.Logging;
            if (layer.MinimumLauncherVersion > 0) merged.MinimumLauncherVersion = layer.MinimumLauncherVersion;
            if (layer.ComplianceLevel > 0) merged.ComplianceLevel = layer.ComplianceLevel;
            if (layer.ReleaseTime is not null) merged.ReleaseTime = layer.ReleaseTime;
            if (layer.Time is not null) merged.Time = layer.Time;

            if (layer.Downloads is not null)
            {
                merged.Downloads ??= new VersionDownloads();
                merged.Downloads.Client ??= layer.Downloads.Client;
                merged.Downloads.Server ??= layer.Downloads.Server;
                merged.Downloads.ClientMappings ??= layer.Downloads.ClientMappings;
                merged.Downloads.ServerMappings ??= layer.Downloads.ServerMappings;
            }

            if (layer.Libraries is { Count: > 0 })
            {
                foreach (var library in layer.Libraries)
                {
                    if (string.IsNullOrWhiteSpace(library.Name))
                    {
                        libraries.Add(library);
                        continue;
                    }

                    // Merge by group:artifact (+classifier), not by the full coordinate: a child
                    // that ships org.ow2.asm:asm:9.10.1 must *replace* the parent's
                    // org.ow2.asm:asm:9.3 instead of appending a second copy. Two versions of the
                    // same artifact on one classpath is a classic source of
                    // NoSuchMethodError/NoClassDefFoundError crashes.
                    var key = LibraryKey(library);
                    var index = libraries.FindIndex(existing => LibraryKey(existing) == key);

                    if (index >= 0) libraries[index] = library;
                    else libraries.Add(library);
                }
            }

            if (layer.Arguments?.Game is { Count: > 0 }) gameArgs.AddRange(layer.Arguments.Game);
            if (layer.Arguments?.Jvm is { Count: > 0 }) jvmArgs.AddRange(layer.Arguments.Jvm);
        }

        merged.Libraries = libraries;
        if (gameArgs.Count > 0 || jvmArgs.Count > 0)
        {
            merged.Arguments = new LaunchArguments
            {
                Game = gameArgs,
                Jvm = jvmArgs,
            };
        }

        return merged;
    }
}
