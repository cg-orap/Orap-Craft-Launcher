using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;
using Orion.Core.Game;
using Orion.Core.Instances;
using Orion.Core.Java;
using Orion.Core.Loaders;

namespace Orion.Core.Mods;

public enum PackArchiveKind
{
    /// <summary>Modrinth <c>.mrpack</c>: handled by <see cref="MrPackInstaller"/>.</summary>
    Modrinth,

    /// <summary>CurseForge zip with a <c>manifest.json</c> (<c>minecraftModpack</c> format).</summary>
    CurseForge,

    /// <summary>Plain zip that already contains <c>.minecraft</c> / <c>mods</c> / <c>config</c>.</summary>
    MinecraftZip,

    Unknown,
}

/// <summary>What a dropped pack archive turned out to be.</summary>
public sealed record PackArchiveInfo(
    PackArchiveKind Kind,
    string Name,
    string? MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    int FileCount,
    string? Note);

/// <summary>
/// Installs CurseForge-shaped and plain-zip modpacks, complementing the Modrinth path.
///
/// CurseForge manifests only carry <c>projectID</c>/<c>fileID</c>, so mods are fetched from
/// CurseForge's public file redirect; anything that cannot be resolved is reported instead of
/// silently skipped. The <c>overrides/</c> folder and the loader itself are always installed, so the
/// user still ends up with a launchable version plus a list of the files that need manual attention.
/// </summary>
public sealed class PackArchiveInstaller
{
    private readonly GamePaths _paths;
    private readonly InstanceManager _instances;
    private readonly LoaderService _loaders;
    private readonly InstanceImporter _importer;
    private readonly HttpClient _http;

    public PackArchiveInstaller(
        GamePaths paths,
        InstanceManager instances,
        LoaderService loaders,
        InstanceImporter importer,
        HttpClient http)
    {
        _paths = paths;
        _instances = instances;
        _loaders = loaders;
        _importer = importer;
        _http = http;
    }

    /// <summary>Quick sniff of an archive without installing anything (used for drag &amp; drop hints).</summary>
    public static PackArchiveInfo Inspect(string path)
    {
        if (!File.Exists(path)) return new(PackArchiveKind.Unknown, "", null, LoaderKind.Vanilla, null, 0, "文件不存在");

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

            if (names.Any(n => n.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)))
            {
                return new(PackArchiveKind.Modrinth, Path.GetFileNameWithoutExtension(path), null, LoaderKind.Vanilla, null, 0, null);
            }

            var manifestEntry = zip.GetEntry("manifest.json");
            if (manifestEntry is not null)
            {
                using var stream = manifestEntry.Open();
                var manifest = JsonSerializer.Deserialize<CurseForgeManifest>(stream);
                if (manifest?.Minecraft is not null)
                {
                    var (loader, version) = ParseLoader(manifest.Minecraft.ModLoaders?.FirstOrDefault()?.Id);
                    return new(
                        PackArchiveKind.CurseForge,
                        string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(path) : manifest.Name!,
                        manifest.Minecraft.Version,
                        loader,
                        version,
                        manifest.Files?.Count ?? 0,
                        null);
                }
            }

            var hasMinecraftFolder = names.Any(n => n.Contains(".minecraft/", StringComparison.OrdinalIgnoreCase)
                                                    || n.StartsWith(".minecraft", StringComparison.OrdinalIgnoreCase));
            var hasMods = names.Any(n => n.Contains("mods/", StringComparison.OrdinalIgnoreCase));
            var hasConfig = names.Any(n => n.Contains("config/", StringComparison.OrdinalIgnoreCase));

            if (hasMinecraftFolder || hasMods || hasConfig)
            {
                return new(
                    PackArchiveKind.MinecraftZip,
                    Path.GetFileNameWithoutExtension(path),
                    null,
                    LoaderKind.Vanilla,
                    null,
                    names.Count(n => n.Contains("mods/", StringComparison.OrdinalIgnoreCase)),
                    hasMinecraftFolder ? ".minecraft 目录" : "mods/config 目录");
            }

            return new(PackArchiveKind.Unknown, Path.GetFileNameWithoutExtension(path), null, LoaderKind.Vanilla, null, 0,
                "压缩包里没有 manifest.json / modrinth.index.json / .minecraft，无法识别整合包格式");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            AppLog.Warn("packs", "inspect-failed", ("error", ex.Message));
            return new(PackArchiveKind.Unknown, Path.GetFileNameWithoutExtension(path), null, LoaderKind.Vanilla, null, 0, ex.Message);
        }
    }

    /// <summary>Installs a CurseForge manifest pack as a new instance (loader + overrides + mods).</summary>
    public async Task<MrPackResult> InstallCurseForgeAsync(
        string archivePath,
        string? instanceName = null,
        JavaRuntime? java = null,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var info = Inspect(archivePath);
        if (info.Kind != PackArchiveKind.CurseForge || string.IsNullOrWhiteSpace(info.MinecraftVersion))
        {
            return new MrPackResult { Success = false, Error = info.Note ?? "不是 CurseForge 整合包" };
        }

        var name = string.IsNullOrWhiteSpace(instanceName) ? info.Name : instanceName!;
        var warnings = new List<string>();
        var unresolved = new List<string>();

        try
        {
            output?.Report($"读取 CurseForge 清单：{name}（MC {info.MinecraftVersion}，{LoaderService.DisplayName(info.Loader)}）");

            // 1. the loader (and with it the version the launcher will start)
            var versionId = info.MinecraftVersion;
            if (info.Loader != LoaderKind.Vanilla && !string.IsNullOrWhiteSpace(info.LoaderVersion))
            {
                output?.Report($"安装 {LoaderService.DisplayName(info.Loader)} {info.LoaderVersion}…");

                var install = await _loaders.InstallAsync(info.Loader, new LoaderInstallRequest
                {
                    MinecraftVersion = info.MinecraftVersion,
                    LoaderVersion = info.LoaderVersion!,
                    Java = java,
                    Output = output,
                }, ct).ConfigureAwait(false);

                if (!install.Success)
                {
                    return new MrPackResult
                    {
                        Success = false,
                        Error = "加载器安装失败：" + (install.Error ?? "未知错误"),
                    };
                }

                versionId = install.VersionId;
                warnings.AddRange(install.Warnings);
            }

            // 2. instance + overrides
            var instance = await _instances
                .CreateAsync(name, versionId, info.MinecraftVersion, info.Loader, info.LoaderVersion, ct)
                .ConfigureAwait(false);

            var overridesCopied = 0;
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in zip.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    const string prefix = "overrides/";
                    if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.EndsWith('/')) continue;

                    var target = Path.GetFullPath(Path.Combine(instance.GameDirectory, relative[prefix.Length..]));
                    if (!target.StartsWith(Path.GetFullPath(instance.GameDirectory), StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("已跳过越界路径：" + relative);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    overridesCopied++;
                }
            }

            // 3. mods: CurseForge manifests only carry ids, so resolve them through the public redirect
            var manifest = ReadManifest(archivePath);
            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            Directory.CreateDirectory(modsDirectory);

            var downloaded = 0;
            foreach (var file in manifest?.Files ?? [])
            {
                ct.ThrowIfCancellationRequested();

                var url = $"https://www.curseforge.com/api/v1/mods/{file.ProjectId}/files/{file.FileId}/download";
                var target = Path.Combine(modsDirectory, $"{file.ProjectId}-{file.FileId}.jar");

                try
                {
                    output?.Report($"下载模组 {file.ProjectId}/{file.FileId}…");
                    // No public key-less download API: the request may redirect to HTML or stall, so it
                // is bounded and counted as unresolved instead of leaving the task in 进行中 forever.
                using var perFile = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perFile.CancelAfter(TimeSpan.FromSeconds(30));

                using var response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perFile.Token)
                    .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        unresolved.Add($"{file.ProjectId}/{file.FileId}（HTTP {(int)response.StatusCode}）");
                        continue;
                    }

                    // Prefer the server-provided name so the jar is recognisable.
                    var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                                   ?? response.Content.Headers.ContentDisposition?.FileName;

                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        target = Path.Combine(modsDirectory, Path.GetFileName(fileName.Trim('"')));
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var destination = File.Create(target);
                    await stream.CopyToAsync(destination, ct).ConfigureAwait(false);
                    downloaded++;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    unresolved.Add($"{file.ProjectId}/{file.FileId}（{ex.Message}）");
                }
            }

            if (unresolved.Count > 0)
            {
                warnings.Add($"有 {unresolved.Count} 个模组无法自动下载，需要手动补齐（CurseForge 只提供项目/文件 ID）：");
                warnings.AddRange(unresolved.Take(20));
            }

            output?.Report($"整合包已安装：{name}（{downloaded} 个模组，{overridesCopied} 个配置文件）");

            var result = new MrPackResult
            {
                Success = true,
                InstanceId = instance.Id,
                VersionId = versionId,
                FilesDownloaded = downloaded,
                OverridesCopied = overridesCopied,
            };

            result.Warnings.AddRange(warnings);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            AppLog.Warn("packs", "curseforge-install-failed", ("error", ex.Message));
            return new MrPackResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Imports a plain zip that already contains <c>.minecraft</c>, <c>mods</c> or <c>config</c>
    /// (the usual shape of domestic launcher exports). Nothing is downloaded.
    /// </summary>
    public async Task<MrPackResult> InstallMinecraftZipAsync(
        string archivePath,
        string? instanceName = null,
        string? baseVersionId = null,
        string? baseGameVersion = null,
        CancellationToken ct = default)
    {
        var info = Inspect(archivePath);
        var extractionRoot = Path.Combine(_paths.CacheDir, "packs", "extract", Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(extractionRoot);
            ZipFile.ExtractToDirectory(archivePath, extractionRoot, overwriteFiles: true);

            var dotMinecraft = FindDirectory(extractionRoot, ".minecraft");

            // A real .minecraft folder can go through the normal importer (handles versions/, mods/, saves/).
            if (dotMinecraft is not null)
            {
                var imported = await _importer.ImportAsync(dotMinecraft, ct).ConfigureAwait(false);
                if (imported.Instances.Count == 0)
                {
                    return new MrPackResult { Success = false, Error = "没有找到可导入的内容： " + string.Join("; ", imported.Warnings.Take(3)) };
                }

                var first = imported.Instances[0];
                var importedResult = new MrPackResult
                {
                    Success = true,
                    InstanceId = first.Id,
                    VersionId = first.VersionId,
                };

                importedResult.Warnings.AddRange(imported.Warnings);
                return importedResult;
            }

            // Otherwise: build an instance on top of an existing version and copy the pack contents.
            if (string.IsNullOrWhiteSpace(baseVersionId))
            {
                return new MrPackResult
                {
                    Success = false,
                    Error = "该压缩包没有 .minecraft 目录，需要先在「版本管理」里选择要作为基础的版本。",
                };
            }

            var instance = await _instances
                .CreateAsync(string.IsNullOrWhiteSpace(instanceName) ? info.Name : instanceName!, baseVersionId!, baseGameVersion ?? baseVersionId!, ct: ct)
                .ConfigureAwait(false);

            var copied = 0;
            foreach (var directory in new[] { "mods", "config", "resourcepacks", "shaderpacks", "saves", "scripts", "defaultconfigs" })
            {
                var source = FindDirectory(extractionRoot, directory);
                if (source is null) continue;

                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(source, file);
                    var target = Path.GetFullPath(Path.Combine(instance.GameDirectory, directory, relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    copied++;
                }
            }

            var zipResult = new MrPackResult
            {
                Success = true,
                InstanceId = instance.Id,
                VersionId = instance.VersionId,
                OverridesCopied = copied,
            };

            if (info.Note is not null) zipResult.Warnings.Add("识别为：" + info.Note);
            return zipResult;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Warn("packs", "zip-install-failed", ("error", ex.Message));
            return new MrPackResult { Success = false, Error = ex.Message };
        }
        finally
        {
            TryDelete(extractionRoot);
        }
    }

    private static CurseForgeManifest? ReadManifest(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry("manifest.json");
        if (entry is null) return null;

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<CurseForgeManifest>(stream);
    }

    private static (LoaderKind Kind, string? Version) ParseLoader(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (LoaderKind.Vanilla, null);

        var separator = id.IndexOf('-');
        if (separator <= 0) return (LoaderKind.Vanilla, null);

        var kind = id[..separator].ToLowerInvariant() switch
        {
            "forge" => LoaderKind.Forge,
            "neoforge" => LoaderKind.NeoForge,
            "fabric" => LoaderKind.Fabric,
            "quilt" => LoaderKind.Quilt,
            _ => LoaderKind.Vanilla,
        };

        return (kind, id[(separator + 1)..]);
    }

    private static string? FindDirectory(string root, string name)
    {
        if (!Directory.Exists(root)) return null;

        var direct = Path.Combine(root, name);
        if (Directory.Exists(direct)) return direct;

        foreach (var child in Directory.EnumerateDirectories(root))
        {
            var nested = Path.Combine(child, name);
            if (Directory.Exists(nested)) return nested;
        }

        return null;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("packs", "cleanup-failed", ("error", ex.Message));
        }
    }

    private sealed class CurseForgeManifest
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("minecraft")] public CurseForgeMinecraft? Minecraft { get; set; }
        [JsonPropertyName("files")] public List<CurseForgeFile>? Files { get; set; }
    }

    private sealed class CurseForgeMinecraft
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("modLoaders")] public List<CurseForgeModLoader>? ModLoaders { get; set; }
    }

    private sealed class CurseForgeModLoader
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("primary")] public bool Primary { get; set; }
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("projectID")] public long ProjectId { get; set; }
        [JsonPropertyName("fileID")] public long FileId { get; set; }
        [JsonPropertyName("required")] public bool Required { get; set; } = true;
    }
}
