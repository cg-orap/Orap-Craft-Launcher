using System.Diagnostics;
using System.Xml.Linq;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Install;
using Orion.Core.Java;
using Orion.Core.Net;

namespace Orion.Core.Loaders;

/// <summary>
/// Forge and NeoForge share an installer-based installation model: the official installer
/// JAR resolves processors, patches the vanilla jar and writes a version JSON next to a set
/// of patched libraries.
///
/// <para>
/// "Installed" therefore means: the installer exited successfully <b>and</b> the version JSON
/// it produced exists with a main class. Downloading a loader JAR alone is never reported as
/// success.
/// </para>
/// </summary>
public abstract class InstallerBasedProvider : ILoaderProvider
{
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);

    protected readonly HttpClient Http;
    protected readonly GamePaths Paths;
    protected readonly VersionRepository Repository;
    protected readonly VersionInstallService InstallService;
    protected readonly MirrorResolver Mirrors;
    protected readonly DownloadEngine Downloader;

    protected InstallerBasedProvider(
        HttpClient http,
        GamePaths paths,
        VersionRepository repository,
        VersionInstallService installService,
        MirrorResolver mirrors,
        DownloadEngine downloader)
    {
        Http = http;
        Paths = paths;
        Repository = repository;
        InstallService = installService;
        Mirrors = mirrors;
        Downloader = downloader;
    }

    public abstract LoaderKind Kind { get; }

    /// <summary>Ordered Maven repositories that host the installer.</summary>
    protected abstract IReadOnlyList<string> MavenRepositories { get; }

    protected abstract string DisplayName { get; }

    /// <summary>Installer artifact coordinate path (without the repository base).</summary>
    protected abstract string InstallerPath(LoaderInstallRequest request);

    /// <summary>Version id the installer is expected to generate.</summary>
    protected abstract string ExpectedVersionId(LoaderInstallRequest request);

    public abstract Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default);

    public async Task<LoaderInstallResult> InstallAsync(LoaderInstallRequest request, CancellationToken ct = default)
    {
        var versionId = ExpectedVersionId(request);
        var warnings = new List<string>();

        try
        {
            // 1. The loader installer needs a complete vanilla installation to patch.
            request.Output?.Report("正在安装原版 Minecraft（这一阶段会下载完整客户端）…");
            var vanillaProgress = request.Output is null
                ? null
                : new Progress<Install.InstallProgress>(p =>
                {
                    if (!string.IsNullOrWhiteSpace(p.Message)) request.Output.Report(p.Message);
                });

            // A null sink made this phase silent: downloading the whole vanilla install looked frozen.
            var vanilla = await InstallService.InstallAsync(
                request.MinecraftVersion, new InstallOptions(), vanillaProgress, ct).ConfigureAwait(false);
            if (!vanilla.Success)
            {
                warnings.Add("原版安装未完成: " + (vanilla.Error ?? "未知错误"));
                return Failure(versionId, "无法安装原版 Minecraft，安装器无法继续。", warnings);
            }

            // 2. Fetch the installer JAR.
            request.Output?.Report($"正在下载 {DisplayName} 安装器…");
            var relative = InstallerPath(request);
            var installerFile = Path.Combine(Paths.CacheDir, "installers", Path.GetFileName(relative));

            var urls = new List<Uri>();
            foreach (var repository in MavenRepositories)
            {
                if (Uri.TryCreate(repository.TrimEnd('/') + "/" + relative, UriKind.Absolute, out var uri))
                {
                    urls.Add(uri);
                    foreach (var mirrored in Mirrors.Resolve(uri))
                    {
                        if (!urls.Contains(mirrored)) urls.Add(mirrored);
                    }
                }
            }

            if (urls.Count == 0) return Failure(versionId, "没有可用的安装器下载地址。", warnings);

            var download = await Downloader.DownloadAsync(
                [new DownloadRequest { TargetPath = installerFile, Urls = urls, Label = $"{DisplayName} installer" }],
                null,
                ct).ConfigureAwait(false);

            if (!download.Success)
                return Failure(versionId, $"下载 {DisplayName} 安装器失败: {download.Failures.FirstOrDefault()?.Message}", warnings);

            // 2b. Pre-seed every library the installer will need.
            //
            // The installer downloads its own dependencies single-threaded from its canonical
            // Maven host. On networks where that host is unreachable the whole install aborts.
            // Because the installer carries the exact SHA-1 of every artifact it needs, we can
            // fetch them with our own concurrent, resumable, verified downloader first - the
            // installer then finds them on disk and only has to run its processors.
            var seeded = await PreSeedInstallerLibrariesAsync(installerFile, request.Output, ct).ConfigureAwait(false);
            if (seeded.Failures.Count > 0)
            {
                warnings.Add($"{seeded.Failures.Count} 个安装器依赖未能预下载，安装器可能会自行重试。");
                foreach (var failure in seeded.Failures.Take(3))
                {
                    warnings.Add($"  {Path.GetFileName(failure.TargetPath)}: {failure.Message}");
                }
            }

            // 3. Run the installer headless.
            var java = request.Java ?? throw new InvalidOperationException(
            $"需要 Java 运行时来运行安装器（MC {request.MinecraftVersion}）。" +
            "请安装对应的 JDK，或在启动器的 设置 → Java 里指定 java.exe 路径后重试。");
            EnsureLauncherProfiles();
            var arguments = new List<string>();

            // Java on Windows encodes redirected stdout with the console code page unless told
            // otherwise, which would mangle non-ASCII paths in the installer output.
            if (java.MajorVersion >= 19)
            {
                arguments.Add("-Dstdout.encoding=UTF-8");
                arguments.Add("-Dstderr.encoding=UTF-8");
            }

            arguments.AddRange(
            [
                "-jar",
                installerFile,
                // joptsimple is case-sensitive: "--install-client" is rejected with
                // UnrecognizedOptionException (exit 1), so the camelCase spelling is required.
                "--installClient",
                Paths.Root,
            ]);

            if (!string.IsNullOrWhiteSpace(request.InstallerMirror))
            {
                arguments.Add("--mirror");
                arguments.Add(request.InstallerMirror);
            }

            request.Output?.Report($"正在运行安装器 ({DisplayName} {request.LoaderVersion})…");
            var (exitCode, output) = await RunInstallerAsync(java.ExecutablePath, arguments, Paths.Root, request.Output, ct).ConfigureAwait(false);

            if (exitCode != 0)
            {
                return Failure(versionId,
                    $"{DisplayName} 安装器以退出代码 {exitCode} 结束。" + LastMeaningfulLine(output),
                    warnings,
                    output);
            }

            // 4. Verify the installer actually produced a launchable version.
            if (!File.Exists(Paths.VersionJsonPath(versionId)))
            {
                var discovered = FindGeneratedVersion();
                if (discovered is null)
                    return Failure(versionId, $"安装器未生成版本配置 ({versionId})，安装未完成。", warnings, output);

                versionId = discovered;
            }

            var metadata = await Util.JsonUtil.DeserializeFileAsync<Json.VersionMetadata>(Paths.VersionJsonPath(versionId), ct).ConfigureAwait(false);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.MainClass))
                return Failure(versionId, "安装器生成的版本配置缺少 mainClass。", warnings, output);

            // 5. Complete libraries/assets through the normal installer path.
            request.Output?.Report("正在校验并补齐游戏文件…");
            var finalize = await InstallService.InstallAsync(versionId, new InstallOptions(), null, ct).ConfigureAwait(false);
            warnings.AddRange(finalize.Warnings);

            if (!finalize.Success)
                return Failure(versionId, "补齐游戏文件失败: " + (finalize.Error ?? "未知错误"), warnings, output);

            AppLog.Info("loaders", $"{Kind}-installed",
                ("mc", request.MinecraftVersion),
                ("loader", request.LoaderVersion),
                ("version", versionId));

            var success = new LoaderInstallResult { Success = true, VersionId = versionId };
            success.Warnings.AddRange(warnings);
            success.Output.AddRange(output);
            return success;
        }
        catch (OperationCanceledException)
        {
            return Failure(versionId, "安装已取消。", warnings);
        }
        catch (Exception ex)
        {
            AppLog.Error("loaders", $"{Kind}-install-failed", ex);
            return Failure(versionId, ex.Message, warnings);
        }
    }

    /// <summary>
    /// Reads <c>install_profile.json</c> and <c>version.json</c> from the installer JAR and
    /// downloads every declared library into the shared Maven layout.
    /// </summary>
    private async Task<DownloadReport> PreSeedInstallerLibrariesAsync(
        string installerFile,
        IProgress<string>? output,
        CancellationToken ct)
    {
        var requests = new List<DownloadRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var archive = System.IO.Compression.ZipFile.OpenRead(installerFile))
        {
            foreach (var entryName in new[] { "install_profile.json", "version.json" })
            {
                var entry = archive.GetEntry(entryName);
                if (entry is null) continue;

                System.Text.Json.JsonDocument document;
                try
                {
                    await using var stream = entry.Open();
                    document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
                {
                    AppLog.Warn("loaders", "installer-json-unreadable", ("entry", entryName), ("error", ex.Message));
                    continue;
                }

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                        libraries.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var library in libraries.EnumerateArray())
                    {
                        var request = BuildLibraryRequest(library, seen);
                        if (request is not null) requests.Add(request);
                    }
                }
            }
        }

        if (requests.Count == 0) return new DownloadReport { TotalFiles = 0, Duration = TimeSpan.Zero };

        output?.Report($"正在预下载安装器依赖 ({requests.Count} 个文件)…");

        // This phase silently downloaded ~60 jars; forward the engine progress so the bar moves.
        var preseedProgress = output is null
            ? null
            : new Progress<DownloadProgress>(p =>
                output.Report($"正在预下载安装器依赖 {p.CompletedFiles + p.FailedFiles}/{p.TotalFiles} · {p.BytesPerSecond / 1024:0} KB/s"));
        var report = await Downloader.DownloadAsync(requests, preseedProgress, ct).ConfigureAwait(false);
        output?.Report($"安装器依赖预下载完成: {report.CompletedFiles} 个文件");
        return report;
    }

    private DownloadRequest? BuildLibraryRequest(System.Text.Json.JsonElement library, HashSet<string> seen)
    {
        string? path = null;
        string? url = null;
        string? sha1 = null;
        long size = 0;

        if (library.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("artifact", out var artifact) &&
            artifact.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (artifact.TryGetProperty("path", out var p)) path = p.GetString();
            if (artifact.TryGetProperty("url", out var u)) url = u.GetString();
            if (artifact.TryGetProperty("sha1", out var h)) sha1 = h.GetString();
            if (artifact.TryGetProperty("size", out var s) && s.TryGetInt64(out var parsedSize)) size = parsedSize;
        }

        var name = library.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(path) && Json.MavenCoordinate.TryParse(name, out var coordinate))
            path = coordinate.ToRepositoryPath();

        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!seen.Add(path)) return null;

        var urls = new List<Uri>();

        void AddUrl(Uri uri)
        {
            foreach (var candidate in MavenUrlRewriter.Candidates(uri))
            {
                if (!urls.Contains(candidate)) urls.Add(candidate);
            }
        }

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var direct))
        {
            AddUrl(direct);
        }
        else if (library.TryGetProperty("url", out var repo) && repo.GetString() is { Length: > 0 } repositoryBase &&
                 Uri.TryCreate(repositoryBase, UriKind.Absolute, out var repoUri) &&
                 Uri.TryCreate(repoUri, path, out var combined))
        {
            AddUrl(combined);
        }

        if (urls.Count == 0) return null;

        return new DownloadRequest
        {
            TargetPath = Path.Combine(Paths.LibrariesDir, path.Replace('/', Path.DirectorySeparatorChar)),
            Urls = urls,
            Sha1 = sha1,
            Size = size,
            Label = Path.GetFileName(path),
        };
    }

    /// <summary>
    /// The Forge / NeoForge installer refuses to run unless the target directory looks like a
    /// Minecraft installation, which it detects through <c>launcher_profiles.json</c>. The
    /// official launcher creates that file; we create the minimal valid document when it is
    /// missing. No game data is invented — this file only lists launcher profiles.
    /// </summary>
    private void EnsureLauncherProfiles()
    {
        var path = Path.Combine(Paths.Root, "launcher_profiles.json");
        if (File.Exists(path)) return;

        const string minimal = """
            {
              "profiles": {},
              "settings": {
                "enableSnapshots": false,
                "enableAdvanced": false,
                "keepLauncherOpen": false,
                "showGameLog": false,
                "showMenu": false,
                "soundOn": false
              },
              "version": 3
            }
            """;

        File.WriteAllText(path, minimal);
        AppLog.Info("loaders", "launcher-profiles-created", ("path", path));
    }

    private static LoaderInstallResult Failure(string versionId, string error, List<string> warnings, List<string>? output = null)
    {
        var result = new LoaderInstallResult
        {
            Success = false,
            VersionId = versionId,
            Error = error,
        };

        result.Warnings.AddRange(warnings);
        if (output is not null) result.Output.AddRange(output);
        return result;
    }

    private string? FindGeneratedVersion()
    {
        if (!Directory.Exists(Paths.VersionsDir)) return null;

        var prefix = Kind == LoaderKind.NeoForge ? "neoforge-" : "forge-";

        return Directory.EnumerateDirectories(Paths.VersionsDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(name => File.Exists(Paths.VersionJsonPath(name!)))
            .OrderByDescending(name => File.GetLastWriteTimeUtc(Paths.VersionJsonPath(name!)))
            .FirstOrDefault();
    }

    private static string LastMeaningfulLine(IEnumerable<string> output)
    {
        var line = output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return string.IsNullOrWhiteSpace(line) ? "" : $" 最后输出: {line.Trim()}";
    }

    private static async Task<(int ExitCode, List<string> Output)> RunInstallerAsync(
        string javaPath,
        List<string> arguments,
        string workingDirectory,
        IProgress<string>? log,
        CancellationToken ct)
    {
        var output = new List<string>();

        var startInfo = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        void Capture(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                output.Add(line);
                if (output.Count > 4000) output.RemoveAt(0);
            }

            log?.Report(line);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InstallerTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (-1, ["安装器超时或被取消。"]);
        }

        return (process.ExitCode, output);
    }

    protected static async Task<XDocument> LoadMavenMetadataAsync(
        HttpClient http,
        MirrorResolver mirrors,
        string repositoryBase,
        string artifactPath,
        CancellationToken ct)
    {
        var url = new Uri($"{repositoryBase.TrimEnd('/')}/{artifactPath}/maven-metadata.xml");
        var xml = await HttpUtil.GetStringAsync(http, mirrors.Resolve(url), ct).ConfigureAwait(false);
        return XDocument.Parse(xml);
    }
}

public sealed class NeoForgeProvider : InstallerBasedProvider
{
    // maven.neoforged.net is the canonical host; the other two are mirrors used when the
    // canonical host is unreachable (which is common in some regions).
    private static readonly string[] Repositories =
    [
        "https://maven.neoforged.net/releases",
        "https://maven.creeperhost.net",
        "https://bmclapi2.bangbang93.com/maven",
    ];

    public NeoForgeProvider(HttpClient http, GamePaths paths, VersionRepository repository, VersionInstallService installService, MirrorResolver mirrors, DownloadEngine downloader)
        : base(http, paths, repository, installService, mirrors, downloader)
    {
    }

    public override LoaderKind Kind => LoaderKind.NeoForge;
    protected override IReadOnlyList<string> MavenRepositories => Repositories;
    protected override string DisplayName => "NeoForge";

    /// <summary>
    /// NeoForge versions use the Minecraft version as their prefix since 1.20.2
    /// (1.21.1 → 21.1.x). For 1.20.1 the fork lives under <c>net.neoforged:forge</c>.
    /// </summary>
    protected override string InstallerPath(LoaderInstallRequest request)
    {
        if (IsLegacy20X(request.MinecraftVersion))
        {
            var artifact = $"net/neoforged/forge/{request.MinecraftVersion}-{request.LoaderVersion}";
            return $"{artifact}/forge-{request.MinecraftVersion}-{request.LoaderVersion}-installer.jar";
        }

        return $"net/neoforged/neoforge/{request.LoaderVersion}/neoforge-{request.LoaderVersion}-installer.jar";
    }

    protected override string ExpectedVersionId(LoaderInstallRequest request)
        => IsLegacy20X(request.MinecraftVersion)
            ? $"neoforge-{request.MinecraftVersion}-{request.LoaderVersion}"
            : $"neoforge-{request.LoaderVersion}";

    public override async Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var result = new List<LoaderVersionInfo>();

        if (IsLegacy20X(minecraftVersion))
        {
            foreach (var repository in Repositories)
            {
                try
                {
                    var document = await LoadMavenMetadataAsync(Http, Mirrors, repository, "net/neoforged/forge", ct).ConfigureAwait(false);
                    var prefix = minecraftVersion + "-";
                    var versions = document.Descendants("version")
                        .Select(e => e.Value.Trim())
                        .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(v => v[prefix.Length..])
                        .Distinct()
                        .OrderByDescending(v => v, NumericVersionComparer.Instance)
                        .ToList();

                    foreach (var version in versions)
                    {
                        result.Add(new LoaderVersionInfo(version, Stable: true));
                    }

                    if (result.Count > 0) return result;
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or IOException)
                {
                    AppLog.Debug("loaders", "neoforge-legacy-metadata-failed", ("repo", repository), ("error", ex.Message));
                }
            }

            return result;
        }

        var major = minecraftVersion.Split('.');
        if (major.Length < 3) return result;

        var versionPrefix = $"{major[1]}.{major[2]}.";

        foreach (var repository in Repositories)
        {
            try
            {
                var document = await LoadMavenMetadataAsync(Http, Mirrors, repository, "net/neoforged/neoforge", ct).ConfigureAwait(false);
                var versions = document.Descendants("version")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.StartsWith(versionPrefix, StringComparison.Ordinal))
                    .Distinct()
                    .OrderByDescending(v => v, NumericVersionComparer.Instance)
                    .ToList();

                if (versions.Count == 0) continue;

                foreach (var version in versions)
                {
                    result.Add(new LoaderVersionInfo(version, Stable: !version.Contains("-beta", StringComparison.OrdinalIgnoreCase)));
                }

                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or IOException)
            {
                AppLog.Debug("loaders", "neoforge-metadata-failed", ("repo", repository), ("error", ex.Message));
            }
        }

        return result;
    }

    private static bool IsLegacy20X(string minecraftVersion)
        => minecraftVersion is "1.20.1" or "1.20";
}

public sealed class ForgeProvider : InstallerBasedProvider
{
    private static readonly string[] Repositories =
    [
        "https://maven.minecraftforge.net",
        "https://bmclapi2.bangbang93.com/maven",
    ];

    public ForgeProvider(HttpClient http, GamePaths paths, VersionRepository repository, VersionInstallService installService, MirrorResolver mirrors, DownloadEngine downloader)
        : base(http, paths, repository, installService, mirrors, downloader)
    {
    }

    public override LoaderKind Kind => LoaderKind.Forge;
    protected override IReadOnlyList<string> MavenRepositories => Repositories;
    protected override string DisplayName => "Forge";

    protected override string InstallerPath(LoaderInstallRequest request)
    {
        var combined = $"{request.MinecraftVersion}-{request.LoaderVersion}";
        return $"net/minecraftforge/forge/{combined}/forge-{combined}-installer.jar";
    }

    protected override string ExpectedVersionId(LoaderInstallRequest request)
        => $"{request.MinecraftVersion}-forge-{request.LoaderVersion}";

    public override async Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var result = new List<LoaderVersionInfo>();

        foreach (var repository in Repositories)
        {
            try
            {
                var document = await LoadMavenMetadataAsync(Http, Mirrors, repository, "net/minecraftforge/forge", ct).ConfigureAwait(false);
                var prefix = minecraftVersion + "-";
                var versions = document.Descendants("version")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(v => v[prefix.Length..])
                    .Distinct()
                    .OrderByDescending(v => v, NumericVersionComparer.Instance)
                    .ToList();

                if (versions.Count == 0) continue;

                foreach (var version in versions)
                {
                    result.Add(new LoaderVersionInfo(version, Stable: !version.Contains('-')));
                }

                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or IOException)
            {
                AppLog.Debug("loaders", "forge-metadata-failed", ("repo", repository), ("error", ex.Message));
            }
        }

        return result;
    }
}
