using System.Runtime.InteropServices;

namespace Orion.Core.Game;

/// <summary>
/// 自动给游戏分配内存。
///
/// 规则对齐主流启动器的经验值，并按玩家反馈调得更"舍得给"：
/// · 内存基线按物理内存分档；
/// · 再按模组数量加成（大包需要更多）；
/// · 只给系统留 2 GB，不再限制"最多一半物理内存"（老规则会把 16 GB 机器卡在 7.5 GB）；
/// · 上限仍是物理内存的 90%，避免把系统压死。
/// </summary>
public static class MemoryPlanner
{
    /// <summary>物理内存总量（MB）。取不到时按 8 GB 处理。</summary>
    public static int TotalMemoryMb { get; set; } = DetectTotalMemoryMb();

    /// <summary>给这台机器推荐的内存（MB），不考虑模组数量。</summary>
    public static int RecommendForMachine(int totalMb) =>
        totalMb >= 32 * 1024 ? 10240
        : totalMb >= 24 * 1024 ? 9216
        : totalMb >= 16 * 1024 ? 8192
        : totalMb >= 12 * 1024 ? 6144
        : totalMb >= 8 * 1024 ? 5120
        : totalMb >= 6 * 1024 ? 4096
        : 2560;

    /// <summary>按模组数量给出推荐值（MB）。</summary>
    public static int RecommendForPack(int totalMb, int modCount)
    {
        var baseline = RecommendForMachine(totalMb);

        var byMods = modCount switch
        {
            >= 400 => 12288,
            >= 300 => 10240,
            >= 200 => 8192,
            >= 100 => 6144,
            >= 50 => 5120,
            _ => baseline,
        };

        var value = Math.Max(baseline, byMods);

        // 只给系统留 2 GB；同时不超过物理内存的 90%
        var reserveBased = totalMb - 2048;
        var ratioBased = (int)(totalMb * 0.9);
        var ceiling = Math.Max(1024, Math.Min(reserveBased, ratioBased));

        value = Math.Min(value, ceiling);

        return Math.Clamp(RoundTo512(value), 1024, 65536);
    }

    /// <summary>界面滑块用：按 512 MB 取整。</summary>
    public static int RoundTo512(int value) => (int)Math.Round(value / 512.0) * 512;

    private static int DetectTotalMemoryMb()
    {
        // Windows 上读真实物理内存：GC.GetGCMemoryInfo() 给的是"GC 可用量"，
        // 在 16 GB 机器上常常只有 15 GB，旧规则按"一半"算就卡在 7680 MB。
        if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryMb(out var physical))
        {
            return physical;
        }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryGetPhysicalMemoryMb(out int megabytes)
    {
        megabytes = 0;

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;

            megabytes = (int)(status.TotalPhys / 1024 / 1024);
            return megabytes > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
