using System.IO.Compression;
using Orion.Core.Instances;

namespace Orion.Core.Mods;

/// <summary>冲突的严重程度：Error 一定要处理，Warning 只提示可能有问题。</summary>
public enum ConflictSeverity
{
    Error,
    Warning,
}

/// <summary>一条冲突：涉及哪些文件、为什么、怎么处理。</summary>
public sealed record ModConflict(
    string Code,
    ConflictSeverity Severity,
    string Title,
    IReadOnlyList<string> Files,
    string Detail)
{
    /// <summary>给界面直接用的"要不要拦一下"判断。</summary>
    public bool IsBlocking => Severity == ConflictSeverity.Error;
}

/// <summary>
/// 把 mods 目录当成一个整体来看：重复 modId、同名 jar、加载器不匹配、版本范围互斥、
/// 声明了 incompatible 却装着、共享 mixin 配置、核心库版本错配。
///
/// 设计原则：**宁可漏报也不要误报**。C1–C5 由文件内容直接判定（Error），
/// C6/C7 只能是"可能"（Warning），因为同名 mixin 配置、核心库范围不重叠本身不必然崩游戏。
/// </summary>
public static class ModConflictScanner
{
    /// <summary>永远忽略的内置 id（Forge/NeoForge/Fabric 自己，以及原版）。</summary>
    private static readonly HashSet<string> BuiltinIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fml", "java", "fabricloader", "fabric-api", "fabric", "quilt_loader",
        "cloth-config", "cloth_config",
    };

    /// <summary>被多个模组共同依赖、版本错配概率最高的库（C7 只查这些，避免刷屏）。</summary>
    private static readonly string[] CoreLibraries =
    [
        "kotlinforforge", "architectury", "geckolib", "curios", "cloth_config", "cloth-config",
        "create", "flywheel", "moonlight", "resourcefullib", "bookshelf",
    ];

    public static IReadOnlyList<ModConflict> Scan(string modsDirectory, LoaderKind loader, string? gameVersion)
    {
        if (!Directory.Exists(modsDirectory)) return [];

        var jars = new List<JarSnapshot>();

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            var snapshot = ReadJar(path);
            if (snapshot is not null) jars.Add(snapshot);
        }

        if (jars.Count == 0) return [];

        var conflicts = new List<ModConflict>();

        AppendDuplicateModIds(jars, conflicts);
        AppendDuplicateFileNames(jars, conflicts);
        AppendLoaderMismatch(jars, loader, conflicts);
        AppendVersionRangeConflicts(jars, conflicts);
        AppendIncompatible(jars, conflicts);
        AppendSharedMixins(jars, conflicts);
        AppendCoreLibraryMismatch(jars, conflicts);

        return [.. conflicts
            .Select(c => c with { Files = [.. c.Files.Distinct(StringComparer.OrdinalIgnoreCase)] })
            .OrderBy(c => c.Severity)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .ThenBy(c => c.Title, StringComparer.Ordinal)];
    }

    // ── C1 重复 modId ──────────────────────────────────────────────────────────
    private static void AppendDuplicateModIds(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        var owners = new Dictionary<string, List<(string File, string? Version)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in jars)
        {
            foreach (var (id, version) in jar.OuterMods)
            {
                if (BuiltinIds.Contains(id) || IsPlaceholderVersion(version)) ;
                if (BuiltinIds.Contains(id)) continue;
                if (!owners.TryGetValue(id, out var list))
                {
                    owners[id] = list = [];
                }

                // 同一个 jar 内重复不算冲突；内嵌 jar 之间的重复是 JarJar 的正常行为，也不报
                if (list.Any(x => x.File.Equals(jar.FileName, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add((jar.FileName, version));
            }
        }

        foreach (var (id, list) in owners.Where(p => p.Value.Count > 1))
        {
            var files = list.Select(x => x.File).ToList();
            var versions = list.Select(x => x.Version ?? "?").ToList();

            conflicts.Add(new ModConflict(
                "C1",
                ConflictSeverity.Error,
                $"重复的 modId：{id}",
                files,
                $"同一个 modId 被 {files.Count} 个文件提供（版本：{string.Join("、", versions)}）。" +
                "游戏只会加载其中一个，另一个的配方/物品会消失或直接崩溃。建议只保留一个。"));
        }
    }

    // ── C2 同名不同版本 ────────────────────────────────────────────────────────
    private static void AppendDuplicateFileNames(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        var groups = jars
            .GroupBy(j => BaseName(j.FileName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var files = group.Select(j => j.FileName).ToList();

            // C1 已经报过的就不重复报（同一组 modId 多半也同名）
            if (conflicts.Any(c => c.Code == "C1" && c.Files.Any(files.Contains))) continue;

            conflicts.Add(new ModConflict(
                "C2",
                ConflictSeverity.Error,
                $"同名 jar 存在多个版本：{group.Key}",
                files,
                $"文件名去版本号后相同（{string.Join("、", files)}）。" +
                "通常是手动升版本时忘了删旧的，两个都会被加载。建议进回收站删掉旧的那份。"));
        }
    }

    // ── C3 加载器不匹配 ────────────────────────────────────────────────────────
    private static void AppendLoaderMismatch(List<JarSnapshot> jars, LoaderKind loader, List<ModConflict> conflicts)
    {
        var forgeFamily = loader is LoaderKind.Forge or LoaderKind.NeoForge;
        var fabricFamily = loader is LoaderKind.Fabric or LoaderKind.Quilt;

        if (!forgeFamily && !fabricFamily) return;

        foreach (var jar in jars)
        {
            if (forgeFamily && jar.HasFabricManifest && !jar.HasForgeManifest)
            {
                conflicts.Add(new ModConflict(
                    "C3",
                    ConflictSeverity.Error,
                    $"Fabric 模组装进了 {loader} 实例：{jar.FileName}",
                    [jar.FileName],
                    "这个 jar 只有 fabric.mod.json、没有 mods.toml，Forge/NeoForge 不会加载它。" +
                    "要么删掉，要么用 Sinytra Connector 之类的桥接（桥接本身也可能引发兼容问题）。"));
            }
            else if (fabricFamily && jar.HasForgeManifest && !jar.HasFabricManifest)
            {
                conflicts.Add(new ModConflict(
                    "C3",
                    ConflictSeverity.Error,
                    $"Forge/NeoForge 模组装进了 {loader} 实例：{jar.FileName}",
                    [jar.FileName],
                    "这个 jar 只有 mods.toml、没有 fabric.mod.json，Fabric/Quilt 不会加载它。"));
            }
        }
    }

    // ── C4 版本范围互斥 / 装低了 ───────────────────────────────────────────────
    private static void AppendVersionRangeConflicts(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        var installed = new Dictionary<string, (string File, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var jar in jars)
        {
            foreach (var (id, version) in jar.AllMods)
            {
                if (BuiltinIds.Contains(id) || string.IsNullOrWhiteSpace(version)) continue;
                if (IsPlaceholderVersion(version)) continue;
                installed.TryAdd(id, (jar.FileName, version));
            }
        }

        foreach (var jar in jars)
        {
            foreach (var (id, range, mandatory) in jar.Dependencies)
            {
                if (!mandatory || BuiltinIds.Contains(id) || string.IsNullOrWhiteSpace(range)) continue;

                // 只认 Maven 显式区间：裸版本号（"1.21-1.3"）在 Forge 里是"建议版本"，
                // 真实加载并不强制，报出来会变成误报。
                var trimmedRange = range.Trim();
                if (!trimmedRange.StartsWith('[') && !trimmedRange.StartsWith('(')) continue;
                if (trimmedRange is "[,)" or "(*,)") continue;
                if (!installed.TryGetValue(id, out var have)) continue;         // 缺前置由依赖扫描负责
                if (VersionRange.Satisfies(have.Version, range)) continue;

                conflicts.Add(new ModConflict(
                    "C4",
                    ConflictSeverity.Warning,
                    $"版本不满足：{jar.FileName} 需要 {id} {range}",
                    [jar.FileName, have.File],
                    $"当前装的是 {id} {have.Version}，但 {jar.FileName} 写的是 {range}。" +
                    "整合包里这类版本号常用「MC版本-模组版本」的自定义写法，解析可能有偏差，所以列为建议检查；" +
                    "真出现 missing/incompatible 报错时优先怀疑这一对。"));
            }
        }
    }

    // ── C5 声明了 incompatible ─────────────────────────────────────────────────
    private static void AppendIncompatible(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        var installedIds = new HashSet<string>(
            jars.SelectMany(j => j.AllMods.Select(m => m.Id)), StringComparer.OrdinalIgnoreCase);

        foreach (var jar in jars)
        {
            foreach (var (id, _) in jar.Incompatible)
            {
                if (!installedIds.Contains(id)) continue;

                var other = jars.FirstOrDefault(j =>
                    j.OuterMods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

                conflicts.Add(new ModConflict(
                    "C5",
                    ConflictSeverity.Warning,
                    $"{jar.FileName} 声明与 {id} 不兼容",
                    other is null ? [jar.FileName] : [jar.FileName, other.FileName],
                    $"{jar.FileName} 的 mods.toml 里把 {id} 标成了 incompatible，而它确实装着。" +
                    "NeoForge 对这种声明一般只记日志、不阻断启动（实测这个包能进主菜单），" +
                    "所以列为建议检查；如果确实出现异常行为，优先怀疑这一对。"));
            }
        }
    }

    // ── C6 共享 mixin 配置（Warning，聚合为一条） ─────────────────────────────
    private static void AppendSharedMixins(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in jars)
        {
            foreach (var mixin in jar.MixinConfigs)
            {
                if (!owners.TryGetValue(mixin, out var list)) owners[mixin] = list = [];
                if (!list.Contains(jar.FileName, StringComparer.OrdinalIgnoreCase)) list.Add(jar.FileName);
            }
        }

        var shared = owners.Where(p => p.Value.Count > 1).ToList();
        if (shared.Count == 0) return;

        // 一次说完：多少份配置、涉及多少个 jar。逐个报在 300+ 模组的包里会刷出几百条。
        var files = shared
            .SelectMany(p => p.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var samples = string.Join("、", shared.Select(p => p.Key).Take(3));
        if (shared.Count > 3) samples += $" 等 {shared.Count} 份";

        conflicts.Add(new ModConflict(
            "C6",
            ConflictSeverity.Warning,
            $"{shared.Count} 份 mixin 配置被多个 jar 重复打包",
            [.. files.Take(8)],
            $"例如 {samples}。" +
            "这通常只是同一个前置库被打进了多个模组，本身不冲突；" +
            "只有启动日志里出现 mixin apply failed 时才需要按文件名排查。"));
    }

    // ── C7 核心库版本范围没有交集（Warning，按库聚合） ─────────────────────────
    private static void AppendCoreLibraryMismatch(List<JarSnapshot> jars, List<ModConflict> conflicts)
    {
        foreach (var library in CoreLibraries)
        {
            var requirements = jars
                .SelectMany(j => j.Dependencies
                    .Where(d => d.Mandatory
                                && d.ModId.Equals(library, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(d.Range)
                                && !VersionRange.IsWildcard(d.Range))
                    .Select(d => (j.FileName, d.Range)))
                .Distinct()
                .ToList();

            if (requirements.Count < 2) continue;

            // 只要存在一对互斥就报一次，把涉及的模组一起列出来（不是每对一条）
            var conflictsHere = new List<(string File, string Range)>();
            for (var i = 0; i < requirements.Count; i++)
            {
                for (var j = i + 1; j < requirements.Count; j++)
                {
                    if (VersionRange.Overlaps(requirements[i].Range, requirements[j].Range)) continue;
                    if (!conflictsHere.Contains(requirements[i])) conflictsHere.Add(requirements[i]);
                    if (!conflictsHere.Contains(requirements[j])) conflictsHere.Add(requirements[j]);
                }
            }

            if (conflictsHere.Count == 0) continue;

            var ranges = string.Join(" 与 ", conflictsHere.Take(3).Select(r => $"{r.File} 要 {r.Range}"));
            if (conflictsHere.Count > 3) ranges += $" 等 {conflictsHere.Count} 个模组";

            conflicts.Add(new ModConflict(
                "C7",
                ConflictSeverity.Warning,
                $"要求的 {library} 版本范围互相矛盾",
                [.. conflictsHere.Select(r => r.File).Take(6)],
                $"{ranges}。无论装哪个版本，总有一个模组不满足要求，" +
                "通常表现为某个模组功能异常或加载时报版本不符。"));
        }
    }

    // ── jar 读取 ──────────────────────────────────────────────────────────────
    private sealed record JarSnapshot(
        string FileName,
        IReadOnlyList<(string Id, string? Version)> OuterMods,
        IReadOnlyList<(string Id, string? Version)> NestedMods,
        IReadOnlyList<(string ModId, string Range, bool Mandatory)> Dependencies,
        IReadOnlyList<(string Id, string Range)> Incompatible,
        bool HasForgeManifest,
        bool HasFabricManifest,
        IReadOnlyList<string> MixinConfigs)
    {
        /// <summary>外层 + 内嵌，用来判断"某个 modId 是否存在"。</summary>
        public IEnumerable<(string Id, string? Version)> AllMods => OuterMods.Concat(NestedMods);
    }

    private static JarSnapshot? ReadJar(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var fileName = Path.GetFileName(path);

            var toml = zip.Entries.FirstOrDefault(e =>
                           e.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase))
                       ?? zip.Entries.FirstOrDefault(e =>
                           e.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));

            var hasFabric = zip.Entries.Any(e =>
                e.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
            var hasForge = toml is not null;

            string tomlText = "";
            if (toml is not null)
            {
                using var reader = new StreamReader(toml.Open());
                tomlText = reader.ReadToEnd();
            }

            var outerMods = new List<(string, string?)>();
            var dependencies = new List<(string, string, bool)>();
            var incompatible = new List<(string, string)>();

            if (tomlText.Length > 0)
            {
                var info = ModDependencyScanner.ParseToml(tomlText);
                if (info is not null)
                {
                    foreach (var (id, version) in info.Mods) outerMods.Add((id, version));
                    foreach (var (id, range, mandatory) in info.Dependencies) dependencies.Add((id, range, mandatory));
                }

                incompatible.AddRange(ReadIncompatible(tomlText));
            }
            else if (hasFabric)
            {
                var fabricEntry = zip.Entries.First(e =>
                    e.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
                using var reader = new StreamReader(fabricEntry.Open());
                var json = reader.ReadToEnd();
                var id = System.Text.RegularExpressions.Regex.Match(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                var version = System.Text.RegularExpressions.Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (id.Success) outerMods.Add((id.Groups[1].Value, version.Success ? version.Groups[1].Value : null));
            }

            // 这里**不解 JarJar 内嵌 jar**：376 个模组里解一遍内嵌要几秒钟，而内嵌模块只影响
            // "某个依赖是否已安装"的判断——那属于 ModDependencyScanner 的职责（它仍然读内嵌）。
            // 冲突检测只看外层 modId 就够，删掉这一步把页面从 ~3s 降到亚秒级。
            var nestedMods = new List<(string, string?)>();
            var nestedMixins = new List<string>();

            if (outerMods.Count == 0 && nestedMods.Count == 0 && !hasFabric) return null;

            // 内嵌 jar 的 mixin 只用于"这不冲突"的判断，不进 C6；外层依赖才参与 C4/C7
            _ = nestedMixins;

            return new JarSnapshot(fileName, outerMods, nestedMods, dependencies, incompatible,
                hasForge, hasFabric, MixinConfigNames(zip));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>zip 里所有 <c>*.mixins.json</c> / <c>*.mixin.json</c> 的文件名（只取名，不解压）。</summary>
    private static List<string> MixinConfigNames(ZipArchive zip) =>
        [.. zip.Entries
            .Select(e => e.FullName)
            .Where(name => name.EndsWith(".mixins.json", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".mixin.json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>只挑 <c>type = "incompatible"</c> 的依赖块（ParseToml 把它们当可选，这里单独取）。</summary>
    private static IEnumerable<(string Id, string Range)> ReadIncompatible(string toml)
    {
        var results = new List<(string, string)>();
        var inDependencies = false;
        string? pendingId = null;
        var pendingRange = "";
        var pendingIsIncompatible = false;

        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                Finish();
                var section = line[2..^2].Trim().Trim('"', '\'');
                inDependencies = section.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inDependencies) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim().ToLowerInvariant();
            var value = line[(equals + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "modid":
                    Finish();
                    pendingId = value;
                    break;
                case "versionrange":
                    pendingRange = value;
                    break;
                case "type":
                    pendingIsIncompatible = value.Contains("incompatible", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        Finish();
        return results;

        void Finish()
        {
            if (pendingId is not null && pendingIsIncompatible) results.Add((pendingId, pendingRange));
            pendingId = null;
            pendingRange = "";
            pendingIsIncompatible = false;
        }
    }

    /// <summary>mods.toml 里没被替换的占位符（${file.jarVersion}）不能当版本比较。</summary>
    internal static bool IsPlaceholderVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && (version.Contains('$') || version.Contains('{') || version.Contains('}'));

    /// <summary>
    /// 去掉版本号，用来识别"同一模组的不同版本文件"。
    ///
    /// 只把 <c>1.0.0</c> 这种带小数点的段当版本：CurseForge 的 CDN 文件名
    /// <c>curseforge-1477581-8004324.jar</c> 是"项目ID-文件ID"，去掉数字后全部变成 "curseforge"，
    /// 会把几百个互不相干的文件误判成同名。
    /// </summary>
    internal static string BaseName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (CurseForgeCdnName.IsMatch(stem)) return stem;

        var match = System.Text.RegularExpressions.Regex.Match(
            stem, @"^(.+?)[-_ ]v?\d+(\.\d+)+.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.TrimEnd('-', '_', ' ', '.') : stem;
    }

    private static readonly System.Text.RegularExpressions.Regex CurseForgeCdnName =
        new(@"^curseforge-\d+-\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
