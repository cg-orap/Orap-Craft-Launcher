namespace Orion.Core.Mods;

/// <summary>
/// 把整合包安装器的输出行换算成 0–1 的总体进度（未识别的行返回 -1，表示"别动进度条"）。
///
/// 安装过程是"装加载器 → 下载模组 → 复制 overrides"几段，每段长度并不相同，
/// 所以按固定区间映射，保证进度条单调向前、不会来回跳。
/// </summary>
public static class PackInstallProgress
{
    private static readonly (string Keyword, double From, double To)[] Phases =
    [
        ("读取 CurseForge 清单", 0.02, 0.08),
        ("安装 加载器", 0.08, 0.45),
        ("安装 Forge", 0.08, 0.45),
        ("安装 NeoForge", 0.08, 0.45),
        ("安装 Fabric", 0.08, 0.45),
        ("安装 Quilt", 0.08, 0.45),
        ("正在预下载安装器依赖", 0.45, 0.58),
        ("正在运行", 0.58, 0.62),
        ("下载模组", 0.62, 0.95),
        ("复制 overrides", 0.95, 0.99),
        ("整合包已安装", 1.0, 1.0),
    ];

    /// <summary>返回 0–1；无法识别返回 -1。</summary>
    public static double FromLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return -1;

        foreach (var (keyword, from, to) in Phases)
        {
            if (!line.Contains(keyword, StringComparison.Ordinal)) continue;

            var inner = ParseRatio(line) ?? 0;
            return Math.Clamp(from + ((to - from) * inner), 0, 1);
        }

        return -1;
    }

    /// <summary>从 "12/64"、"正在下载 45%" 这类文字里取比例。</summary>
    private static double? ParseRatio(string line)
    {
        var percent = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})\s*%");
        if (percent.Success && double.TryParse(percent.Groups[1].Value, out var pct))
        {
            return Math.Clamp(pct / 100.0, 0, 1);
        }

        var ratio = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*/\s*(\d+)");
        if (!ratio.Success) return null;

        if (!double.TryParse(ratio.Groups[1].Value, out var done)) return null;
        if (!double.TryParse(ratio.Groups[2].Value, out var total) || total <= 0) return null;

        return Math.Clamp(done / total, 0, 1);
    }
}
