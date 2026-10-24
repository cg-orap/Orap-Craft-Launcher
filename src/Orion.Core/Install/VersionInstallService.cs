using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Json;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Install;

public enum InstallPhase
{
    Preparing,
    Metadata,
    Client,
    Libraries,
    Assets,
    Natives,
    Completed,
    Failed,
}

public sealed record InstallProgress(InstallPhase Phase, string Message, double Fraction, DownloadProgress? Download)
{
    public double Percent => Math.Clamp(Fraction, 0, 1) * 100;
}

public sealed class InstallResult
{
    public required string VersionId { get; init; }
    public required bool Success { get; init; }
    public int FilesDownloaded { get; init; }
    public long BytesDownloaded { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<DownloadFailure> Failures { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
    public bool Cancelled { get; init; }
}

public sealed class InstallOptions
{
    public bool InstallAssets { get; set; } = true;
    public bool ExtractNatives { get; set; } = true;

    /// <summary>Hash every existing file instead of trusting a matching size.</summary>
    public bool DeepVerify { get; set; }
}

/// <summary>
/// Installs a Minecraft version (vanilla or a merged mod loader profile) into the shared
/// root: client jar, libraries, native libraries, asset index, asset objects and the
/// log4j configuration.
///
/// The installer is idempotent: re-running it only downloads what is missing or corrupt,
/// which is what makes "install cancelled, then resumed" work.
/// </summary>
public sealed class VersionInstallService
{
    private readonly GamePaths _paths;
    private readonly VersionRepository _repository;
    private readonly VersionResolver _resolver;
    private readonly DownloadEngine _engine;
    private readonly MirrorResolver _mirrors;

    public VersionInstallService(
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

    public async Task<InstallResult> InstallAsync(
        string versionId,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new InstallOptions();
        var started = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var failures = new List<DownloadFailure>();
        var filesDownloaded = 0;
        long bytesDownloaded = 0;

        try
        {
            progress?.Report(new InstallProgress(InstallPhase.Preparing, "正在解析版本元数据…", 0.01, null));
            _paths.EnsureBaseDirectories();

            var resolved = await _resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
            AppLog.Info("install", "start",
                ("version", versionId),
                ("root", resolved.RootVersionId),
                ("java", resolved.RequiredJavaMajorVersion));

            // ---------- phase 1: metadata, client jar, libraries, asset index ----------
            progress?.Report(new InstallProgress(InstallPhase.Metadata, "正在准备下载清单…", 0.03, null));

            var phase1 = BuildMetadataRequests(resolved, warnings);
            var phase1Progress = new PhaseProgress(progress, InstallPhase.Libraries, "正在下载游戏文件…", 0.05, 0.50);

            var phase1Report = await _engine.DownloadAsync(phase1, phase1Progress, ct).ConfigureAwait(false);
            filesDownloaded += phase1Report.CompletedFiles;
            bytesDownloaded += phase1Report.DownloadedBytes;
            failures.AddRange(phase1Report.Failures);

            if (!phase1Report.Success)
            {
                return Fail(versionId, "游戏文件下载失败，请检查网络后重试（已下载的文件会被复用）。", failures, warnings, started);
            }

            // ---------- phase 2: asset objects ----------
            if (options.InstallAssets)
            {
                var assetRequests = await BuildAssetRequestsAsync(resolved, warnings, ct).ConfigureAwait(false);
                if (assetRequests.Count > 0)
                {
                    progress?.Report(new InstallProgress(InstallPhase.Assets, $"正在下载资源文件 ({assetRequests.Count} 个)…", 0.55, null));
                    var assetProgress = new PhaseProgress(progress, InstallPhase.Assets, "正在下载资源文件…", 0.55, 0.40);

                    var assetReport = await _engine.DownloadAsync(assetRequests, assetProgress, ct).ConfigureAwait(false);
                    filesDownloaded += assetReport.CompletedFiles;
                    bytesDownloaded += assetReport.DownloadedBytes;
                    failures.AddRange(assetReport.Failures);

                    if (!assetReport.Success)
                    {
                        return Fail(versionId, "资源文件下载失败，请重试。", failures, warnings, started);
                    }
                }

                await MaterializeVirtualAssetsAsync(resolved, warnings, ct).ConfigureAwait(false);
            }

            // ---------- phase 3: natives ----------
            if (options.ExtractNatives)
            {
                var plan = LibraryPlanner.Plan(_paths, resolved.Metadata.Libraries, RuleContext.ForCurrentMachine(), _mirrors.Resolve);
                var nativesDirectory = _paths.VersionNativesDir(versionId);

                if (plan.Natives.Count > 0)
                {
                    progress?.Report(new InstallProgress(InstallPhase.Natives, "正在解压本地库…", 0.96, null));
                    var extract = await NativeExtractor.ExtractAsync(plan.Natives, nativesDirectory, ct).ConfigureAwait(false);
                    foreach (var error in extract.Errors) warnings.Add(error);
                    AppLog.Info("install", "natives-extracted", ("files", extract.FilesWritten), ("skipped", extract.FilesSkipped));
                }
            }

            progress?.Report(new InstallProgress(InstallPhase.Completed, "安装完成", 1, null));

            var result = new InstallResult
            {
                VersionId = versionId,
                Success = true,
                FilesDownloaded = filesDownloaded,
                BytesDownloaded = bytesDownloaded,
                Duration = DateTimeOffset.UtcNow - started,
                Failures = failures,
                Warnings = warnings,
            };

            AppLog.Info("install", "finished",
                ("version", versionId),
                ("files", filesDownloaded),
                ("bytes", bytesDownloaded),
                ("seconds", Math.Round(result.Duration.TotalSeconds, 1)));

            return result;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("install", "cancelled", ("version", versionId));
            progress?.Report(new InstallProgress(InstallPhase.Failed, "安装已取消（可稍后继续）", 0, null));
            return new InstallResult
            {
                VersionId = versionId,
                Success = false,
                FilesDownloaded = filesDownloaded,
                BytesDownloaded = bytesDownloaded,
                Duration = DateTimeOffset.UtcNow - started,
                Failures = failures,
                Warnings = warnings,
                Error = "安装已取消。",
                Cancelled = true,
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("install", "failed", ex, ("version", versionId));
            progress?.Report(new InstallProgress(InstallPhase.Failed, "安装失败: " + ex.Message, 0, null));
            return Fail(versionId, ex.Message, failures, warnings, started);
        }
    }

    private List<DownloadRequest> BuildMetadataRequests(ResolvedVersion resolved, List<string> warnings)
    {
        var requests = new List<DownloadRequest>();
        var context = RuleContext.ForCurrentMachine();

        // 1. Client jar. For an inherited version the jar belongs to the vanilla ancestor.
        var client = resolved.Metadata.Downloads?.Client;
        if (client is not null && !string.IsNullOrWhiteSpace(client.Url) && Uri.TryCreate(client.Url, UriKind.Absolute, out var clientUri))
        {
            var jarVersion = resolved.ClientJarVersionId;
            requests.Add(new DownloadRequest
            {
                TargetPath = _paths.VersionJarPath(jarVersion),
                Urls = _mirrors.Resolve(clientUri),
                Sha1 = client.Sha1,
                Size = client.Size,
                Label = $"客户端 {jarVersion}.jar",
                Verify = DownloadVerifyPolicy.Auto,
            });
        }
        else
        {
            warnings.Add("该版本未声明客户端 JAR（可能依赖父版本）。");
        }

        // 2. Libraries (classpath + natives).
        var plan = LibraryPlanner.Plan(_paths, resolved.Metadata.Libraries, context, _mirrors.Resolve);

        foreach (var file in plan.AllFiles)
        {
            if (file.Urls.Count == 0)
            {
                warnings.Add($"无法确定下载地址: {file.Name}");
                continue;
            }

            requests.Add(new DownloadRequest
            {
                TargetPath = file.AbsolutePath,
                Urls = file.Urls,
                Sha1 = file.Sha1,
                Sha256 = file.Sha256,
                Size = file.Size,
                Label = Path.GetFileName(file.AbsolutePath),
            });
        }

        // 3. Asset index document.
        var index = resolved.Metadata.AssetIndex;
        if (index is not null && !string.IsNullOrWhiteSpace(index.Url) && Uri.TryCreate(index.Url, UriKind.Absolute, out var indexPath))
        {
            requests.Add(new DownloadRequest
            {
                TargetPath = Path.Combine(_paths.AssetIndexesDir, resolved.AssetIndexId + ".json"),
                Urls = _mirrors.Resolve(indexPath),
                Sha1 = index.Sha1,
                Size = index.Size,
                Label = $"资源索引 {resolved.AssetIndexId}",
            });
        }

        // 4. Log4j / logging configuration.
        var logging = resolved.Metadata.Logging?.Client?.File;
        if (logging is not null && !string.IsNullOrWhiteSpace(logging.Url) && Uri.TryCreate(logging.Url, UriKind.Absolute, out var loggingUri))
        {
            requests.Add(new DownloadRequest
            {
                TargetPath = Path.Combine(_paths.LogConfigsDir, logging.Id),
                Urls = _mirrors.Resolve(loggingUri),
                Sha1 = logging.Sha1,
                Size = logging.Size,
                Label = logging.Id,
            });
        }

        return requests;
    }

    private async Task<List<DownloadRequest>> BuildAssetRequestsAsync(ResolvedVersion resolved, List<string> warnings, CancellationToken ct)
    {
        var requests = new List<DownloadRequest>();
        var indexFile = Path.Combine(_paths.AssetIndexesDir, resolved.AssetIndexId + ".json");

        if (!File.Exists(indexFile))
        {
            warnings.Add($"资源索引 {resolved.AssetIndexId} 不存在，跳过资源下载。");
            return requests;
        }

        AssetIndex? index;
        try
        {
            index = await JsonUtil.DeserializeFileAsync<AssetIndex>(indexFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            warnings.Add($"无法解析资源索引 {indexFile}: {ex.Message}");
            return requests;
        }

        if (index is null || index.Objects.Count == 0) return requests;

        var baseUri = new Uri("https://resources.download.minecraft.net/");
        requests.Capacity = index.Objects.Count;

        foreach (var (name, obj) in index.Objects)
        {
            if (string.IsNullOrEmpty(obj.Hash)) continue;

            var group = obj.Hash.Length >= 2 ? obj.Hash[..2] : "00";
            requests.Add(new DownloadRequest
            {
                TargetPath = Path.Combine(_paths.AssetObjectsDir, group, obj.Hash),
                Urls = _mirrors.Resolve(new Uri(baseUri, $"{group}/{obj.Hash}")),
                Sha1 = obj.Hash,
                Size = obj.Size,
                Label = name,
            });
        }

        return requests;
    }

    /// <summary>
    /// Old versions read assets from a flat directory (<c>assets/virtual/&lt;index&gt;</c> or the
    /// game's <c>resources</c> folder) rather than from the hashed object store.
    /// </summary>
    private async Task MaterializeVirtualAssetsAsync(ResolvedVersion resolved, List<string> warnings, CancellationToken ct)
    {
        var indexFile = Path.Combine(_paths.AssetIndexesDir, resolved.AssetIndexId + ".json");
        if (!File.Exists(indexFile)) return;

        AssetIndex? index;
        try
        {
            index = await JsonUtil.DeserializeFileAsync<AssetIndex>(indexFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return;
        }

        if (index is null || (!index.Virtual && !index.MapToResources)) return;

        var target = index.Virtual
            ? Path.Combine(_paths.AssetVirtualDir, resolved.AssetIndexId)
            : Path.Combine(_paths.Root, "resources");

        var copied = 0;
        foreach (var (name, obj) in index.Objects)
        {
            ct.ThrowIfCancellationRequested();

            var source = Path.Combine(_paths.AssetObjectsDir, obj.StoragePath);
            if (!File.Exists(source)) continue;

            var destination = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            if (File.Exists(destination) && new FileInfo(destination).Length == obj.Size) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                copied++;
            }
            catch (IOException ex)
            {
                warnings.Add($"复制资源失败 {name}: {ex.Message}");
            }
        }

        if (copied > 0)
        {
            AppLog.Info("install", "virtual-assets-materialized", ("count", copied), ("target", target));
        }
    }

    private static InstallResult Fail(
        string versionId,
        string error,
        List<DownloadFailure> failures,
        List<string> warnings,
        DateTimeOffset started) => new()
        {
            VersionId = versionId,
            Success = false,
            FilesDownloaded = 0,
            Duration = DateTimeOffset.UtcNow - started,
            Failures = failures,
            Warnings = warnings,
            Error = error,
        };

    /// <summary>Maps a sub-phase download progress onto the overall installation progress bar.</summary>
    private sealed class PhaseProgress(
        IProgress<InstallProgress>? outer,
        InstallPhase phase,
        string message,
        double baseFraction,
        double span) : IProgress<DownloadProgress>
    {
        private readonly string _message = message;

        public void Report(DownloadProgress value)
        {
            if (outer is null) return;

            var fraction = baseFraction + (Math.Clamp(value.Fraction, 0, 1) * span);
            var text = $"{_message} {value.CompletedFiles}/{value.TotalFiles} · {SpeedMeter.Humanize(value.BytesPerSecond)}";
            outer.Report(new InstallProgress(phase, text, fraction, value));
        }
    }
}
