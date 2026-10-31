namespace Orion.Core.Mods;

/// <summary>
/// 从 CurseForge 的文件列表里挑一个"真的能用"的文件。
///
/// 之前的实现只算分不淘汰：没有匹配当前 MC 版本的文件时，仍然会返回**最新的那个**，
/// 于是 1.19.2 的实例被塞进 Iceberg-26.3 和 LibX-1.20.1 —— Forge 根本不加载它们，
/// 启动后依旧报 "iceberg is not installed"。这里改成硬性过滤：
/// 只要指定了游戏版本，就不再接受不含该版本的文件。
/// </summary>
public static class CurseForgeFileSelection
{
    /// <summary>
    /// 选文件。
    /// </summary>
    /// <param name="files">候选文件（CurseForge 返回的列表）。</param>
    /// <param name="gameVersion">目标 MC 版本；为空表示"不限定版本"（浏览场景）。</param>
    /// <param name="loaderTag">Forge / NeoForge / Fabric / Quilt；为空表示不限加载器。</param>
    /// <param name="extension">期望扩展名（.jar / .zip）。</param>
    /// <param name="requireCompatible">
    /// true = 必须匹配版本与加载器，没有就不装（默认）；
    /// false = 找不到匹配时允许退而求其次（只在"不限定版本"浏览时使用）。
    /// </param>
    public static CurseForgeFile? SelectBest(
        IReadOnlyList<CurseForgeFile> files,
        string? gameVersion,
        string? loaderTag,
        string extension,
        bool requireCompatible = true)
    {
        if (files.Count == 0) return null;

        var byExtension = files
            .Where(f => f.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byExtension.Count == 0) byExtension = files.ToList();

        var compatible = byExtension.Where(f => IsCompatible(f, gameVersion, loaderTag)).ToList();

        var pool = compatible.Count > 0 ? compatible : (requireCompatible ? [] : byExtension);

        // 都没有可用的：宁可不装，也不要把别的版本塞进去
        if (pool.Count == 0) return null;

        // 同一批里按文件 id 倒序（CurseForge 的 id 单调递增，等价于"更新的优先"）。
        // 版本范围最终还要用 jar 里的 mods.toml 再核一遍，所以这里只保证"是对版本 + 对的加载器"。
        return pool.OrderByDescending(f => f.FileId).First();
    }

    /// <summary>版本与加载器都要匹配（加载器标签由 CurseForge 写在 gameVersions 里）。</summary>
    public static bool IsCompatible(CurseForgeFile file, string? gameVersion, string? loaderTag)
    {
        if (!string.IsNullOrWhiteSpace(gameVersion) &&
            !file.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(loaderTag) &&
            !file.GameVersions.Contains(loaderTag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// 处理"同一个前置被装了两份、其中一份是别的 MC 版本"的情况：
/// 补到正确文件后，把文件名里含同一 modId 的其它 jar 挪进隔离目录（不删，用户可还原），
/// 否则 Forge 会因为重复 mod 再次报错。
/// </summary>
public static class StaleModFiles
{
    /// <summary>挑出需要隔离的文件（排除刚装好的那个）。</summary>
    public static IReadOnlyList<string> SelectStale(IEnumerable<string> jarPaths, string modId, string keepPath)
    {
        var keep = Path.GetFullPath(keepPath);

        return jarPaths
            .Where(path => !string.Equals(Path.GetFullPath(path), keep, StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileName(path).Contains(modId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>把文件移到 &lt;gameDir&gt;/.orap-quarantine/&lt;时间戳&gt;/ 下并返回新位置。</summary>
    public static string Quarantine(string gameDirectory, string filePath, DateTimeOffset now)
    {
        var directory = Path.Combine(gameDirectory, ".orap-quarantine", now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, Path.GetFileName(filePath));
        if (File.Exists(target)) target = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(filePath));

        File.Move(filePath, target);
        return target;
    }
}
