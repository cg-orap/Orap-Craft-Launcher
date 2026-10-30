using System.Text.RegularExpressions;

namespace Orion.Core.Mods;

/// <summary>KubeJS 脚本里引用到、但当前实例没有提供该命名空间的一条记录。</summary>
public sealed record MissingScriptReference(string File, int Line, string Namespace, string ItemId);

/// <summary>
/// 扫 KubeJS 脚本里的 <c>namespace:id</c> 引用，找出"引用了但没装对应模组"的命名空间。
///
/// 这类问题和缺前置不是一回事：mods.toml 里没有依赖声明，但脚本里的物品 id 会解析失败，
/// 于是 KubeJS 在启动时弹出 "client script errors" 并把游戏卡在错误界面
/// （例如 fxntstorage:passer_block 在没装 FXNT Storage 的包里）。
/// 命名空间一般就是 modid，所以这里直接和"已安装的 modid 集合"对比。
/// </summary>
public static class KubeJsScriptScanner
{
    /// <summary>原版与 KubeJS 自带的命名空间，永远算"存在"。</summary>
    private static readonly HashSet<string> BuiltinNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "kubejs", "forge", "neoforge", "fabric", "fabricloader", "c", "tag", "kubejs_additions",
    };

    private static readonly Regex ReferencePattern = new(
        @"\b([a-z0-9_.-]+):([a-z0-9_./-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 扫描目录下的 .js 脚本。
    /// </summary>
    /// <param name="kubeJsDirectory">实例里的 kubejs 目录（不存在则返回空）。</param>
    /// <param name="installedModIds">已安装模组的 modid 集合。</param>
    public static IReadOnlyList<MissingScriptReference> Scan(string kubeJsDirectory, IEnumerable<string> installedModIds)
    {
        if (!Directory.Exists(kubeJsDirectory)) return [];

        var known = new HashSet<string>(BuiltinNamespaces, StringComparer.OrdinalIgnoreCase);
        foreach (var id in installedModIds)
        {
            if (!string.IsNullOrWhiteSpace(id)) known.Add(id);
        }

        var missing = new List<MissingScriptReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(kubeJsDirectory, "*.js", SearchOption.AllDirectories))
        {
            var lines = ReadLinesSafely(file);
            if (lines is null) continue;

            var relative = Path.GetRelativePath(kubeJsDirectory, file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                // 注释掉的行不算（我们自己的修复就是靠注释）
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;

                foreach (Match match in ReferencePattern.Matches(line))
                {
                    var ns = match.Groups[1].Value;
                    if (known.Contains(ns)) continue;

                    var id = match.Value;
                    if (!seen.Add(ns + "|" + id)) continue;

                    missing.Add(new MissingScriptReference(relative, index + 1, ns, id));
                }
            }
        }

        return missing;
    }

    private static string[]? ReadLinesSafely(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
