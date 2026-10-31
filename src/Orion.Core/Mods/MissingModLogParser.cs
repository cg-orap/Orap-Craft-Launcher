using System.Text.RegularExpressions;

namespace Orion.Core.Mods;

/// <summary>
/// 从游戏日志 / 崩溃报告里找出"缺哪个前置"。
///
/// Forge / NeoForge 的报错长这样（界面上也能看到）：
///   Mod <c>legendarytooltips</c> requires <c>iceberg</c> <c>1.1.2</c> or above
///   Currently, <c>iceberg</c> is <c>not installed</c>
///   Mod file X is missing or incompatible...
/// 这两行不好直接当成"依赖声明"（第一行说需要谁，第二行才说缺谁），所以分开解析：
/// 第一行建立 "requirer → dependency" 的待定关系，第二行的 "is not installed" 才确认缺失。
///
/// 抽成纯函数便于单测：给几段真实日志就能验证解析结果。
/// </summary>
public static class MissingModLogParser
{
    // Mod legendarytooltips requires iceberg 1.1.2 or above
    private static readonly Regex Requires = new(
        @"Mod\s+(?<mod>[\w\-.]+)\s+requires\s+(?<dep>[\w\-.]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Currently, iceberg is not installed   /   Currently, libx is not installed
    private static readonly Regex NotInstalled = new(
        @"Currently,\s+(?<dep>[\w\-.]+)\s+is\s+not\s+installed",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // forge / minecraft 这类内置 id 不需要补
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fabricloader", "fabric", "quilt_loader", "quilt",
        "java", "lowcodefml", "mcp",
    };

    /// <summary>解析日志文本，返回缺失的模组 id（去重，按出现顺序）。</summary>
    public static IReadOnlyList<MissingMod> Parse(IEnumerable<string> lines)
    {
        // 先记住"谁需要谁"，等遇到 "is not installed" 再确认
        var requiresBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<MissingMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();

            foreach (Match match in Requires.Matches(line))
            {
                var dep = match.Groups["dep"].Value;
                if (BuiltIn.Contains(dep)) continue;

                requiresBy[dep] = match.Groups["mod"].Value;
            }

            foreach (Match match in NotInstalled.Matches(line))
            {
                var dep = match.Groups["dep"].Value;
                if (BuiltIn.Contains(dep)) continue;
                if (!seen.Add(dep)) continue;

                missing.Add(new MissingMod(dep, requiresBy.TryGetValue(dep, out var requiredBy) ? requiredBy : "（日志未指明）"));
            }
        }

        return missing;
    }

    /// <summary>从磁盘上的日志/崩溃报告文件解析；文件不存在返回空。</summary>
    public static IReadOnlyList<MissingMod> ParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadLines(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 在一个实例目录里找最值得解析的日志：优先 crash-reports 里最新的，其次 logs/latest.log。
    /// </summary>
    public static string? FindNewestReport(string gameDirectory)
    {
        try
        {
            var candidates = new List<string>();

            var crashDir = Path.Combine(gameDirectory, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                candidates.AddRange(Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc));
            }

            var latest = Path.Combine(gameDirectory, "logs", "latest.log");
            if (File.Exists(latest)) candidates.Add(latest);

            return candidates.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>日志里查到的一个缺失前置。</summary>
public sealed record MissingMod(string ModId, string RequiredBy);
