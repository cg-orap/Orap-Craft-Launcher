using System.Text.Json;
using Orion.Core.Diagnostics;
using Orion.Core.Json;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Game;

/// <summary>
/// Reads Minecraft version metadata: the official version manifest and the per-version
/// JSON documents, with a disk cache so the launcher keeps working when offline.
/// </summary>
public sealed class VersionRepository
{
    public static readonly Uri OfficialManifestUrl = new("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");

    private static readonly TimeSpan ManifestTtl = TimeSpan.FromMinutes(15);

    private readonly GamePaths _paths;
    private readonly HttpClient _http;
    private readonly MirrorResolver _mirrors;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private readonly object _cacheGate = new();

    private VersionManifest? _manifest;
    private DateTimeOffset _manifestFetched;

    public VersionRepository(GamePaths paths, HttpClient http, MirrorResolver mirrors)
    {
        _paths = paths;
        _http = http;
        _mirrors = mirrors;
    }

    /// <summary>Additional version roots, used for instances imported from other launchers.</summary>
    public List<string> ExtraVersionRoots { get; } = [];

    public IReadOnlyList<Uri> ManifestUrls => _mirrors.Resolve(OfficialManifestUrl);

    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        lock (_cacheGate)
        {
            if (!forceRefresh && _manifest is not null && DateTimeOffset.UtcNow - _manifestFetched < ManifestTtl)
                return _manifest;
        }

        await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_cacheGate)
            {
                if (!forceRefresh && _manifest is not null && DateTimeOffset.UtcNow - _manifestFetched < ManifestTtl)
                    return _manifest;
            }

            // A fresh on-disk cache avoids a network round trip on every start.
            if (!forceRefresh && File.Exists(_paths.ManifestCacheFile))
            {
                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_paths.ManifestCacheFile);
                if (age < ManifestTtl)
                {
                    var cached = await TryReadManifestFileAsync(_paths.ManifestCacheFile, ct).ConfigureAwait(false);
                    if (cached is not null)
                    {
                        Remember(cached);
                        return cached;
                    }
                }
            }

            try
            {
                var manifest = await HttpUtil.GetJsonAsync<VersionManifest>(_http, ManifestUrls, ct).ConfigureAwait(false);
                if (manifest.Versions.Count == 0) throw new JsonException("版本清单为空。");

                Directory.CreateDirectory(_paths.CacheDir);
                await JsonUtil.WriteFileAsync(_paths.ManifestCacheFile, manifest, ct).ConfigureAwait(false);
                Remember(manifest);
                AppLog.Info("manifest", "fetched", ("versions", manifest.Versions.Count), ("release", manifest.Latest?.Release));
                return manifest;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                // Offline fallback: any cached manifest is better than none.
                var fallback = await TryReadManifestFileAsync(_paths.ManifestCacheFile, ct).ConfigureAwait(false);
                if (fallback is not null)
                {
                    AppLog.Warn("manifest", "using-stale-cache", ("reason", ex.Message));
                    Remember(fallback);
                    return fallback;
                }

                throw;
            }
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    /// <summary>Locates a local version JSON, searching the shared root and any imported roots.</summary>
    public string? FindLocalJsonPath(string versionId)
    {
        var direct = _paths.VersionJsonPath(versionId);
        if (File.Exists(direct)) return direct;

        foreach (var root in ExtraVersionRoots)
        {
            var candidate = Path.Combine(root, versionId, versionId + ".json");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public Task<VersionMetadata?> TryReadLocalAsync(string versionId, CancellationToken ct = default)
    {
        var path = FindLocalJsonPath(versionId);
        return path is null ? Task.FromResult<VersionMetadata?>(null) : ReadFileAsync(path, ct);
    }

    /// <summary>
    /// Loads a version's metadata, preferring a local copy and downloading it from the
    /// manifest when it is missing.
    /// </summary>
    public async Task<VersionMetadata> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("Version id is required.", nameof(versionId));

        var local = await TryReadLocalAsync(versionId, ct).ConfigureAwait(false);
        if (local is not null) return local;

        var manifest = await GetManifestAsync(false, ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new VersionNotFoundException($"版本清单中不存在 “{versionId}”。");

        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url))
            throw new VersionNotFoundException($"版本 “{versionId}” 的元数据地址无效。");

        var json = await HttpUtil.GetStringAsync(_http, _mirrors.Resolve(url), ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(entry.Sha1))
        {
            var actual = FileHashing.HashString(json, HashAlgorithmKind.Sha1);
            if (!string.Equals(actual, entry.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"版本 “{versionId}” 的元数据校验失败。");
        }

        var metadata = JsonSerializer.Deserialize<VersionMetadata>(json, JsonUtil.Options)
                       ?? throw new InvalidDataException($"版本 “{versionId}” 的元数据无法解析。");
        if (string.IsNullOrEmpty(metadata.Id)) metadata.Id = versionId;

        var target = _paths.VersionJsonPath(versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Keep whatever the server sent byte-for-byte; it is the signed source of truth.
        await File.WriteAllTextAsync(target, json, ct).ConfigureAwait(false);
        AppLog.Info("versions", "downloaded-json", ("id", versionId), ("bytes", json.Length));

        return metadata;
    }

    /// <summary>Writes a version JSON document (used by loader profiles and imported instances).</summary>
    public async Task SaveVersionAsync(VersionMetadata metadata, string? versionRoot = null, CancellationToken ct = default)
    {
        var root = versionRoot ?? _paths.VersionsDir;
        var path = Path.Combine(root, metadata.Id, metadata.Id + ".json");
        await JsonUtil.WriteFileAsync(path, metadata, ct).ConfigureAwait(false);
        AppLog.Info("versions", "json-saved", ("id", metadata.Id), ("path", path));
    }

    private void Remember(VersionManifest manifest)
    {
        lock (_cacheGate)
        {
            _manifest = manifest;
            _manifestFetched = DateTimeOffset.UtcNow;
        }
    }

    private static async Task<VersionManifest?> TryReadManifestFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return await JsonUtil.DeserializeFileAsync<VersionManifest>(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn("manifest", "cache-unreadable", ("path", path), ("error", ex.Message));
            return null;
        }
    }

    private static async Task<VersionMetadata?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await JsonUtil.DeserializeFileAsync<VersionMetadata>(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn("versions", "json-unreadable", ("path", path), ("error", ex.Message));
            return null;
        }
    }
}

public sealed class VersionNotFoundException(string message) : Exception(message);
