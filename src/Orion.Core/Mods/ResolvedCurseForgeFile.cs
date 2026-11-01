using System.Security.Cryptography;

namespace Orion.Core.Mods;

/// <summary>
/// 解析后的 CurseForge 文件：manifest 只给 (projectID, fileID)，
/// 这里把它变成"能下载、能校验"的完整信息（真实文件名 + sha1 + 大小 + 直链）。
///
/// 这一层是照着 Axolotl 启动器的做法加的：先解析全部文件元数据再下载，
/// 不要一边猜名字一边下——后者会留下 curseforge-123-456.jar 这种占位名，
/// 整合包自带的完整性检查（Better MC 的 Too Many Changes）就会把这些当成用户改过包。
/// </summary>
public sealed record ResolvedCurseForgeFile(
    int ProjectId,
    int FileId,
    string FileName,
    string? Sha1,
    long Size,
    string? DownloadUrl)
{
    /// <summary>下载完成后是否与元数据一致（大小 + sha1）。</summary>
    public bool Matches(FileInfo file)
    {
        if (!file.Exists) return false;
        if (Size > 0 && file.Length != Size) return false;

        if (!string.IsNullOrWhiteSpace(Sha1))
        {
            return string.Equals(HashOf(file.FullName), Sha1, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>算文件 sha1（大写十六进制，与 CurseForge 一致）。</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
