using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Json;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Install;

public sealed record VerifyProgress(double Fraction, string Message)
{
    public double Percent => Math.Clamp(Fraction, 0, 1) * 100;
}

public sealed class VerifyResult
{
    public int CheckedFiles { get; init; }
    public int AssetCount { get; init; }
    public List<string> Missing { get; } = [];
    public List<string> Corrupt { get; } = [];
    public TimeSpan Duration { get; init; }
    public bool IsHealthy => Missing.Count == 0 && Corrupt.Count == 0;
}

public sealed class RepairResult
{
    public bool Success { get; init; }
    public int FilesDownloaded { get; init; }
    public long BytesDownloaded { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<DownloadFailure> Failures { get; init; } = [];
}

/// <summary>
/// Verifies that every file a version needs is present and matches the digest published in its
/// metadata, then re-downloads whatever is missing or corrupt.
///
/// Asset objects are thousands of tiny files, so they are checked by size first and hashed only
/// when the size differs (or when a deep check is requested).
/// </summary>
public sealed class IntegrityService
{
    private readonly GamePaths _paths;
    private readonly VersionRepository _repository;
    private readonly VersionResolver _resolver;
    private readonly DownloadEngine _engine;
    private readonly MirrorResolver _mirrors;

    public IntegrityService(
        GamePaths paths,
        VersionRepository repository,
        VersionResolver resolver,
        DownloadEngine engine,
        MirrorResolver mirrors)
    {
        _paths = paths;
        _repository = repository;
        _resolver = resolver;
        _engine = engine;
        _mirrors = mirrors;
    }

    public async Task<VerifyResult> VerifyAsync(
        string versionId,
        Func<Uri, IReadOnlyList<Uri>>? mirror = null,
        IProgress<VerifyProgress>? progress = null,
        bool deep = false,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var resolved = await _resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);

        var missing = new List<string>();
        var corrupt = new List<string>();
        var checkedFiles = 0;

        var context = RuleContext.ForCurrentMachine();
        var plan = LibraryPlanner.Plan(_paths, resolved.Metadata.Libraries, context, mirror ?? _mirrors.Resolve);

        var expected = new List<(string Path, string? Sha1, long Size)>();

        var client = resolved.Metadata.Downloads?.Client;
        if (client is not null)
            expected.Add((_paths.VersionJarPath(resolved.ClientJarVersionId), client.Sha1, client.Size));

        foreach (var library in plan.AllFiles)
            expected.Add((library.AbsolutePath, library.Sha1, library.Size));

        var index = resolved.Metadata.AssetIndex;
        if (index is not null)
            expected.Add((Path.Combine(_paths.AssetIndexesDir, resolved.AssetIndexId + ".json"), index.Sha1, index.Size));

        var logging = resolved.Metadata.Logging?.Client?.File;
        if (logging is not null)
            expected.Add((Path.Combine(_paths.LogConfigsDir, logging.Id), logging.Sha1, logging.Size));

        for (var i = 0; i < expected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (path, sha1, size) = expected[i];
            checkedFiles++;

            progress?.Report(new VerifyProgress(i / (double)Math.Max(1, expected.Count) * 0.7, $"校验 {Path.GetFileName(path)}"));

            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            var length = new FileInfo(path).Length;

            if (size > 0 && length != size)
            {
                corrupt.Add(path);
                continue;
            }

            if ((deep || size <= 0) && !await FileHashing.VerifyAsync(path, sha1, HashAlgorithmKind.Sha1, ct).ConfigureAwait(false))
            {
                corrupt.Add(path);
            }
        }

        var assetCount = await VerifyAssetsAsync(resolved, deep, progress, missing, corrupt, ct).ConfigureAwait(false);
        checkedFiles += assetCount;

        progress?.Report(new VerifyProgress(1, "校验完成"));

        return new VerifyResult
        {
            CheckedFiles = checkedFiles,
            AssetCount = assetCount,
            Duration = DateTimeOffset.UtcNow - started,
        }.WithLists(missing, corrupt);
    }

    private async Task<int> VerifyAssetsAsync(
        ResolvedVersion resolved,
        bool deep,
        IProgress<VerifyProgress>? progress,
        List<string> missing,
        List<string> corrupt,
        CancellationToken ct)
    {
        var indexFile = Path.Combine(_paths.AssetIndexesDir, resolved.AssetIndexId + ".json");
        if (!File.Exists(indexFile)) return 0;

        AssetIndex? assetIndex;
        try
        {
            assetIndex = await JsonUtil.DeserializeFileAsync<AssetIndex>(indexFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            AppLog.Warn("integrity", "asset-index-unreadable", ("error", ex.Message));
            return 0;
        }

        if (assetIndex is null || assetIndex.Objects.Count == 0) return 0;

        var done = 0;
        foreach (var asset in assetIndex.Objects.Values)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (done % 250 == 0)
            {
                progress?.Report(new VerifyProgress(
                    0.7 + (done / (double)assetIndex.Objects.Count * 0.3),
                    $"校验资源 {done}/{assetIndex.Objects.Count}"));
            }

            var path = Path.Combine(_paths.AssetObjectsDir, asset.StoragePath);

            if (!File.Exists(path))
            {
                missing.Add(path);
                continue;
            }

            if (new FileInfo(path).Length != asset.Size)
            {
                corrupt.Add(path);
            }
            else if (deep && !await FileHashing.VerifyAsync(path, asset.Hash, HashAlgorithmKind.Sha1, ct).ConfigureAwait(false))
            {
                corrupt.Add(path);
            }
        }

        return assetIndex.Objects.Count;
    }

    /// <summary>Re-downloads everything <see cref="VerifyAsync"/> flagged, using the normal installer.</summary>
    public async Task<RepairResult> RepairAsync(
        string versionId,
        Func<Uri, IReadOnlyList<Uri>>? mirror = null,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            progress?.Report(new VerifyProgress(0, "正在检查文件…"));
            var verification = await VerifyAsync(versionId, mirror, null, deep: false, ct).ConfigureAwait(false);

            // Corrupt files must be removed so the installer cannot mistake them for complete.
            foreach (var path in verification.Corrupt)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("integrity", "delete-corrupt-failed", ("path", path), ("error", ex.Message));
                }
            }

            progress?.Report(new VerifyProgress(0.1, "正在重新下载…"));

            var installer = new VersionInstallService(_paths, _repository, _resolver, _engine, _mirrors);
            var installProgress = new Progress<InstallProgress>(p => progress?.Report(new VerifyProgress(p.Fraction, p.Message)));

            var install = await installer.InstallAsync(versionId, new InstallOptions(), installProgress, ct).ConfigureAwait(false);

            return new RepairResult
            {
                Success = install.Success,
                FilesDownloaded = install.FilesDownloaded,
                BytesDownloaded = install.BytesDownloaded,
                Error = install.Error,
                Failures = install.Failures,
            };
        }
        catch (OperationCanceledException)
        {
            return new RepairResult { Success = false, Error = "修复已取消。" };
        }
        catch (Exception ex)
        {
            AppLog.Error("integrity", "repair-failed", ex, ("version", versionId));
            return new RepairResult { Success = false, Error = ex.Message };
        }
    }
}

internal static class VerifyResultExtensions
{
    /// <summary>Copies the collected file lists into the immutable result.</summary>
    public static VerifyResult WithLists(this VerifyResult result, IEnumerable<string> missing, IEnumerable<string> corrupt)
    {
        result.Missing.AddRange(missing);
        result.Corrupt.AddRange(corrupt);
        return result;
    }
}
