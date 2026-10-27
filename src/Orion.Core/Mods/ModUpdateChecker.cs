using Orion.Core.Instances;

namespace Orion.Core.Mods;

/// <summary>检查更新的结果：一条可更新项。下载由调用方在用户确认后再触发。</summary>
public sealed record ModUpdateCandidate(
    string ProjectId,
    string Title,
    string CurrentVersionId,
    string CurrentVersionNumber,
    string CurrentFileName,
    string LatestVersionId,
    string LatestVersionNumber,
    string LatestFileName,
    string? DownloadUrl,
    string? Sha1,
    IReadOnlyList<string> Dependencies,
    ModrinthVersion Latest);

/// <summary>取某个项目在指定游戏版本 / 加载器下的可用版本；抽成接口方便单测替换。</summary>
public interface IVersionSource
{
    Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string loader,
        CancellationToken ct = default);
}

/// <summary>走真实 Modrinth 的版本来源。</summary>
public sealed class ModrinthVersionSource(ModrinthClient client) : IVersionSource
{
    public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectId,
        string gameVersion,
        string loader,
        CancellationToken ct = default)
    {
        var kind = loader.ToLowerInvariant() switch
        {
            "fabric" => LoaderKind.Fabric,
            "quilt" => LoaderKind.Quilt,
            "forge" => LoaderKind.Forge,
            "neoforge" => LoaderKind.NeoForge,
            _ => (LoaderKind?)null,
        };

        return client.GetVersionsAsync(projectId, gameVersion, kind, ct);
    }
}

/// <summary>
/// 内容更新检查：只读清单 + 查询 Modrinth，不下载任何东西。
/// 结果交给界面展示，用户点"更新"之后才真正下载（见 ContentUpdateInstaller）。
/// </summary>
public sealed class ModUpdateChecker(IVersionSource source)
{
    public async Task<IReadOnlyList<ModUpdateCandidate>> CheckAsync(
        ContentManifest manifest,
        string gameVersion,
        string loader,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<ModUpdateCandidate>();
        var tracked = manifest.Entries.Where(e => e.IsTracked).ToList();

        for (var i = 0; i < tracked.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = tracked[i];
            progress?.Report($"检查 {entry.Title}（{i + 1}/{tracked.Count}）");

            IReadOnlyList<ModrinthVersion> versions;
            try
            {
                versions = await source.GetVersionsAsync(entry.ProjectId, gameVersion, loader, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 单个项目查询失败不影响其余项目，继续下一个。
                continue;
            }

            var latest = versions
                .OrderByDescending(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (latest is null || string.Equals(latest.Id, entry.VersionId, StringComparison.Ordinal)) continue;

            var file = latest.PrimaryFile;
            if (file is null) continue;

            results.Add(new ModUpdateCandidate(
                entry.ProjectId,
                entry.Title.Length > 0 ? entry.Title : entry.FileName,
                entry.VersionId,
                entry.VersionNumber,
                entry.FileName,
                latest.Id,
                latest.VersionNumber,
                file.Filename,
                file.Url,
                file.Sha1,
                latest.Dependencies.Where(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.ProjectId ?? d.VersionId ?? "")
                    .Where(id => id.Length > 0)
                    .ToList(),
                latest));
        }

        progress?.Report(results.Count == 0 ? "已是最新版本" : $"发现 {results.Count} 个可更新项");
        return results;
    }
}
