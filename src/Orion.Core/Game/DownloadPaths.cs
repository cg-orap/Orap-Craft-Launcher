namespace Orion.Core.Game;

/// <summary>
/// 下载目录的解析规则：用户自定义优先，否则用数据根目录下的 <c>downloads</c>。
/// 抽成纯函数，便于单测（"设置里填了相对路径"也要能正确处理）。
/// </summary>
public static class DownloadPaths
{
    /// <summary>默认下载目录：&lt;root&gt;/downloads。</summary>
    public static string Default(GamePaths paths) => paths.DownloadsDir;

    /// <summary>
    /// 解析实际使用的下载目录。
    /// 自定义目录可以是绝对路径，也可以是相对数据根目录的相对路径；
    /// 空白视为"用默认"。
    /// </summary>
    public static string Resolve(GamePaths paths, string? customDirectory)
    {
        if (string.IsNullOrWhiteSpace(customDirectory)) return Default(paths);

        var value = customDirectory.Trim();
        try
        {
            return paths.Resolve(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 用户填了非法路径：退回默认目录，不要因此让下载直接失败
            return Default(paths);
        }
    }

    /// <summary>是否等于默认目录（界面用来显示"（默认）"）。</summary>
    public static bool IsDefault(GamePaths paths, string? customDirectory) =>
        string.IsNullOrWhiteSpace(customDirectory) ||
        string.Equals(Resolve(paths, customDirectory), Default(paths), StringComparison.OrdinalIgnoreCase);
}
