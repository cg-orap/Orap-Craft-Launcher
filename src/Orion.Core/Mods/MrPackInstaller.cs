using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Install;
using Orion.Core.Java;
using Orion.Core.Loaders;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Mods;

/// <summary>The <c>modrinth.index.json</c> document of a Modrinth modpack (format 1).</summary>
public sealed class MrPackIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("game")] public string Game { get; set; } = "minecraft";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "1.0.0";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("files")] public List<MrPackFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = [];
}

public sealed class MrPackFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
    [JsonPropertyName("env")] public Dictionary<string, string>? Environment { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }

    public string? Sha1 => Hashes.TryGetValue("sha1", out var value) ? value : null;

    public bool ClientRelevant => Environment is null
                                  || !Environment.TryGetValue("client", out var value)
                                  || !string.Equals(value, "unsupported", StringComparison.OrdinalIgnoreCase);
}

public sealed class MrPackResult
{
    public required bool Success { get; init; }
    public string InstanceId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string? Error { get; init; }
    public int FilesDownloaded { get; init; }
    public int OverridesCopied { get; init; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Installs Modrinth <c>.mrpack</c> files: a zip containing <c>modrinth.index.json</c>, optional
/// <c>overrides/</c> and <c>client-overrides/</c>.
///
/// The loader comes from the pack's <c>dependencies</c>; every listed file is downloaded with the
/// declared SHA-1 and everything inside the pack is confined to the new instance directory.
/// </summary>
public sealed class MrPackInstaller
{
    private readonly GamePaths _paths;
    private readonly InstanceManager _instances;
    private readonly LoaderService _loaders;
    private readonly DownloadEngine _downloader;
    private readonly MirrorResolver _mirrors;
    private readonly VersionInstallService _installer;

    public MrPackInstaller(
        GamePaths paths,
        InstanceManager instances,
        LoaderService loaders,
        DownloadEngine downloader,
        MirrorResolver mirrors,
        VersionInstallService installer)
    {
        _paths = paths;
        _instances = instances;
        _loaders = loaders;
        _downloader = downloader;
        _mirrors = mirrors;
        _installer = installer;
    }

    public async Task<MrPackResult> InstallAsync(
        string mrpackPath,
        string? instanceName = null,
        JavaRuntime? java = null,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        try
        {
            var index = await ReadIndexAsync(mrpackPath, ct).ConfigureAwait(false);
            if (index is null)
                return Failed("整合包缺少合法的 modrinth.index.json。");

            if (!index.Dependencies.TryGetValue("minecraft", out var minecraftVersion) || string.IsNullOrWhiteSpace(minecraftVersion))
                return Failed("整合包未声明 Minecraft 版本。");

            var (loaderKind, loaderVersion) = ResolveLoader(index.Dependencies);
            string versionId;

            if (loaderKind == LoaderKind.Vanilla)
            {
                output?.Report($"安装原版 Minecraft {minecraftVersion}…");
                var install = await _installer.InstallAsync(minecraftVersion, null, null, ct).ConfigureAwait(false);
                if (!install.Success) return Failed(install.Error ?? "原版安装失败。");

                versionId = minecraftVersion;
            }
            else
            {
                loaderVersion ??= await _loaders.ResolveLatestLoaderVersionAsync(loaderKind, minecraftVersion, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(loaderVersion))
                    return Failed($"无法确定 {LoaderService.DisplayName(loaderKind)} 的版本。");

                output?.Report($"安装 {LoaderService.DisplayName(loaderKind)} {loaderVersion}…");
                var result = await _loaders.InstallAsync(loaderKind, new LoaderInstallRequest
                {
                    MinecraftVersion = minecraftVersion,
                    LoaderVersion = loaderVersion,
                    Java = java,
                    Output = output,
                }, ct).ConfigureAwait(false);

                if (!result.Success) return Failed(result.Error ?? "加载器安装失败。");

                versionId = result.VersionId;
                warnings.AddRange(result.Warnings);
            }

            var name = string.IsNullOrWhiteSpace(instanceName)
                ? string.IsNullOrWhiteSpace(index.Name) ? Path.GetFileNameWithoutExtension(mrpackPath) : index.Name
                : instanceName;

            output?.Report($"创建实例 {name}…");
            var instance = await _instances.CreateAsync(name, versionId, minecraftVersion, loaderKind, loaderVersion, ct).ConfigureAwait(false);

            output?.Report($"下载整合包文件 ({index.Files.Count} 个)…");
            var requests = BuildRequests(index, instance, warnings);
            var report = await _downloader.DownloadAsync(requests, null, ct).ConfigureAwait(false);

            if (!report.Success) warnings.Add($"{report.FailedFiles} 个整合包文件下载失败。");

            var copied = await ExtractOverridesAsync(mrpackPath, instance.GameDirectory, warnings, ct).ConfigureAwait(false);

            var result2 = new MrPackResult
            {
                Success = true,
                InstanceId = instance.Id,
                VersionId = versionId,
                FilesDownloaded = report.CompletedFiles,
                OverridesCopied = copied,
            };
            result2.Warnings.AddRange(warnings);

            AppLog.Info("mrpack", "installed",
                ("instance", instance.Id), ("version", versionId),
                ("files", report.CompletedFiles), ("overrides", copied));

            return result2;
        }
        catch (OperationCanceledException)
        {
            return Failed("安装已取消。");
        }
        catch (Exception ex)
        {
            AppLog.Error("mrpack", "install-failed", ex);
            return Failed(ex.Message);
        }

        static MrPackResult Failed(string error) => new() { Success = false, Error = error };
    }

    public static async Task<MrPackIndex?> ReadIndexAsync(string mrpackPath, CancellationToken ct = default)
    {
        if (!File.Exists(mrpackPath)) return null;

        try
        {
            using var archive = ZipFile.OpenRead(mrpackPath);
            var entry = archive.GetEntry("modrinth.index.json");
            if (entry is null) return null;

            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<MrPackIndex>(stream, JsonUtil.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            AppLog.Warn("mrpack", "index-unreadable", ("path", mrpackPath), ("error", ex.Message));
            return null;
        }
    }

    private List<DownloadRequest> BuildRequests(MrPackIndex index, Instance instance, List<string> warnings)
    {
        var requests = new List<DownloadRequest>();
        var root = Path.GetFullPath(instance.GameDirectory) + Path.DirectorySeparatorChar;

        foreach (var file in index.Files)
        {
            if (!file.ClientRelevant || string.IsNullOrWhiteSpace(file.Path)) continue;

            if (file.Downloads.Count == 0)
            {
                warnings.Add($"文件缺少下载地址: {file.Path}");
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(instance.GameDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            // A pack must never write outside the instance it creates.
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"已跳过越界路径: {file.Path}");
                continue;
            }

            var urls = new List<Uri>();
            foreach (var download in file.Downloads)
            {
                if (!Uri.TryCreate(download, UriKind.Absolute, out var uri)) continue;
                foreach (var candidate in _mirrors.Resolve(uri))
                {
                    if (!urls.Contains(candidate)) urls.Add(candidate);
                }
            }

            if (urls.Count == 0) continue;

            requests.Add(new DownloadRequest
            {
                TargetPath = target,
                Urls = urls,
                Sha1 = file.Sha1,
                Size = file.FileSize,
                Label = Path.GetFileName(target),
                Verify = DownloadVerifyPolicy.Always,
            });
        }

        return requests;
    }

    private static async Task<int> ExtractOverridesAsync(string mrpackPath, string gameDirectory, List<string> warnings, CancellationToken ct)
    {
        var copied = 0;
        var root = Path.GetFullPath(gameDirectory) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(mrpackPath);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var name = entry.FullName.Replace('\\', '/');
            string? relative = null;

            if (name.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) relative = name["overrides/".Length..];
            else if (name.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)) relative = name["client-overrides/".Length..];

            if (relative is null || relative.Length == 0 || string.IsNullOrEmpty(entry.Name)) continue;

            var destination = Path.GetFullPath(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"已跳过越界的整合包条目: {name}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Overrides are authoritative: they intentionally replace downloaded files.
            await using var source = entry.Open();
            await using var target = File.Create(destination);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
            copied++;
        }

        return copied;
    }

    private static (LoaderKind Kind, string? Version) ResolveLoader(Dictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue("neoforge", out var neoforge)) return (LoaderKind.NeoForge, neoforge);
        if (dependencies.TryGetValue("fabric-loader", out var fabric)) return (LoaderKind.Fabric, fabric);
        if (dependencies.TryGetValue("quilt-loader", out var quilt)) return (LoaderKind.Quilt, quilt);
        if (dependencies.TryGetValue("forge", out var forge)) return (LoaderKind.Forge, forge);
        return (LoaderKind.Vanilla, null);
    }
}

/// <summary>
/// Exports an instance as a Modrinth <c>.mrpack</c>.
///
/// Everything the instance owns (mods, config, saves are excluded on purpose) is written to
/// <c>overrides/</c> and the loader is recorded in <c>dependencies</c>, which is a valid pack even
/// when the original files did not come from Modrinth. Saves are never included: a shared pack
/// should not overwrite someone else's world.
/// </summary>
public static class MrPackExporter
{
    private static readonly string[] IncludedFolders = ["mods", "config", "resourcepacks", "shaderpacks", "defaultconfigs", "kubejs"];
    private static readonly string[] IncludedFiles = ["options.txt", "servers.dat"];

    public sealed class ExportOptions
    {
        public string Name { get; set; } = "";
        public string VersionId { get; set; } = "1.0.0";
        public string? Summary { get; set; }
        public bool IncludeSaves { get; set; }
    }

    public static async Task<MrPackResult> ExportAsync(
        Instance instance,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ExportOptions();
        var warnings = new List<string>();

        try
        {
            var index = new MrPackIndex
            {
                FormatVersion = 1,
                Name = string.IsNullOrWhiteSpace(options.Name) ? instance.Name : options.Name,
                VersionId = options.VersionId,
                Summary = options.Summary ?? $"{instance.Name} — 由 Orion Launcher 导出",
                Dependencies = { ["minecraft"] = instance.GameVersion },
            };

            switch (instance.Loader)
            {
                case LoaderKind.NeoForge when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    index.Dependencies["neoforge"] = instance.LoaderVersion!;
                    break;
                case LoaderKind.Fabric when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    index.Dependencies["fabric-loader"] = instance.LoaderVersion!;
                    break;
                case LoaderKind.Quilt when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    index.Dependencies["quilt-loader"] = instance.LoaderVersion!;
                    break;
                case LoaderKind.Forge when !string.IsNullOrWhiteSpace(instance.LoaderVersion):
                    index.Dependencies["forge"] = instance.LoaderVersion!;
                    break;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            var indexEntry = archive.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var entryStream = indexEntry.Open())
            {
                await JsonSerializer.SerializeAsync(entryStream, index, JsonUtil.Options, ct).ConfigureAwait(false);
            }

            var gameDirectory = instance.GameDirectory;
            var folders = new List<string>(IncludedFolders);
            if (options.IncludeSaves) folders.Add("saves");

            var count = 0;

            foreach (var folder in folders)
            {
                var path = Path.Combine(gameDirectory, folder);
                if (!Directory.Exists(path)) continue;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    count += await AddAsync(archive, gameDirectory, file, warnings, ct).ConfigureAwait(false);
                }
            }

            foreach (var file in IncludedFiles)
            {
                var path = Path.Combine(gameDirectory, file);
                if (!File.Exists(path)) continue;

                count += await AddAsync(archive, gameDirectory, path, warnings, ct).ConfigureAwait(false);
            }

            // Mod loader metadata files that live at the instance root.
            foreach (var file in Directory.EnumerateFiles(gameDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                count += await AddAsync(archive, gameDirectory, file, warnings, ct).ConfigureAwait(false);
            }

            var result = new MrPackResult
            {
                Success = true,
                InstanceId = instance.Id,
                VersionId = instance.VersionId,
                OverridesCopied = count,
            };
            result.Warnings.AddRange(warnings);

            AppLog.Info("mrpack", "exported", ("path", outputPath), ("files", count));
            return result;
        }
        catch (OperationCanceledException)
        {
            return new MrPackResult { Success = false, Error = "导出已取消。" };
        }
        catch (Exception ex)
        {
            AppLog.Error("mrpack", "export-failed", ex);
            return new MrPackResult { Success = false, Error = ex.Message };
        }
    }

    private static async Task<int> AddAsync(ZipArchive archive, string gameDirectory, string file, List<string> warnings, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal))
        {
            warnings.Add($"跳过越界文件: {relative}");
            return 0;
        }

        try
        {
            var entry = archive.CreateEntry("overrides/" + relative, CompressionLevel.Optimal);
            await using var source = File.OpenRead(file);
            await using var target = entry.Open();
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法打包 {relative}: {ex.Message}");
            return 0;
        }
    }
}
