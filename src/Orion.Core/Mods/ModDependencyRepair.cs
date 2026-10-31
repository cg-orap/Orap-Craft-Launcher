using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Orion.Core.Mods;

/// <summary>补齐依赖的结果。</summary>
public sealed record DependencyRepairResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Failed,
    IReadOnlyList<RequiredDependency> Remaining);

/// <summary>
/// 自动补前置：拿扫描出来的"缺失依赖"去 CurseForge 找、下载、校验，再放进 mods。
///
/// 难点在于 CurseForge 的 API 只认 projectId，不提供 modId → 项目的映射，所以这里的做法是：
/// 按 modId 猜项目名搜索 → 对候选项目取匹配版本的文件 → 下到临时文件 → 读 jar 里的 mods.toml
/// 核对 modId 和版本范围 → 对上了才移进 mods。
/// 也就是说"下载并验证"是判据，不靠猜名字。
/// </summary>
public sealed class ModDependencyRepair(CurseForgeClient client, HttpClient http)
{
    /// <summary>最多为每个依赖尝试多少个候选项目（越多越慢，5 个足够覆盖同名项目）。</summary>
    private const int MaxCandidates = 5;

    /// <summary>一次最多补多少个依赖，避免一个坏包把整台机器拖满。</summary>
    private const int MaxRepairs = 20;

    /// <summary>
    /// 少量"模块名不等于项目名"的已知情况：Create Aeronautics 把 aeronautics / simulated / offroad
    /// 三个 modId 打进同一个 bundled jar，光按名字搜是搜不到宿主项目的。
    /// 这里只作为提示，下载后仍要读 jar（含 JarJar 内嵌）核对 modId，填错也不会装错东西。
    /// </summary>
    private static readonly Dictionary<string, int> KnownBundledProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aeronautics"] = 676721,
        ["aeronautics_bundled"] = 676721,
        ["simulated"] = 676721,
        ["offroad"] = 676721,
    };

    /// <summary>整体超时：再慢也不该让界面上的"补齐前置"卡到用户以为按钮坏了。</summary>
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(3);

    public async Task<DependencyRepairResult> RepairAsync(
        string gameDirectory,
        IReadOnlyList<RequiredDependency> missing,
        string? gameVersion,
        int? modLoaderType,
        string cacheDirectory,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RepairTimeout);
        ct = timeout.Token;

        var modsDirectory = Path.Combine(gameDirectory, "mods");
        Orion.Core.Diagnostics.AppLog.Info("deps", "repair-start",
            ("dir", gameDirectory), ("missing", missing.Count),
            ("gameVersion", gameVersion ?? "(null)"), ("loader", modLoaderType?.ToString() ?? "(null)"));
        Directory.CreateDirectory(modsDirectory);
        Directory.CreateDirectory(cacheDirectory);

        var installed = new List<string>();
        var failed = new List<string>();

        // 同一个 modId 可能被多个模组依赖，只补一次
        var targets = missing
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(MaxRepairs)
            .ToList();

        foreach (var dependency in targets)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report($"正在查找前置 {dependency.ModId}（被 {dependency.RequiredBy} 需要）…");

            try
            {
                var installedPath = await TryInstallOneAsync(dependency, modsDirectory, cacheDirectory,
                    gameVersion, modLoaderType, progress, ct).ConfigureAwait(false);

                if (installedPath is null)
                {
                    failed.Add($"{dependency.ModId}（被 {dependency.RequiredBy} 需要，版本 {Describe(dependency.VersionRange)}）");
                    Orion.Core.Diagnostics.AppLog.Warn("deps", "repair-no-candidate",
                        ("modId", dependency.ModId), ("requiredBy", dependency.RequiredBy),
                        ("range", dependency.VersionRange));
                    continue;
                }

                installed.Add($"{Path.GetFileName(installedPath)} → 补齐 {dependency.ModId}");
                progress?.Report($"已补齐前置 {dependency.ModId}：{Path.GetFileName(installedPath)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                           or TaskCanceledException)
            {
                failed.Add($"{dependency.ModId}：{ex.Message}");
                Orion.Core.Diagnostics.AppLog.Warn("deps", "repair-one-failed",
                    ("modId", dependency.ModId), ("error", ex.Message));
            }
        }

        // 补完再扫一遍，剩下的就是真补不上的
        var rescan = ModDependencyScanner.Scan(modsDirectory);
        return new DependencyRepairResult(installed, failed, rescan.Missing);
    }

    /// <summary>
    /// 候选项目：CurseForge 没有 modId → 项目 的映射，只能换几种关键词搜同一批结果再逐个核对。
    /// aeronautics_bundled 这种"某个发行版里的模块"名字，靠截断后缀往往能搜到宿主项目。
    /// </summary>
    /// <summary>
    /// jar 里声明的版本是否是"没被替换的占位符"（Forge 的 ${file.jarVersion} 之类）。
    /// 这类值不能拿去比版本范围。
    /// </summary>
    internal static bool IsUnresolvedVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// modId 是否算"同一个东西"：完全一致，或只差结尾数字（geckolib 与 geckolib3）。
    /// 只做这么窄的放宽——避免把无关模组当成前置装进去。
    /// </summary>
    internal static bool SameMod(string jarModId, string wantedModId)
    {
        if (string.Equals(jarModId, wantedModId, StringComparison.OrdinalIgnoreCase)) return true;

        static string StripDigits(string value) => System.Text.RegularExpressions.Regex.Replace(value, "\\d+$", "");

        return string.Equals(StripDigits(jarModId), StripDigits(wantedModId), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 已知"modId 与 CurseForge slug 不同"的对照表。
    /// 这几个人气模组的 slug 和 modId 差得比较远，光靠规则推不出来。
    /// </summary>
    private static readonly Dictionary<string, string> KnownSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tconstruct"] = "tinkers-construct",
        ["ftblibrary"] = "ftb-library-forge",
        ["farmersdelight"] = "farmers-delight",
        ["architectury"] = "architectury-api",
        ["geckolib3"] = "geckolib",
        ["citadel"] = "citadel",
        ["citadel2"] = "citadel",
        ["alexsmobs"] = "alexs-mobs",
        ["apotheosis"] = "apotheosis",
        ["placebo"] = "placebo",
        ["attributefix"] = "attributefix",
        ["bookshelf"] = "bookshelf-lib",
        ["corgilib"] = "corgilib",
    };

    /// <summary>
    /// modId → 可能的 CurseForge slug：原名、去掉结尾数字（geckolib3 → geckolib）、
    /// 下划线换连字符、去掉常见后缀。
    /// </summary>
    internal static IReadOnlyList<string> SlugCandidates(string modId)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (trimmed.Length < 2) return;
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) candidates.Add(trimmed);
        }

        if (KnownSlugs.TryGetValue(modId, out var known)) Add(known);

        Add(modId);
        Add(modId.Replace('_', '-'));
        Add(System.Text.RegularExpressions.Regex.Replace(modId, "\\d+$", ""));           // geckolib3 → geckolib
        Add(System.Text.RegularExpressions.Regex.Replace(modId, "^(lib|mod)[-_]?", "", RegexOptions.IgnoreCase)); // libx → x
        Add(modId.Replace("_library", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("_api", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("_core", "", StringComparison.OrdinalIgnoreCase));

        return candidates;
    }

    private async Task<IReadOnlyList<CurseForgePack>> SearchCandidatesAsync(
        string modId, string? gameVersion, CancellationToken ct)
    {
        var queries = new List<string> { modId.Replace('_', ' ') };

        if (modId.Contains('_', StringComparison.Ordinal))
        {
            var head = modId.Split('_')[0];
            if (head.Length >= 3 && !queries.Contains(head, StringComparer.OrdinalIgnoreCase)) queries.Add(head);

            var trimmed = modId.Replace("_bundled", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("_lib", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace("_core", "", StringComparison.OrdinalIgnoreCase)
                                 .Replace('_', ' ');
            if (trimmed.Length >= 3 && !queries.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(trimmed);
            }
        }

        var seen = new HashSet<int>();
        var result = new List<CurseForgePack>();

        // 先用 slug 精确匹配（CurseForge 支持 slug= 精确查询）。
        // 关键词搜索对 patchouli / curios / geckolib3 / titanium 这些名字基本没用：
        // 要么被同名小模组淹没，要么 slug 与 modId 不同（geckolib3 → geckolib）。
        foreach (var slug in SlugCandidates(modId))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var id = await client.FindProjectIdBySlugAsync(slug, ct).ConfigureAwait(false);
                if (id is null || !seen.Add(id.Value)) continue;

                var pack = await client.GetPackAsync(id.Value, ct).ConfigureAwait(false);
                if (pack is not null) result.Add(pack);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 单个 slug 失败不影响后面的模糊搜索
            }
        }

        // 先放已知的宿主项目，命中就不用搜索
        if (KnownBundledProjects.TryGetValue(modId, out var knownId))
        {
            try
            {
                var known = await client.GetPackAsync(knownId, ct).ConfigureAwait(false);
                if (known is not null)
                {
                    seen.Add(known.Id);
                    result.Add(known);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 忽略，继续走搜索
            }
        }

        foreach (var query in queries)
        {
            IReadOnlyList<CurseForgePack> hits;
            try
            {
                hits = await client.SearchAsync(CurseForgeKind.Mod, query, gameVersion, MaxCandidates, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                continue;
            }

            foreach (var hit in hits)
            {
                if (seen.Add(hit.Id)) result.Add(hit);
            }

            if (result.Count >= MaxCandidates * 2) break;
        }

        return result;
    }
    private async Task<string?> TryInstallOneAsync(
        RequiredDependency dependency,
        string modsDirectory,
        string cacheDirectory,
        string? gameVersion,
        int? modLoaderType,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var candidates = await SearchCandidatesAsync(dependency.ModId, gameVersion, ct).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var file = await client
                .GetBestFileAsync(candidate, gameVersion, modLoaderType, CurseForgeKind.Mod, ct)
                .ConfigureAwait(false);

            if (file is null) continue;

            progress?.Report($"尝试 {candidate.Name} → {file.FileName}…");
            Orion.Core.Diagnostics.AppLog.Info("deps", "candidate-file",
                ("modId", dependency.ModId), ("project", candidate.Id), ("file", file.FileName),
                ("gameVersions", string.Join('|', file.GameVersions)));

            var temporary = Path.Combine(cacheDirectory, "dep-" + Guid.NewGuid().ToString("N") + ".jar");
            try
            {
                try
                {
                    await DownloadAsync(file, temporary, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    progress?.Report($"{candidate.Name} 下载失败（{ex.Message}），换下一个候选…");
                    Orion.Core.Diagnostics.AppLog.Warn("deps", "download-failed",
                        ("modId", dependency.ModId), ("file", file.FileName), ("error", ex.Message));
                    continue;
                }

                var info = ReadModId(temporary, dependency.ModId);
                if (info is null)
                {
                    Orion.Core.Diagnostics.AppLog.Warn("deps", "jar-no-modid",
                        ("modId", dependency.ModId), ("file", file.FileName));
                    continue;
                }

                var (modId, version) = info.Value;
                if (!SameMod(modId, dependency.ModId))
                {
                    Orion.Core.Diagnostics.AppLog.Warn("deps", "jar-modid-mismatch",
                        ("wanted", dependency.ModId), ("got", modId), ("file", file.FileName));
                    continue;
                }

                // 很多 Forge 模组的 mods.toml 里 version 是没被替换的占位符（例如 "${file.jarVersion}"），
                // 拿它去比版本范围必然失败，于是"正确的文件"被当成不合格丢掉。
                // 这种情况跳过版本范围检查：版本/加载器/modId 已经在前面核对过了。
                if (!IsUnresolvedVersion(version)
                    && !string.IsNullOrWhiteSpace(dependency.VersionRange)
                    && !VersionRange.Satisfies(version, dependency.VersionRange))
                {
                    progress?.Report($"{candidate.Name} 版本 {version} 不满足 {dependency.VersionRange}，继续找…");
                    Orion.Core.Diagnostics.AppLog.Warn("deps", "jar-version-rejected",
                        ("modId", dependency.ModId), ("version", version), ("range", dependency.VersionRange));
                    continue;
                }

                var target = Path.Combine(modsDirectory,
                    string.IsNullOrWhiteSpace(file.FileName) ? $"{modId}.jar" : file.FileName);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);

                // 之前补错过版本的旧 jar（例如 Iceberg-26.3 塞进 1.19.2）会让 Forge 报重复 mod，
                // 装好正确的这份之后把它们挪到隔离目录。
                try
                {
                    var stale = StaleModFiles.SelectStale(
                        Directory.GetFiles(modsDirectory, "*.jar"), dependency.ModId, target);

                    foreach (var old in stale)
                    {
                        StaleModFiles.Quarantine(gameDirectory: Path.GetDirectoryName(modsDirectory) ?? modsDirectory,
                            old, DateTimeOffset.Now);
                        progress?.Report($"隔离了版本不对的旧文件：{Path.GetFileName(old)}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    progress?.Report("清理旧副本失败（不影响本次安装）：" + ex.Message);
                }

                return target;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch (IOException) { }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 下载一个文件：先问官方 download-url，拿不到（作者禁止第三方下载时会 403）就走 ForgeCDN 直链。
    /// 直链的规则是 files/{fileId/1000}/{fileId%1000}/{fileName}，两个 CDN 域名都试。
    /// </summary>
    private async Task DownloadAsync(CurseForgeFile file, string target, CancellationToken ct)
    {
        var candidates = new List<string>();

        try
        {
            var official = await client.GetDownloadUrlAsync(file, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(official)) candidates.Add(official);
        }
        catch (HttpRequestException)
        {
            // 403/404：继续用直链兜底
        }

        if (!string.IsNullOrWhiteSpace(file.FileName))
        {
            var bucket = file.FileId / 1000;
            var rest = file.FileId % 1000;
            var name = Uri.EscapeDataString(file.FileName);
            candidates.Add($"https://edge.forgecdn.net/files/{bucket}/{rest}/{name}");
            candidates.Add($"https://mediafilez.forgecdn.net/files/{bucket}/{rest}/{name}");
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("CurseForge 没有返回下载地址");
        }

        Exception? last = null;
        foreach (var url in candidates)
        {
            try
            {
                using var response = await http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode}（{url}）");
                    continue;
                }

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("所有下载地址都失败了");
    }

    /// <summary>读 jar 里的 modId 与 version（取第一个 [[mods]] 块）。</summary>
    /// <summary>列出 jar 里所有 modId（外层 + JarJar 内嵌），诊断用。</summary>
    public static IReadOnlyList<(string ModId, string Version)> ReadAllModIds(string path)
    {
        var ids = new List<(string, string)>();
        try
        {
            using var zip = ZipFile.OpenRead(path);

            var toml = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));
            if (toml is not null)
            {
                using var reader = new StreamReader(toml.Open());
                var info = ModDependencyScanner.ParseToml(reader.ReadToEnd());
                if (info is not null) ids.AddRange(info.Mods);
            }

            foreach (var nested in zip.Entries.Where(e =>
                         e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)
                         && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = nested.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    using var nestedZip = new ZipArchive(buffer, ZipArchiveMode.Read);
                    var nestedToml = nestedZip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                        ?? nestedZip.Entries.FirstOrDefault(e =>
                            e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));
                    if (nestedToml is null) continue;
                    using var nestedReader = new StreamReader(nestedToml.Open());
                    var nestedInfo = ModDependencyScanner.ParseToml(nestedReader.ReadToEnd());
                    if (nestedInfo is not null) ids.AddRange(nestedInfo.Mods);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }

        return ids;
    }

    public static (string ModId, string Version)? ReadModId(string path, string? wantedModId = null)
    {
        (string ModId, string Version)? yieldCandidate = null;

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var toml = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));

            if (toml is not null)
            {
                using var reader = new StreamReader(toml.Open());
                var info = ModDependencyScanner.ParseToml(reader.ReadToEnd());
                if (info is not null && info.Mods.Count > 0)
                {
                    // 外层 modId 可能就是我们要的
                    yieldCandidate = (info.Mods[0].Id, info.Mods[0].Version);
                    if (info.Mods[0].Id.Equals(wantedModId, StringComparison.OrdinalIgnoreCase))
                    {
                        return yieldCandidate;
                    }
                }
            }

            // 再看 JarJar 内嵌的 jar：Create Aeronautics 这类 bundled 发行版的 simulated / offroad 就在里面
            foreach (var nested in zip.Entries.Where(e => e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)
                                                          && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var nestedStream = nested.Open();
                    using var buffer = new MemoryStream();
                    nestedStream.CopyTo(buffer);
                    buffer.Position = 0;

                    using var nestedZip = new ZipArchive(buffer, ZipArchiveMode.Read);
                    var nestedToml = nestedZip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                        ?? nestedZip.Entries.FirstOrDefault(e =>
                            e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));
                    if (nestedToml is null) continue;

                    using var nestedReader = new StreamReader(nestedToml.Open());
                    var nestedInfo = ModDependencyScanner.ParseToml(nestedReader.ReadToEnd());
                    if (nestedInfo is null || nestedInfo.Mods.Count == 0) continue;

                    if (nestedInfo.Mods[0].Id.Equals(wantedModId, StringComparison.OrdinalIgnoreCase))
                    {
                        return (nestedInfo.Mods[0].Id, nestedInfo.Mods[0].Version);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // 内嵌 jar 坏了就跳过
                }
            }

            return yieldCandidate;

            var fabric = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
            if (fabric is null) return yieldCandidate;

            using var fabricReader = new StreamReader(fabric.Open());
            var json = fabricReader.ReadToEnd();
            var id = Regex.Match(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var version = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            if (!id.Success) return null;

            return (id.Groups[1].Value, version.Success ? version.Groups[1].Value : "0");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(string range) => string.IsNullOrWhiteSpace(range) ? "任意版本" : range;
}
