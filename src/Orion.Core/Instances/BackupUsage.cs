namespace Orion.Core.Instances;

/// <summary>
/// 备份占用统计：侧边栏那块「备份占用 x / y GB」用的就是这里的数据。
///
/// 只统计快照目录（<c>&lt;instance&gt;/minecraft/backups/snapshots</c>），
/// 不把实例本体、assets、libraries 算进去——不然数字会大到没有参考意义。
/// </summary>
public static class BackupUsage
{
    public const int DefaultQuotaMb = 8192;

    public sealed record Result(long UsedBytes, long QuotaBytes)
    {
        public double Percent => QuotaBytes <= 0 ? 0 : Math.Clamp(UsedBytes * 100.0 / QuotaBytes, 0, 100);

        public string UsedText => Format(UsedBytes);

        public string QuotaText => Format(QuotaBytes);

        /// <summary>侧边栏显示的一行文字，例如 "1.1 GB / 8 GB"。</summary>
        public string Text => $"{UsedText} / {QuotaText}";

        public bool IsNearLimit => Percent >= 90;
    }

    /// <summary>统计若干快照目录的总大小；目录不存在按 0 计。</summary>
    public static Result Measure(IEnumerable<string> snapshotRoots, int quotaMb = DefaultQuotaMb)
    {
        long total = 0;

        foreach (var root in snapshotRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件读不到就跳过，不要让整块统计失败
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可读：跳过
            }
        }

        var quota = Math.Max(1, quotaMb) * 1024L * 1024L;
        return new Result(total, quota);
    }

    public static Result MeasureForInstances(IEnumerable<string> gameDirectories, int quotaMb = DefaultQuotaMb) =>
        Measure(gameDirectories.Select(d => Path.Combine(d, "backups", "snapshots")), quotaMb);

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024 * 1024)):0.#} GB",
        >= 1024L * 1024 => $"{(bytes / (1024.0 * 1024)):0.#} MB",
        >= 1024 => $"{(bytes / 1024.0):0} KB",
        _ => bytes + " B",
    };
}
