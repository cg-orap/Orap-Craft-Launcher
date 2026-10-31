namespace Orion.Core.Mods;

/// <summary>
/// ForgeCDN 的直链拼法：<c>files/{fileId/1000}/{fileId%1000}/{fileName}</c>。
///
/// 两个域名都要试（mediafilez / edge）。关键前提是**必须有文件名**：
/// 整合包的 manifest 只带 projectID + fileID，缺文件名时完全拼不出链接——
/// 这正是"同一个包 PCL 能装、我们装不上"的那批文件（PCL 会先查文件元数据拿名字）。
/// </summary>
public static class CurseForgeCdn
{
    private static readonly string[] Hosts =
    [
        "https://mediafilez.forgecdn.net",
        "https://edge.forgecdn.net",
    ];

    /// <summary>按 (fileId, fileName) 生成候选直链；没有文件名时返回空。</summary>
    public static IReadOnlyList<string> Urls(int fileId, string? fileName)
    {
        var name = NormalizeFileName(fileName);
        if (name is null) return [];

        var bucket = fileId / 1000;
        var rest = fileId % 1000;
        var escaped = Uri.EscapeDataString(name);

        var urls = new List<string>(Hosts.Length);
        foreach (var host in Hosts)
        {
            urls.Add($"{host}/files/{bucket}/{rest}/{escaped}");
        }

        return urls;
    }

    /// <summary>去掉 manifest 里可能带的引号与目录部分。</summary>
    public static string? NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var trimmed = fileName.Trim().Trim('"');
        if (trimmed.Length == 0) return null;

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>给用户看的浏览器下载页（自动失败时列出来让用户手点）。</summary>
    public static string BrowserUrl(int projectId, int fileId) =>
        $"https://www.curseforge.com/minecraft/mc-mods/{projectId}/files/{fileId}";
}
