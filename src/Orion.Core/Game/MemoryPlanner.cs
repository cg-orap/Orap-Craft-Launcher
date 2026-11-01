namespace Orion.Core.Game;

/// <summary>
/// 自动给游戏分配内存。
///
/// 规则对齐主流启动器（PCL/HMCL/Axolotl）的经验值：
/// · 小整合包（&lt; 100 模组）基本吃不满，给 3–4 GB 就够；
/// · 中等整合包（100–250）给 4–6 GB；
/// · 大型整合包（250+）需要 6–8 GB；
/// · 上限永远不超过"物理内存的一半"，并且给系统留 2 GB；
/// · 下限 2 GB（1.20 之后原版就要 2 GB 才舒服）。
///
/// 不读注册表、不猜机器型号：只看物理内存总量 + 实例的模组数量 + 玩家填的偏好。
/// </summary>
public static class MemoryPlanner
{
    /// <summary>物理内存总量（MB）。取不到时按 8 GB 处理。</summary>
    public static int TotalMemoryMb { get; set; } = DetectTotalMemoryMb();

    /// <summary>给这台机器推荐的内存（MB），不考虑模组数量。</summary>
    public static int RecommendForMachine(int totalMb) =>
        totalMb >= 32 * 1024 ? 8192
        : totalMb >= 16 * 1024 ? 6144
        : totalMb >= 12 * 1024 ? 5120
        : totalMb >= 8 * 1024 ? 4096
        : totalMb >= 4 * 1024 ? 2048
        : 1536;

    /// <summary>按模组数量给出推荐值（MB）。</summary>
    public static int RecommendForPack(int totalMb, int modCount)
    {
        var baseline = RecommendForMachine(totalMb);

        var byMods = modCount switch
        {
            >= 350 => 8192,
            >= 250 => 7168,
            >= 150 => 6144,
            >= 60 => 5120,
            _ => baseline,
        };

        var value = Math.Max(baseline, byMods);

        // 给系统留 2 GB，且不超过物理内存的一半
        var ceiling = Math.Max(1024, Math.Min(totalMb - 2048, totalMb / 2));
        value = Math.Min(value, ceiling);

        return Math.Clamp(RoundTo512(value), 1024, 65536);
    }

    /// <summary>界面滑块用：按 512 MB 取整。</summary>
    public static int RoundTo512(int value) => (int)Math.Round(value / 512.0) * 512;

    private static int DetectTotalMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (bytes > 0) return (int)(bytes / 1024 / 1024);
        }
        catch (Exception ex) when (ex is NotSupportedException or PlatformNotSupportedException)
        {
            // 拿不到就退回默认值
        }

        return 8192;
    }
}
