using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Install;
using Orion.Core.Java;
using Orion.Core.Net;

namespace Orion.Core.Loaders;

public sealed record LoaderVersionInfo(string Version, bool Stable, DateTimeOffset? Released = null)
{
    public override string ToString() => Stable ? Version + " (稳定)" : Version;
}

public sealed class LoaderInstallRequest
{
    public required string MinecraftVersion { get; init; }
    public required string LoaderVersion { get; init; }

    /// <summary>Java used to run a loader installer (Forge / NeoForge).</summary>
    public JavaRuntime? Java { get; init; }

    /// <summary>Optional mirror base forwarded to the loader installer's <c>--mirror</c>.</summary>
    public string? InstallerMirror { get; init; }

    public IProgress<string>? Output { get; init; }
}

public sealed class LoaderInstallResult
{
    public required bool Success { get; init; }
    public required string VersionId { get; init; }
    public string? Error { get; init; }
    public List<string> Warnings { get; } = [];
    public List<string> Output { get; } = [];
}

public interface ILoaderProvider
{
    LoaderKind Kind { get; }

    /// <summary>Loaders that can be installed for this Minecraft version.</summary>
    Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default);

    /// <summary>
    /// Installs the loader and returns the version id that must be launched.
    /// A loader is only reported as installed after its version JSON exists and declares a
    /// main class — never merely because a jar was downloaded.
    /// </summary>
    Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken ct = default);
}

/// <summary>
/// Shared plumbing for Fabric and Quilt, whose installation model is identical: the loader
/// publishes a complete "profile" version JSON that inherits from the vanilla version, so a
/// correct install is "write the profile + download its libraries".
/// </summary>
public abstract class MetaProfileProvider : ILoaderProvider
{
    protected readonly HttpClient Http;
    protected readonly GamePaths Paths;
    protected readonly VersionRepository Repository;
    protected readonly VersionInstallService InstallService;
    protected readonly MirrorResolver Mirrors;

    protected MetaProfileProvider(
        HttpClient http,
        GamePaths paths,
        VersionRepository repository,
        VersionInstallService installService,
        MirrorResolver mirrors)
    {
        Http = http;
        Paths = paths;
        Repository = repository;
        InstallService = installService;
        Mirrors = mirrors;
    }

    public abstract LoaderKind Kind { get; }

    protected abstract string MetaBase { get; }

    protected abstract string DisplayName { get; }

    public async Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var url = new Uri($"{MetaBase}/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}");
        var candidates = Mirrors.Resolve(url);
        var json = await HttpUtil.GetStringAsync(Http, candidates, ct).ConfigureAwait(false);

        var result = new List<LoaderVersionInfo>();
        using var document = System.Text.Json.JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            return result;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader)) continue;

            var version = loader.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(version)) continue;

            var stable = loader.TryGetProperty("stable", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
            result.Add(new LoaderVersionInfo(version!, stable));
        }

        return result;
    }

    /// <summary>
    /// Loader metadata candidates, BMCLAPI mirror first.
    ///
    /// The official Fabric/Quilt meta endpoints answer in seconds at best and hang for some version
    /// combinations on CN networks (26.3/0.19.5 timed out for 20s+ while the mirror replied in under
    /// a second), and the download-source setting is only read when the resolver is created, so a
    /// user who switches to a mirror mid-session can still end up with a single official URL.
    /// </summary>
    private IReadOnlyList<Uri> MetadataCandidates(Uri official)
    {
        var resolved = Mirrors.Resolve(official);
        if (resolved.Count > 1) return resolved;   // the resolver already offers the mirror

        var prefix = MetaBase.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) ? "quilt-meta" : "fabric-meta";
        var mirrored = new Uri($"https://bmclapi2.bangbang93.com/{prefix}{official.AbsolutePath}");
        return [mirrored, .. resolved];
    }

    public async Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken ct = default)
    {
        var url = new Uri($"{MetaBase}/v2/versions/loader/{Uri.EscapeDataString(request.MinecraftVersion)}/{Uri.EscapeDataString(request.LoaderVersion)}/profile/json");
        request.Output?.Report($"正在获取 {DisplayName} 配置…");

        var json = await HttpUtil.GetStringAsync(Http, MetadataCandidates(url), ct).ConfigureAwait(false);

        Json.VersionMetadata? profile;
        try
        {
            profile = Util.JsonUtil.Deserialize<Json.VersionMetadata>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new LoaderInstallResult
            {
                Success = false,
                VersionId = "",
                Error = $"{DisplayName} 的配置无法解析: {ex.Message}",
            };
        }

        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
        {
            return new LoaderInstallResult { Success = false, VersionId = "", Error = $"{DisplayName} 返回的配置无效。" };
        }

        profile.InheritsFrom ??= request.MinecraftVersion;

        // Persist the profile exactly as published (it is the loader's own metadata).
        var path = Paths.VersionJsonPath(profile.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        request.Output?.Report($"已写入版本配置 {profile.Id}");

        // Download the libraries it references (and the vanilla parent).
        // Pass the caller's progress sink through: without it the download phase reported nothing,
        // so a slow or stalled library/asset download looked exactly like a frozen UI.
        request.Output?.Report($"正在下载 {profile.Id} 的依赖与资源…");
        // Adapt the text sink to the install engine's structured progress so the UI and log see the
        // real download phase (files counted) instead of an apparently frozen task.
        var installProgress = request.Output is null
            ? null
            : new Progress<Install.InstallProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message)) request.Output.Report(p.Message);
            });

        var install = await InstallService.InstallAsync(profile.Id, new Install.InstallOptions(), installProgress, ct).ConfigureAwait(false);
        if (!install.Success)
        {
            return new LoaderInstallResult
            {
                Success = false,
                VersionId = profile.Id,
                Error = install.Error ?? "依赖下载失败。",
            };
        }

        var result = new LoaderInstallResult { Success = true, VersionId = profile.Id };
        result.Warnings.AddRange(install.Warnings);
        AppLog.Info("loaders", $"{Kind}-installed", ("mc", request.MinecraftVersion), ("loader", request.LoaderVersion), ("version", profile.Id));
        return result;
    }
}

public sealed class FabricProvider(HttpClient http, GamePaths paths, VersionRepository repository, VersionInstallService installService, MirrorResolver mirrors)
    : MetaProfileProvider(http, paths, repository, installService, mirrors)
{
    public override LoaderKind Kind => LoaderKind.Fabric;
    protected override string MetaBase => "https://meta.fabricmc.net";
    protected override string DisplayName => "Fabric";
}

public sealed class QuiltProvider(HttpClient http, GamePaths paths, VersionRepository repository, VersionInstallService installService, MirrorResolver mirrors)
    : MetaProfileProvider(http, paths, repository, installService, mirrors)
{
    public override LoaderKind Kind => LoaderKind.Quilt;
    protected override string MetaBase => "https://meta.quiltmc.org";
    protected override string DisplayName => "Quilt";
}
