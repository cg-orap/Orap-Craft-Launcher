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
    private readonly string? _curseForgeApiKey;
    private readonly ModDependencyRepair? _dependencyRepair;

    public PackArchiveInstaller(
        GamePaths paths,
        InstanceManager instances,
        LoaderService loaders,
        InstanceImporter importer,
        HttpClient http,
        string? curseForgeApiKey = null,
        ModDependencyRepair? dependencyRepair = null)
    {
        _paths = paths;
        _instances = instances;
        _loaders = loaders;
        _importer = importer;
        _http = http;
        _curseForgeApiKey = curseForgeApiKey;
        _dependencyRepair = dependencyRepair;
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


            // 3. 模组文件：照 Axolotl 的做法——先解析清单（真实文件名 + sha1 + 大小），再下载并校验。
            var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
            Directory.CreateDirectory(modsDirectory);

            var downloadResult = await DownloadPackModsAsync(archivePath, modsDirectory, output, ct)
                .ConfigureAwait(false);

            var downloaded = downloadResult.Downloaded;

            if (downloadResult.UnavailableCount > 0)
            {
                warnings.Add($"有 {downloadResult.UnavailableCount} 个文件拿不到元数据（CurseForge 没返回文件名/校验和）。");
            }

            if (downloadResult.Failed.Count > 0)
            {
                warnings.Add($"有 {downloadResult.Failed.Count} 个文件没下载成功：");
                warnings.AddRange(downloadResult.Failed.Take(20));
            }

            foreach (var item in downloadResult.Failed) unresolved.Add(item);

            // 收尾自检：坏 jar 留一个就会让 Forge 启动失败，宁可这里删掉并提示
            var pruned = PruneBrokenJars(Path.Combine(instance.GameDirectory, "mods"), warnings);
            if (pruned > 0)
            {
                output?.Report($"已移除 {pruned} 个损坏的模组文件，请重新安装缺失的模组。");
            }

            await CheckAndRepairDependenciesAsync(instance, info.MinecraftVersion,
                CurseForgeClient.LoaderTypeOf(info.Loader), warnings, output, ct).ConfigureAwait(false);

            // 有文件没下下来时不能只说"已安装"：用户看到的就是启动后"缺前置"的红字。
            if (unresolved.Count > 0)
            {
                output?.Report($"整合包不完整：{downloaded}/{downloadResult.Downloaded + downloadResult.Failed.Count} 个模组下载成功，{unresolved.Count} 个失败。");
                warnings.Add($"整合包不完整：{unresolved.Count} 个文件没下载下来（启动会报缺前置）。");
            }
            else
            {
                output?.Report($"整合包已安装：{name}（{downloaded} 个模组，{overridesCopied} 个配置文件）");
            }

            await WriteInstallReportAsync(instance.GameDirectory, name,
                    downloadResult.Downloaded + downloadResult.Failed.Count + downloadResult.UnavailableCount,
                    downloaded, unresolved)
                .ConfigureAwait(false);

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

    /// <summary>
    /// One CurseForge file, resolved the way PCL2/HMCL do it: the public ForgeCDN object first (no
    /// key, fast CDN), the official redirect as a fallback. Anything still failing is queued with a
    /// browser link so the user can fetch it by hand instead of an opaque "some files failed".
    /// </summary>
    /// <summary>
    /// 装完之后检查并自动补齐必需前置。没有 Key 或没有 repair 服务时只报告缺失，不发请求。
    /// </summary>
    private async Task CheckAndRepairDependenciesAsync(
        Instance instance, string? minecraftVersion, int? modLoaderType,
        List<string> warnings, IProgress<string>? output, CancellationToken ct)
    {
        var modsDirectory = Path.Combine(instance.GameDirectory, "mods");
        var scan = ModDependencyScanner.Scan(modsDirectory);

        // KubeJS 脚本引用了没装的模组时，游戏会卡在 "client script errors" 界面，
        // 这既不崩游戏也补不了（没有可装的来源），只能明确告诉用户是哪一行。
        if (scan.ScriptReferences is { Count: > 0 } scripts)
        {
            warnings.Add($"有 {scripts.Count} 处 KubeJS 脚本引用了未安装模组的物品，游戏会在启动时停在脚本错误界面：");
            warnings.AddRange(scripts.Take(10)
                .Select(r => $"  {r.File}:{r.Line} → {r.ItemId}（缺少 {r.Namespace}）"));
            warnings.Add("  处理办法：装回对应模组，或把这些脚本行注释掉。");
        }

        if (scan.Missing.Count == 0) return;

        output?.Report($"检测到 {scan.Missing.Count} 个前置缺失，正在尝试自动补齐…");

        if (_dependencyRepair is null)
        {
            warnings.Add($"缺少 {scan.Missing.Count} 个前置模组，需要手动安装：");
            warnings.AddRange(scan.Missing.Take(20)
                .Select(m => $"  {m.ModId}（被 {m.RequiredBy} 需要，版本 {m.VersionRange}）"));
            return;
        }

        var result = await _dependencyRepair.RepairAsync(
            instance.GameDirectory, scan.Missing, minecraftVersion, modLoaderType,
            Path.Combine(instance.GameDirectory, ".orap-cache"),
            new Progress<string>(line => output?.Report(line)), ct).ConfigureAwait(false);

        if (result.Installed.Count > 0)
        {
            warnings.Add($"已自动补齐 {result.Installed.Count} 个前置：");
            warnings.AddRange(result.Installed);
        }

        if (result.Failed.Count > 0)
        {
            warnings.Add($"有 {result.Failed.Count} 个前置没补上（可在模组页手动搜索安装）：");
            warnings.AddRange(result.Failed.Take(20));
        }
    }

    /// <summary>
    /// 把 mods 里 <c>curseforge-&lt;projectID&gt;-&lt;fileID&gt;.jar</c> 这类占位文件名改回真实文件名。
    ///
    /// 为什么值得做：整合包自带的完整性检查（例如 Better MC 的 "Too Many Changes Warning"）
    /// 会把这些名字对不上清单的文件算成"用户改过整合包"，于是一进游戏就弹一堆警告。
    /// 改名需要查 CurseForge 元数据，所以单独做成一步、由用户点按钮触发。
    /// </summary>
    public async Task<int> NormalizePlaceholderNamesAsync(
        string gameDirectory,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        AppLog.Info("packs", "rename-start", ("dir", modsDirectory), ("hasKey", !string.IsNullOrWhiteSpace(_curseForgeApiKey)));

        if (!Directory.Exists(modsDirectory) || string.IsNullOrWhiteSpace(_curseForgeApiKey)) return 0;

        var placeholders = new List<(string Path, int Project, int FileId)>();

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var match = System.Text.RegularExpressions.Regex.Match(stem, @"^curseforge-(\d+)-(\d+)$");
            if (!match.Success) continue;

            placeholders.Add((path, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)));
        }

        AppLog.Info("packs", "rename-placeholders", ("count", placeholders.Count));
        if (placeholders.Count == 0) return 0;

        output?.Report($"发现 {placeholders.Count} 个文件用的是占位名，正在改回真实文件名…");

        var metaClient2 = new CurseForgeClient(_http, _curseForgeApiKey);
        var renamed = 0;

        foreach (var (path, project, fileId) in placeholders)
        {
            ct.ThrowIfCancellationRequested();

            var meta = await metaClient2.GetFileAsync(project, fileId, ct).ConfigureAwait(false);
            var target = CurseForgeCdn.NormalizeFileName(meta?.FileName);
            if (target is null)
            {
                AppLog.Warn("packs", "rename-no-metadata", ("project", project), ("file", fileId));
                continue;
            }

            var destination = Path.Combine(modsDirectory, target);
            if (string.Equals(destination, path, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                // 目标已存在（同名文件）就不动，避免覆盖
                if (File.Exists(destination)) continue;

                File.Move(path, destination);
                renamed++;
                AppLog.Info("packs", "renamed", ("from", Path.GetFileName(path)), ("to", target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("packs", "rename-failed", ("from", Path.GetFileName(path)), ("error", ex.Message));
            }
        }

        output?.Report($"已把 {renamed} 个文件改回真实名字。");
        return renamed;
    }

    /// <summary>
    /// 按安装报告重下"当时没下下来"的文件。
    ///
    /// 报告里记的是 projectID + fileID，和整合包 manifest 一致，
    /// 所以不需要再靠 modId 搜名字——这是补全一个"装了一半"的整合包最可靠的办法。
    /// </summary>
    public async Task<int> RepairIncompleteAsync(
        string gameDirectory,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var reportPath = Path.Combine(gameDirectory, ".orap-install-report.json");
        if (!File.Exists(reportPath)) return 0;

        List<(long Project, long File)> pending;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, ct).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("failures", out var failures) ||
                failures.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            pending = [];
            foreach (var entry in failures.EnumerateArray())
            {
                var text = entry.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // 形如 "curseforge-1111501-8747159.jar  →  https://..."
                var match = System.Text.RegularExpressions.Regex.Match(text, @"curseforge-(\d+)-(\d+)\.jar");
                if (match.Success)
                {
                    pending.Add((long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            AppLog.Warn("packs", "install-report-unreadable", ("error", ex.Message));
            return 0;
        }

        if (pending.Count == 0) return 0;

        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        output?.Report($"安装报告里还有 {pending.Count} 个文件没下下来，正在重试…");

        var recovered = 0;
        var stillFailed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var warnings = new List<string>();

        foreach (var (project, fileId) in pending)
        {
            ct.ThrowIfCancellationRequested();

            var attempt = new CurseForgeFile { ProjectId = project, FileId = fileId };
            var ok = await DownloadCurseForgeFileCoreAsync(attempt, modsDirectory, stillFailed, warnings, ct)
                .ConfigureAwait(false);

            if (ok) recovered++;
        }

        output?.Report(recovered == 0
            ? "重试仍未成功，请按报告里的链接手动下载。"
            : $"已补下 {recovered}/{pending.Count} 个文件。");

        await WriteInstallReportAsync(gameDirectory, "(重试后)", pending.Count, recovered,
            stillFailed.ToArray()).ConfigureAwait(false);

        return recovered;
    }

    /// <summary>
    /// 把安装结果写到 &lt;gameDir&gt;/.orap-install-report.json：
    /// 下次"为什么缺模组"可以直接看这个文件，不用再猜。
    /// </summary>
    private static async Task WriteInstallReportAsync(
        string gameDirectory, string packName, int total, int downloaded, IReadOnlyList<string> unresolved)
    {
        try
        {
            await Orion.Core.Util.JsonUtil.WriteFileAsync(Path.Combine(gameDirectory, ".orap-install-report.json"), new
            {
                generatedAt = DateTimeOffset.Now,
                pack = packName,
                totalFiles = total,
                downloaded,
                failed = unresolved.Count,
                failures = unresolved,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 报告写不出来不影响安装
        }
    }

    /// <summary>拷贝流的同时把字节数报给 TransferMeter（界面上的实时下载速度来自这里）。</summary>
    private static async Task CopyWithMeterAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            Orion.Core.Net.TransferMeter.Report(read);
        }
    }

    /// <summary>删除下载失败留下的半截文件（失败也无所谓）。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就让后续的校验兜底
        }
    }

    /// <summary>
    /// 安装收尾自检：把 mods 里打不开的 jar 删掉并记进 warnings。
    /// 这样"下载被截断/残留 JSON"最多是少一个模组，而不是启动时 zip END header not found。
    /// </summary>
    private static int PruneBrokenJars(string modsDirectory, List<string> warnings)
    {
        if (!Directory.Exists(modsDirectory)) return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            if (LooksLikeZip(file)) continue;

            TryDeleteFile(file);
            removed++;
            warnings.Add($"已移除损坏的模组文件：{Path.GetFileName(file)}（下载不完整，已重新排队时可再装）");
        }

        return removed;
    }

    /// <summary>实例加载器 → CurseForge 的 modLoaderType（搜索筛选用）。</summary>
    private static int? LoaderKindOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null,
    };
    /// <summary>调用 CurseForge 的 download-url 接口，取出真实下载地址。</summary>
    private async Task<string?> ResolveCurseForgeUrlAsync(string apiUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        if (!string.IsNullOrWhiteSpace(_curseForgeApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _curseForgeApiKey);
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return data.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 不是 JSON：交给调用方换下一个候选
        }

        return null;
    }

    /// <summary>文件是不是真正的 zip/jar（PK 头 + 能打开中央目录）。</summary>
    private static bool LooksLikeZip(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64) return false;   // 更小的不可能是 jar，也不会是合法 zip

            using (var stream = File.OpenRead(path))
            {
                var header = new byte[4];
                if (stream.Read(header, 0, 4) < 4) return false;
                if (header[0] != 0x50 || header[1] != 0x4B) return false;
            }

            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> DownloadCurseForgeFileAsync(
        CurseForgeFile file,
        string modsDirectory,
        System.Collections.Concurrent.ConcurrentQueue<string> unresolved,
        CancellationToken ct)
    {
        return await DownloadCurseForgeFileCoreAsync(file, modsDirectory, unresolved, warnings: null, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> DownloadCurseForgeFileCoreAsync(
        CurseForgeFile file,
        string modsDirectory,
        System.Collections.Concurrent.ConcurrentQueue<string> unresolved,
        List<string>? warnings,
        CancellationToken ct)
    {
        // 统一走"解析元数据 → 下载 → 校验"的新流程（与整合包安装一致）。
        // 拿不到元数据时才退回"按 manifest 名字拼 CDN"，至少别让文件缺失。
        ResolvedCurseForgeFile? target = null;

        if (!string.IsNullOrWhiteSpace(_curseForgeApiKey))
        {
            try
            {
                var client = new CurseForgeClient(_http, _curseForgeApiKey);
                var meta = await client.GetFileAsync((int)file.ProjectId, (int)file.FileId, ct).ConfigureAwait(false);
                var name = CurseForgeCdn.NormalizeFileName(meta?.FileName);
                if (name is not null)
                {
                    target = new ResolvedCurseForgeFile((int)file.ProjectId, (int)file.FileId, name,
                        meta!.Sha1, meta.Size, meta.DirectUrl);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                AppLog.Warn("packs", "resolve-failed", ("project", file.ProjectId), ("file", file.FileId),
                    ("error", ex.Message));
            }
        }

        target ??= new ResolvedCurseForgeFile((int)file.ProjectId, (int)file.FileId,
            CurseForgeCdn.NormalizeFileName(file.FileName) ?? $"curseforge-{file.ProjectId}-{file.FileId}.jar",
            null, 0, null);

        if (await downloadContentAsync(target, modsDirectory, warnings, ct).ConfigureAwait(false))
        {
            return true;
        }

        unresolved.Enqueue($"{target.FileName}  →  {CurseForgeCdn.BrowserUrl(target.ProjectId, target.FileId)}");
        return false;
    }

    /// <summary>整合包模组下载结果。</summary>
    public sealed record ModDownloadResult(int Downloaded, int UnavailableCount, IReadOnlyList<string> Failed);

    /// <summary>
    /// 下载一个 CurseForge 整合包里清单列出的全部模组文件。
    ///
    /// 步骤完全照 Axolotl 的做法：
    /// 1) 先把每个 (projectID, fileID) 解析成真实文件名 + sha1 + 大小（并发、限流）；
    /// 2) 再按"直链 → ForgeCDN → 官方 download-url"的顺序下载，边下边记速度；
    /// 3) 下完按 sha1/大小校验，不合格就删掉重试下一个源。
    ///
    /// 与旧实现的差别：不猜文件名（不会留下 curseforge-123-456.jar）、不信任半截文件、
    /// 单个文件失败只记进 Failed，不影响其它文件。
    /// </summary>
    public async Task<ModDownloadResult> DownloadPackModsAsync(
        string archivePath,
        string modsDirectory,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modsDirectory);

        var manifest = ReadManifest(archivePath);
        var files = manifest?.Files ?? [];

        if (files.Count == 0) return new ModDownloadResult(0, 0, Array.Empty<string>());

        output?.Report($"解析清单：{files.Count} 个文件…");

        var resolver = new CurseForgeClient(_http, _curseForgeApiKey);
        var resolved = new System.Collections.Concurrent.ConcurrentBag<ResolvedCurseForgeFile>();
        var unavailable = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var resolveGate = new SemaphoreSlim(MaxConcurrentDownloads);

        var resolveJobs = files.Select(async file =>
        {
            await resolveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var meta = await resolver.GetFileAsync((int)file.ProjectId, (int)file.FileId, ct).ConfigureAwait(false);
                var name = CurseForgeCdn.NormalizeFileName(meta?.FileName ?? file.FileName);

                if (name is null)
                {
                    unavailable.Enqueue($"{file.ProjectId}/{file.FileId}");
                    return;
                }

                resolved.Add(new ResolvedCurseForgeFile((int)file.ProjectId, (int)file.FileId, name,
                    meta?.Sha1, meta?.Size ?? 0, meta?.DirectUrl));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                unavailable.Enqueue($"{file.ProjectId}/{file.FileId}");
            }
            finally
            {
                resolveGate.Release();
            }
        });

        await Task.WhenAll(resolveJobs).ConfigureAwait(false);

        var targets = resolved.ToList();
        output?.Report($"清单解析完成：{targets.Count} 个可下载、{unavailable.Count} 个拿不到元数据。");

        var done = 0;
        var failed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var warnings = new List<string>();
        using var downloadGate = new SemaphoreSlim(MaxConcurrentDownloads);

        var downloadJobs = targets.Select(async target =>
        {
            await downloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await downloadContentAsync(target, modsDirectory, warnings, ct).ConfigureAwait(false))
                {
                    var finished = Interlocked.Increment(ref done);
                    if (finished % 10 == 0) output?.Report($"下载模组 {finished}/{targets.Count}…");
                }
                else
                {
                    failed.Enqueue($"{target.FileName}  →  {CurseForgeCdn.BrowserUrl(target.ProjectId, target.FileId)}");
                }
            }
            finally
            {
                downloadGate.Release();
            }
        });

        await Task.WhenAll(downloadJobs).ConfigureAwait(false);

        return new ModDownloadResult(done, unavailable.Count, failed.ToArray());
    }

    /// <summary>整合包并发下载数：8 个连接足够吃满家用带宽，又不会把 CDN 打出 429。</summary>
    private const int MaxConcurrentDownloads = 8;

    /// <summary>
    /// 下载一个已解析的文件：直链（元数据给的）→ ForgeCDN（mediafilez/edge）→ 官方 download-url。
    /// 下载后按 sha1 / 大小校验，不一致就当失败（半截文件不能留在 mods 里）。
    /// </summary>
    private async Task<bool> downloadContentAsync(
        ResolvedCurseForgeFile target,
        string modsDirectory,
        List<string>? warnings,
        CancellationToken ct)
    {
        var path = Path.Combine(modsDirectory, target.FileName);

        // 已经下过且校验通过就不用再下
        if (target.Matches(new FileInfo(path))) return true;

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(target.DownloadUrl)) candidates.Add(target.DownloadUrl!);
        foreach (var cdn in CurseForgeCdn.Urls(target.FileId, target.FileName)) candidates.Add(cdn);
        if (!string.IsNullOrWhiteSpace(_curseForgeApiKey))
        {
            candidates.Add(
                $"https://api.curseforge.com/v1/mods/{target.ProjectId}/files/{target.FileId}/download-url");
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var perFile = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perFile.CancelAfter(TimeSpan.FromMinutes(5));

                var url = candidate;
                if (url.Contains("/download-url", StringComparison.OrdinalIgnoreCase))
                {
                    url = await ResolveCurseForgeUrlAsync(url, perFile.Token).ConfigureAwait(false) ?? "";
                    if (string.IsNullOrWhiteSpace(url)) continue;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (url.StartsWith("https://api.curseforge.com", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(_curseForgeApiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", _curseForgeApiKey);
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perFile.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                await using (var stream = await response.Content.ReadAsStreamAsync(perFile.Token).ConfigureAwait(false))
                await using (var destination = File.Create(path))
                {
                    await CopyWithMeterAsync(stream, destination, perFile.Token).ConfigureAwait(false);
                }

                var info = new FileInfo(path);
                if (!LooksLikeZip(path) || !target.Matches(info))
                {
                    AppLog.Warn("packs", "file-verify-failed",
                        ("name", target.FileName), ("size", info.Length), ("expected", target.Size),
                        ("source", new Uri(url).Host));
                    TryDeleteFile(path);
                    continue;
                }

                var routed = PackFileClassifier.Route(path, Path.GetDirectoryName(modsDirectory)!, out var kind);
                if (warnings is not null && kind is PackFileKind.ResourcePack or PackFileKind.ShaderPack)
                {
                    warnings.Add($"{Path.GetFileName(routed)} 识别为资源包/光影，已放到对应目录");
                }

                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                TryDeleteFile(path);
            }
        }

        return false;
    }

    /// <summary>
    /// 依次尝试候选地址（CDN 直链 / 官方 download-url / curseforge 跳转），成功返回 true。
    /// 只接受真正的 zip/jar；顺带把资源包和光影分流到对应目录。
    /// </summary>
    private async Task<bool> TryDownloadAsync(
        CurseForgeFile file,
        IReadOnlyList<string> cdnUrls,
        string modsDirectory,
        string name,
        List<string>? warnings,
        CancellationToken ct)
    {
        var target = Path.Combine(modsDirectory, name);

        var candidates = new List<string>(cdnUrls);

        // curseforge 的跳转接口不需要 Key，但对禁用第三方下载的文件同样会 403
        candidates.Add($"https://www.curseforge.com/api/v1/mods/{file.ProjectId}/files/{file.FileId}/download");

        // 带 Key 时官方接口最权威（返回真实 CDN 地址；作者禁用时返回 data=null）
        if (!string.IsNullOrWhiteSpace(_curseForgeApiKey))
        {
            candidates.Insert(0,
                $"https://api.curseforge.com/v1/mods/{file.ProjectId}/files/{file.FileId}/download-url");
        }

        foreach (var url in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var perFile = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // 大整合包的模组能有几十 MB，60s 在慢速 CDN 上会把文件截断（留下打不开的 jar）
                perFile.CancelAfter(TimeSpan.FromSeconds(240));

                var downloadUrl = url;
                if (url.Contains("/download-url", StringComparison.OrdinalIgnoreCase))
                {
                    // 该接口返回 {"data":"真实地址"}：必须解出来再下，否则会把 JSON 当 jar 写进 mods
                    downloadUrl = await ResolveCurseForgeUrlAsync(url, perFile.Token).ConfigureAwait(false) ?? "";
                    if (string.IsNullOrWhiteSpace(downloadUrl)) continue;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                if (downloadUrl.StartsWith("https://api.curseforge.com", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(_curseForgeApiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", _curseForgeApiKey);
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perFile.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) continue;

                await using (var stream = await response.Content.ReadAsStreamAsync(perFile.Token).ConfigureAwait(false))
                await using (var destination = File.Create(target))
                {
                    await CopyWithMeterAsync(stream, destination, perFile.Token).ConfigureAwait(false);
                }

                // 只认真正的 jar/zip：小 JSON、HTML 错误页、截断文件一律丢弃并换下一个源
                if (!LooksLikeZip(target))
                {
                    TryDeleteFile(target);
                    continue;
                }

                // 资源包 / 光影不能留在 mods（NeoForge 会判定 "not a valid mod file" 并让加载失败）
                var routed = PackFileClassifier.Route(target, Path.GetDirectoryName(modsDirectory)!, out var kind);
                if (warnings is not null && kind is PackFileKind.ResourcePack or PackFileKind.ShaderPack)
                {
                    warnings.Add($"{Path.GetFileName(routed)} 识别为资源包/光影，已放到对应目录");
                }

                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // 半截文件（超时/断流）必须删掉，否则会留在 mods 里让游戏启动失败
                TryDeleteFile(target);
            }
        }

        return false;
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
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
    }
}
