using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Orion.Core.Mods;

/// <summary>模组声明的依赖，以及它被谁需要。</summary>
public sealed record RequiredDependency(string ModId, string VersionRange, string RequiredBy);

/// <summary>扫描结果：已装模组 + 缺失的必需依赖 + KubeJS 脚本引用了但不存在的命名空间。</summary>
public sealed record DependencyScan(
    IReadOnlyList<RequiredDependency> Missing,
    int ModCount,
    int DependencyCount,
    IReadOnlyList<MissingScriptReference>? ScriptReferences = null);

/// <summary>
/// 扫描整合包 mods 目录，找出<b>必需但没装</b>的前置模组。
///
/// 依据是每个 jar 里的 <c>META-INF/neoforge.mods.toml</c> / <c>META-INF/mods.toml</c>：
/// <code>
/// [[mods]]
/// modId = "tracks"
/// version = "1.0.1"
///
/// [[dependencies.tracks]]
/// modId = "simulated"
/// mandatory = true
/// versionRange = "[1.0.1,)"
/// </code>
/// Forge 与 NeoForge 的字段一致，所以同一套解析两边都能用。
/// </summary>
public static class ModDependencyScanner
{
    private const string ForgeToml = "META-INF/mods.toml";
    private const string NeoForgeToml = "META-INF/neoforge.mods.toml";
    private const string FabricJson = "fabric.mod.json";

    /// <summary>扫描一个 mods 目录。</summary>
    public static DependencyScan Scan(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
        {
            return new DependencyScan(Array.Empty<RequiredDependency>(), 0, 0);
        }

        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var required = new List<RequiredDependency>();
        var modCount = 0;

        foreach (var jar in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            var info = ReadJar(jar);
            if (info is null) continue;

            modCount++;
            foreach (var (id, version) in info.Mods)
            {
                // 同一个 modid 出现多次时保留第一个（正常不会）
                installed.TryAdd(id, version);
            }

            foreach (var dependency in info.Dependencies)
            {
                if (!dependency.Mandatory) continue;
                if (IsBuiltin(dependency.ModId)) continue;

                required.Add(new RequiredDependency(dependency.ModId, dependency.VersionRange, info.PrimaryModId));
            }
        }

        var missing = new List<RequiredDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in required)
        {
            if (installed.TryGetValue(dependency.ModId, out var version))
            {
                // 已安装：只有当版本不满足范围时才算问题。
                // 注意：很多 Forge 模组的 mods.toml 里 version 是没被替换的占位符
                // （"${file.jarVersion}"），这种值没法比较——直接当"已安装"处理，
                // 否则补好的前置会被反复报成"缺失"，用户永远修不完。
                if (string.IsNullOrWhiteSpace(dependency.VersionRange)
                    || ModDependencyRepair.IsUnresolvedVersion(version)
                    || VersionRange.Satisfies(version, dependency.VersionRange))
                {
                    continue;
                }

                var tooOld = dependency with { VersionRange = dependency.VersionRange };
                if (seen.Add($"old:{tooOld.ModId}")) missing.Add(tooOld);
                continue;
            }

            if (!seen.Add($"miss:{dependency.ModId}")) continue;
            missing.Add(dependency);
        }

        var kubeJs = Path.Combine(Path.GetDirectoryName(modsDirectory) ?? modsDirectory, "kubejs");
        var scripts = KubeJsScriptScanner.Scan(kubeJs, installed.Keys);

        return new DependencyScan(missing, modCount, required.Count, scripts);
    }

    /// <summary>Minecraft / Forge / NeoForge 这些内置模块永远"已安装"，不用去找。</summary>
    private static bool IsBuiltin(string modId) => modId.ToLowerInvariant() switch
    {
        "minecraft" or "forge" or "neoforge" or "fml" or "java" or "fabricloader" or "fabric-api" => true,
        _ => false,
    };

    /// <summary>去掉行尾注释（'#' 之后的），但不碰引号里的内容（比如 URL 里的 #）。</summary>
    private static string StripInlineComment(string value)
    {
        var inQuote = false;
        var quoteChar = '"';

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is '"' or '\'')
            {
                if (inQuote && ch == quoteChar) inQuote = false;
                else if (!inQuote) { inQuote = true; quoteChar = ch; }
                continue;
            }

            if (!inQuote && ch == '#') return value[..i];
        }

        return value;
    }
    internal sealed record JarInfo(string PrimaryModId, IReadOnlyList<(string Id, string Version)> Mods,
        IReadOnlyList<(string ModId, string VersionRange, bool Mandatory)> Dependencies);

    private static JarInfo? ReadJar(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var toml = zip.Entries.FirstOrDefault(e =>
                           e.FullName.Equals(NeoForgeToml, StringComparison.OrdinalIgnoreCase))
                       ?? zip.Entries.FirstOrDefault(e =>
                           e.FullName.Equals(ForgeToml, StringComparison.OrdinalIgnoreCase));

            if (toml is not null)
            {
                using var reader = new StreamReader(toml.Open());
                var info = ParseToml(reader.ReadToEnd());
                if (info is null) return null;

                // bundled 发行版（例如 Create Aeronautics）把 aeronautics/simulated/offroad 打进
                // META-INF/jarjar/*.jar。这些内嵌 jar 的 modId 也算"已安装"，
                // 否则整合包会永远被判成缺前置。
                var nestedMods = ReadNestedMods(zip);
                return nestedMods.Count == 0
                    ? info
                    : info with { Mods = [.. info.Mods, .. nestedMods] };
            }

            // Fabric 模组没有依赖版本范围语义（version 字段是字符串），这里只记录它存在
            var fabric = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals(FabricJson, StringComparison.OrdinalIgnoreCase));
            if (fabric is null) return null;

            using var fabricReader = new StreamReader(fabric.Open());
            var json = fabricReader.ReadToEnd();
            var idMatch = Regex.Match(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (!idMatch.Success) return null;

            var versionMatch = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            var fabricId = idMatch.Groups[1].Value;
            return new JarInfo(fabricId,
                [(fabricId, versionMatch.Success ? versionMatch.Groups[1].Value : "0")],
                Array.Empty<(string, string, bool)>());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读 META-INF/jarjar/*.jar 里的 modId（bundled 发行版的模块就在这些内嵌 jar 里）。</summary>
    private static List<(string Id, string Version)> ReadNestedMods(ZipArchive zip)
    {
        var nestedMods = new List<(string Id, string Version)>();

        foreach (var nested in zip.Entries.Where(e =>
                     e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)
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
                    e.FullName.Equals(NeoForgeToml, StringComparison.OrdinalIgnoreCase))
                    ?? nestedZip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals(ForgeToml, StringComparison.OrdinalIgnoreCase));
                if (nestedToml is null) continue;

                using var nestedReader = new StreamReader(nestedToml.Open());
                var nestedInfo = ParseToml(nestedReader.ReadToEnd());
                if (nestedInfo is not null) nestedMods.AddRange(nestedInfo.Mods);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // 内嵌 jar 坏了就跳过
            }
        }

        return nestedMods;
    }


    /// <summary>
    /// 够用的 TOML 解析：只关心 [[mods]] 与 [[dependencies.xxx]] 两个表，
    /// 不引入 TOML 库（发布是单文件、离线可还原的）。
    /// </summary>
    internal static JarInfo? ParseToml(string toml)
    {
        var mods = new List<(string Id, string Version)>();
        var dependencies = new List<(string ModId, string VersionRange, bool Mandatory)>();

        string? primary = null;
        var section = "";

        foreach (var rawLine in toml.Split('\n'))
        {
            // 真实的 mods.toml 通篇带行尾注释，连表头都带：[[mods]] #mandatory。
            // 不先剥注释，表头永远匹配不上，解析结果会是空的。
            var line = StripInlineComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                section = line[2..^2].Trim().Trim('"', '\'');
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Trim('"', '\'');

            if (section.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("modId", StringComparison.OrdinalIgnoreCase))
                {
                    mods.Add((value, "0"));
                    primary ??= value;
                }
                else if (key.Equals("version", StringComparison.OrdinalIgnoreCase) && mods.Count > 0)
                {
                    var last = mods[^1];
                    mods[^1] = (last.Id, value);
                }

                continue;
            }

            if (!section.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase)) continue;

            switch (key.ToLowerInvariant())
            {
                case "modid":
                    dependencies.Add((value, "", true));
                    break;

                case "versionrange" when dependencies.Count > 0:
                    var withRange = dependencies[^1];
                    dependencies[^1] = (withRange.ModId, value, withRange.Mandatory);
                    break;

                case "mandatory" when dependencies.Count > 0:
                    var flag = dependencies[^1];
                    dependencies[^1] = (flag.ModId, flag.VersionRange,
                        !value.Equals("false", StringComparison.OrdinalIgnoreCase));
                    break;

                // 新版 Forge/NeoForge 用 type：required / optional / incompatible / discouraged
                case "type" when dependencies.Count > 0:
                    var typed = dependencies[^1];
                    var optional = value.Contains("optional", StringComparison.OrdinalIgnoreCase)
                                   || value.Contains("incompatible", StringComparison.OrdinalIgnoreCase)
                                   || value.Contains("discouraged", StringComparison.OrdinalIgnoreCase);
                    dependencies[^1] = (typed.ModId, typed.VersionRange, !optional);
                    break;
            }
        }

        if (primary is null) return null;
        return new JarInfo(primary, mods, dependencies);
    }

}

/// <summary>
/// Maven 风格的版本范围判断（Forge/NeoForge 用的就是这个语法）：
/// <c>[1.2.1,)</c>、<c>[1.1.3,2.0.0)</c>、<c>[1.0,1.1]</c>、<c>1.2.3</c>（精确匹配）。
/// 版本号按数字段比较，非数字段忽略。
/// </summary>
public static class VersionRange
{
    public static bool Satisfies(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;

        // mods.toml 里常见的通配写法：* / x / 任意
        var trimmedRange = range.Trim();
        if (trimmedRange is "*" or "x" or "X") return true;

        var (minimum, minInclusive, maximum, maxInclusive, exact) = Parse(range);
        var current = ParseVersion(version);

        if (exact is not null)
        {
            return Compare(current, ParseVersion(exact)) == 0;
        }

        if (minimum is not null)
        {
            var cmp = Compare(current, ParseVersion(minimum));
            if (cmp < 0 || (cmp == 0 && !minInclusive)) return false;
        }

        if (maximum is not null)
        {
            var cmp = Compare(current, ParseVersion(maximum));
            if (cmp > 0 || (cmp == 0 && !maxInclusive)) return false;
        }

        return true;
    }

    /// <summary>范围是不是通配符（* / x / 任意）。</summary>
    public static bool IsWildcard(string range)
    {
        var text = range.Trim();
        return text is "*" or "x" or "X" or "任意"
               || text.Equals("any", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 两个 Maven 范围是否可能存在交集。空白范围与通配符永远算有交集。
    /// </summary>
    public static bool Overlaps(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return true;

        // "*"/"x" 在 mods.toml 里表示"任意版本"，和谁都有交集（否则 C7 会误报）
        if (IsWildcard(left) || IsWildcard(right)) return true;

        var a = Parse(left);
        var b = Parse(right);

        // 精确版本：只要落在对方范围内就算有交集
        if (a.Exact is not null) return Satisfies(a.Exact, right);
        if (b.Exact is not null) return Satisfies(b.Exact, left);

        // 下界取大、上界取小，看是否还能构成区间
        var (min, minInclusive) = StrongerLower(a, b);
        var (max, maxInclusive) = WeakerUpper(a, b);

        if (min is null || max is null) return true;

        var cmp = Compare(ParseVersion(min), ParseVersion(max));
        if (cmp < 0) return true;
        return cmp == 0 && minInclusive && maxInclusive;
    }

    private static (string? Min, bool Inclusive) StrongerLower(
        (string? Min, bool MinInclusive, string? Max, bool MaxInclusive, string? Exact) a,
        (string? Min, bool MinInclusive, string? Max, bool MaxInclusive, string? Exact) b)
    {
        if (a.Min is null) return (b.Min, b.MinInclusive);
        if (b.Min is null) return (a.Min, a.MinInclusive);

        var cmp = Compare(ParseVersion(a.Min), ParseVersion(b.Min));
        if (cmp > 0) return (a.Min, a.MinInclusive);
        if (cmp < 0) return (b.Min, b.MinInclusive);
        return (a.Min, a.MinInclusive && b.MinInclusive);
    }

    private static (string? Max, bool Inclusive) WeakerUpper(
        (string? Min, bool MinInclusive, string? Max, bool MaxInclusive, string? Exact) a,
        (string? Min, bool MinInclusive, string? Max, bool MaxInclusive, string? Exact) b)
    {
        if (a.Max is null) return (b.Max, b.MaxInclusive);
        if (b.Max is null) return (a.Max, a.MaxInclusive);

        var cmp = Compare(ParseVersion(a.Max), ParseVersion(b.Max));
        if (cmp < 0) return (a.Max, a.MaxInclusive);
        if (cmp > 0) return (b.Max, b.MaxInclusive);
        return (a.Max, a.MaxInclusive && b.MaxInclusive);
    }

    /// <summary>
    /// 版本键：数字核心 + 预发布后缀。Maven/语义化版本里
    /// <c>1.2.3-beta.1</c> 小于 <c>1.2.3</c>，而 <c>1.2.3+build</c> 与 <c>1.2.3</c> 相等。
    /// 只比数字会把 <c>0.3.31-beta.53</c> 当成 [0,3,31,53]，比 0.3.31 还大，于是误报"版本不满足"。
    /// </summary>
    internal readonly record struct VersionKey(int[] Numbers, string? PreRelease);

    internal static VersionKey ParseVersion(string version)
    {
        var text = version.Trim();

        // 构建元数据（+xxx）不参与比较
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        string? pre = null;
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            pre = text[(dash + 1)..].Trim();
            text = text[..dash];
        }

        var numbers = new List<int>();
        foreach (Match match in Regex.Matches(text, @"\d+"))
        {
            numbers.Add(int.TryParse(match.Value, out var value) ? value : 0);
            if (numbers.Count >= 4) break;
        }

        return new VersionKey(numbers.Count == 0 ? [0] : [.. numbers], string.IsNullOrWhiteSpace(pre) ? null : pre);
    }

    internal static int Compare(VersionKey left, VersionKey right)
    {
        var length = Math.Max(left.Numbers.Length, right.Numbers.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Numbers.Length ? left.Numbers[i] : 0;
            var b = i < right.Numbers.Length ? right.Numbers[i] : 0;
            if (a != b) return a < b ? -1 : 1;
        }

        // 数字相同：没有预发布后缀的更大（1.0.0 > 1.0.0-beta）
        if (left.PreRelease is null && right.PreRelease is null) return 0;
        if (left.PreRelease is null) return 1;
        if (right.PreRelease is null) return -1;

        return string.Compare(left.PreRelease, right.PreRelease,
            StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Min, bool MinInclusive, string? Max, bool MaxInclusive, string? Exact) Parse(string range)
    {
        var text = range.Trim();
        if (!text.StartsWith('[') && !text.StartsWith('('))
        {
            return (null, false, null, false, text.Trim('[', ']', '(', ')'));
        }

        var minInclusive = text.StartsWith('[');
        var maxInclusive = text.EndsWith(']');
        var inner = text[1..^1];

        var parts = inner.Split(',', 2);
        var min = parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : null;
        var max = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;
        return (min, minInclusive, max, maxInclusive, null);
    }
}
