using System.Text.Json.Serialization;
using Orion.Core.Util;

namespace Orion.Core.Mods;

/// <summary>
/// 一个实例里"由启动器安装、可追踪更新"的内容条目。手动放进 mods 目录的文件也会记录，
/// 但 Source 为 manual，不参与更新检查。
/// </summary>
public sealed class ContentEntry
{
    [JsonPropertyName("projectId")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("versionNumber")] public string VersionNumber { get; set; } = "";
    /// <summary>modrinth / pack / manual。只有 modrinth 条目会被更新检查。</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "modrinth";
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public bool IsTracked => Source == "modrinth" && ProjectId.Length > 0;
}

/// <summary>
/// 实例内容清单（content.json）。更新检查靠它比对版本，而不是扫 jar 文件名猜。
/// </summary>
public sealed class ContentManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("entries")] public List<ContentEntry> Entries { get; set; } = [];

    [JsonIgnore] public string? Path { get; private set; }

    public static string FileName => "content.json";

    public ContentEntry? Find(string projectId) =>
        Entries.FirstOrDefault(e => string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));

    public ContentEntry? FindByFile(string fileName) =>
        Entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>同一个 project 只保留一条：按 projectId 覆盖，否则按文件名覆盖。</summary>
    public void Upsert(ContentEntry entry)
    {
        var existing = entry.ProjectId.Length > 0 ? Find(entry.ProjectId) : FindByFile(entry.FileName);
        existing ??= FindByFile(entry.FileName);

        if (existing is null)
        {
            Entries.Add(entry);
            return;
        }

        existing.ProjectId = entry.ProjectId.Length > 0 ? entry.ProjectId : existing.ProjectId;
        existing.VersionId = entry.VersionId;
        existing.FileName = entry.FileName;
        existing.Sha1 = entry.Sha1 ?? existing.Sha1;
        existing.Title = entry.Title.Length > 0 ? entry.Title : existing.Title;
        existing.VersionNumber = entry.VersionNumber.Length > 0 ? entry.VersionNumber : existing.VersionNumber;
        existing.Source = entry.Source;
        existing.UpdatedAt = entry.UpdatedAt;
    }

    public bool Remove(string fileName)
    {
        var removed = Entries.RemoveAll(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    public static ContentManifest Load(string instanceDirectory)
    {
        var path = System.IO.Path.Combine(instanceDirectory, FileName);
        if (!File.Exists(path)) return new ContentManifest { Path = path };

        try
        {
            var manifest = JsonUtil.Deserialize<ContentManifest>(File.ReadAllText(path)) ?? new ContentManifest();
            manifest.Path = path;
            manifest.Version = CurrentVersion;
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // 清单损坏时不要挡住启动：当成空清单，下一次保存会覆盖。
            return new ContentManifest { Path = path };
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var path = Path ?? throw new InvalidOperationException("清单缺少路径");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await JsonUtil.WriteFileAsync(path, this, ct).ConfigureAwait(false);
    }
}
