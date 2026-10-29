using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Instances;

namespace Orion.Core.Mods;

/// <summary>CurseForge 上搜到的一个整合包。</summary>
public sealed record CurseForgePack(
    int Id,
    string Name,
    string Summary,
    string? LogoUrl,
    long Downloads,
    DateTimeOffset? Updated);

/// <summary>下载中心里可浏览的 CurseForge 内容类型（classId 来自官方分类）。</summary>
public enum CurseForgeKind
{
    Mod = 6,
    Modpack = 4471,
    ResourcePack = 12,
    Shader = 6552,
}

/// <summary>CurseForge 文件（整合包是 zip，模组是 jar，材质/光影是 zip）。</summary>
public sealed record CurseForgeFile(
    int ModId,
    int FileId,
    string FileName,
    string? DisplayName,
    string? DirectUrl,
    IReadOnlyList<string> GameVersions);

/// <summary>
/// CurseForge 官方 API 的最小客户端（只做整合包：搜索 → 选文件 → 拿下载地址）。
///
/// 需要 API Key；没有 Key 时 <see cref="HasKey"/> 为 false，界面应当直接提示去设置里填，
/// 而不是发一个注定 403 的请求。所有请求都带 <c>x-api-key</c>，Key 不会被写进日志。
/// </summary>
public sealed class CurseForgeClient(HttpClient http, string? apiKey)
{
    private const int GameMinecraft = 432;
    private const int ClassModpacks = 4471;
    private const string Base = "https://api.curseforge.com/v1/";

    private string? _key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    public bool HasKey => _key is not null;

    /// <summary>设置页填了 Key 之后立刻生效，不必重启。</summary>
    public void SetApiKey(string? key) => _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    /// <summary>内容类型 → 实例里的目录名（模组/材质/光影）；整合包没有目录。</summary>
    public static string? InstallFolderOf(CurseForgeKind kind) => kind switch
    {
        CurseForgeKind.Mod => "mods",
        CurseForgeKind.ResourcePack => "resourcepacks",
        CurseForgeKind.Shader => "shaderpacks",
        _ => null,
    };

    public Task<IReadOnlyList<CurseForgePack>> SearchModpacksAsync(
        string? query, string? gameVersion, int pageSize = 20, CancellationToken ct = default) =>
        SearchAsync(CurseForgeKind.Modpack, query, gameVersion, pageSize, ct);

    public async Task<IReadOnlyList<CurseForgePack>> SearchAsync(
        CurseForgeKind kind, string? query, string? gameVersion, int pageSize = 20, CancellationToken ct = default)
    {
        if (!HasKey) return [];

        var url = $"{Base}mods/search?gameId={GameMinecraft}&classId={(int)kind}" +
                  $"&sortField=2&sortOrder=desc&pageSize={Math.Clamp(pageSize, 1, 50)}";

        if (!string.IsNullOrWhiteSpace(query)) url += "&searchFilter=" + Uri.EscapeDataString(query.Trim());
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion.Trim());

        // 搜索是 GET（官方文档：GET /v1/mods/search），用 POST 会 405 然后静默空结果
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (payload?.Data is null) return [];

        return [.. payload.Data.Select(ToPack)];
    }

    /// <summary>
    /// 选一个和当前实例匹配的文件：优先"目标 MC 版本 + 相同加载器"，退而求其次取最新文件。
    /// </summary>
    public async Task<CurseForgeFile?> GetBestFileAsync(
        CurseForgePack pack, string? gameVersion, int? modLoaderType,
        CurseForgeKind kind = CurseForgeKind.Modpack, CancellationToken ct = default)
    {
        if (!HasKey) return null;

        var url = $"{Base}mods/{pack.Id}/files?pageSize=50";
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<FilesResponse>(JsonOptions, ct).ConfigureAwait(false);
        var files = payload?.Data;
        if (files is null || files.Count == 0) return null;

        var extension = kind == CurseForgeKind.Mod ? ".jar" : ".zip";
        var candidates = files
            .Where(f => f.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) candidates = files;

        var scored = candidates
            .Select(f => (File: f, Score: Score(f, gameVersion, modLoaderType)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.File.FileDate)
            .ToList();

        if (scored.Count == 0) return null;
        var best = scored[0].File;
        return new CurseForgeFile(best.ModId, best.Id, best.FileName, best.DisplayName, best.DownloadUrl,
            best.GameVersions);
    }

    /// <summary>官方下载地址（文件元数据里没有时需要再问一次接口）。</summary>
    public async Task<string?> GetDownloadUrlAsync(CurseForgeFile file, CancellationToken ct = default)
    {
        if (!HasKey) return file.DirectUrl;

        if (!string.IsNullOrWhiteSpace(file.DirectUrl)) return file.DirectUrl;

        var url = $"{Base}mods/{file.ModId}/files/{file.FileId}/download-url";
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<DataResponse<string>>(JsonOptions, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(payload?.Data) ? null : payload!.Data;
    }

    /// <summary>把单个文件（.jar / .zip）下载到目标目录，返回落地路径。</summary>
    public async Task<string> DownloadFileAsync(CurseForgeFile file, string targetDirectory,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var url = await GetDownloadUrlAsync(file, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("CurseForge 没有返回可用下载地址（Key 可能已失效）");

        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, Sanitize(file.FileName, "curseforge-file"));

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0) progress?.Report((long)(written * 100.0 / total));
        }

        return target;
    }

    /// <summary>把整包 zip 下到临时文件，交给整合包安装器处理。</summary>
    public async Task<string> DownloadPackAsync(
        CurseForgePack pack, CurseForgeFile file, string targetDirectory,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var url = await GetDownloadUrlAsync(file, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("CurseForge 没有返回可用下载地址（Key 可能已失效）");

        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, Sanitize(file.FileName, pack.Name));

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            if (total > 0) progress?.Report((long)(written * 100.0 / total));
        }

        return target;
    }

    /// <summary>CurseForge 的 modLoaderType：1 Forge / 4 Fabric / 5 Quilt / 6 NeoForge。</summary>
    public static int? LoaderTypeOf(LoaderKind loader) => loader switch
    {
        LoaderKind.Forge => 1,
        LoaderKind.Fabric => 4,
        LoaderKind.Quilt => 5,
        LoaderKind.NeoForge => 6,
        _ => null,
    };

    private static int Score(CurseForgeFileDto file, string? gameVersion, int? modLoaderType)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(gameVersion) && file.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
            score += 4;
        if (modLoaderType is { } loaderType && file.GameVersions.Contains(LoaderTag(loaderType), StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (file.ReleaseType == 1) score += 1;
        return score;
    }

    private static string LoaderTag(int modLoaderType) => modLoaderType switch
    {
        1 => "Forge",
        4 => "Fabric",
        5 => "Quilt",
        6 => "NeoForge",
        _ => "",
    };

    private static string Sanitize(string fileName, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? fallback + ".zip" : fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-api-key", _key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static CurseForgePack ToPack(ModDto dto) => new(
        dto.Id,
        string.IsNullOrWhiteSpace(dto.Name) ? $"#{dto.Id}" : dto.Name!,
        dto.Summary ?? "",
        dto.Logo?.ThumbnailUrl,
        dto.DownloadCount,
        dto.DateModified ?? dto.DateReleased);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record SearchResponse([property: JsonPropertyName("data")] List<ModDto>? Data);
    private sealed record FilesResponse([property: JsonPropertyName("data")] List<CurseForgeFileDto>? Data);
    private sealed record DataResponse<T>([property: JsonPropertyName("data")] T? Data);

    private sealed record ModDto
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; init; }
        [JsonPropertyName("dateModified")] public DateTimeOffset? DateModified { get; init; }
        [JsonPropertyName("dateReleased")] public DateTimeOffset? DateReleased { get; init; }
        [JsonPropertyName("logo")] public LogoDto? Logo { get; init; }
    }

    private sealed record LogoDto(
        [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record CurseForgeFileDto
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("modId")] public int ModId { get; init; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("fileName")] public string FileName { get; init; } = "";
        [JsonPropertyName("releaseType")] public int ReleaseType { get; init; }
        [JsonPropertyName("fileDate")] public DateTimeOffset FileDate { get; init; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }
        [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; init; } = [];
    }
}
