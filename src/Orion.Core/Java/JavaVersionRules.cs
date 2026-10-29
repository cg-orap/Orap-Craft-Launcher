namespace Orion.Core.Java;

/// <summary>
/// MC 版本 → 加载器安装器需要的 Java 主版本。
///
/// 装整合包时最容易踩的坑就是这里：Forge/NeoForge 的安装器是 java -jar 跑的，
/// 版本挑错（或没挑）就会报"需要 Java 运行时来运行安装器"。
/// </summary>
public static class JavaVersionRules
{
    /// <summary>按 MC 版本推断需要的主版本；拿不到版本时按 17 处理（1.18–1.20 的常见情况）。</summary>
    public static int RequiredMajorFor(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return 17;

        var parts = minecraftVersion.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return 17;
        }

        if (major >= 26) return 25;   // 新版版本号方案（26.x）
        if (major > 1) return 21;     // 万一出现 2.x 这类
        return minor >= 21 ? 21 : minor >= 18 ? 17 : 8;
    }
}
