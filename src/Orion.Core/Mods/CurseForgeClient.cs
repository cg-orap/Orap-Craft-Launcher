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

/// <summary>一页 CurseForge 搜索结果（带总数，供翻页）。</summary>
public sealed record CurseForgePage(IReadOnlyList<CurseForgePack> Packs, int TotalCount, int Index, int PageSize);

/// <summary>CurseForge 项目详情（详情页用：描述、截图、分类、下载页链接）。</summary>
public sealed record CurseForgeModDetail(
    int Id,
    string Name,
    string Summary,
    string Description,
    string? IconUrl,
    IReadOnlyList<string> Screenshots,
    IReadOnlyList<string> Categories,
    long Downloads,
    DateTimeOffset? Updated,
    string? WebsiteUrl);

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

    public async Task<IReadOnlyList<CurseForgePack>> SearchModpacksAsync(
        string? query, string? gameVersion, int pageSize = 20, CancellationToken ct = default) =>
        (await SearchPageAsync(CurseForgeKind.Modpack, query, gameVersion, 0, pageSize, ct).ConfigureAwait(false)).Packs;

    public async Task<IReadOnlyList<CurseForgePack>> SearchAsync(
        CurseForgeKind kind, string? query, string? gameVersion, int pageSize = 20, CancellationToken ct = default) =>
        (await SearchPageAsync(kind, query, gameVersion, 0, pageSize, ct).ConfigureAwait(false)).Packs;

    /// <summary>
    /// 按页搜索。CurseForge 用 <c>index</c>（偏移条数）+ <c>pageSize</c> 分页，返回里的
    /// <c>pagination.totalCount</c> 决定还能翻几页。
    /// </summary>
    public async Task<CurseForgePage> SearchPageAsync(
        CurseForgeKind kind, string? query, string? gameVersion, int index = 0, int pageSize = 20,
        CancellationToken ct = default, int sortField = 2, int? categoryId = null, int? modLoaderType = null)
    {
        if (!HasKey) return new CurseForgePage(Array.Empty<CurseForgePack>(), 0, 0, pageSize);

        var size = Math.Clamp(pageSize, 1, 50);
        var offset = Math.Max(0, index) * size;

        var url = $"{Base}mods/search?gameId={GameMinecraft}&classId={(int)kind}" +
                  $"&sortField={Math.Max(1, sortField)}&sortOrder=desc&pageSize={size}&index={offset}";

        if (!string.IsNullOrWhiteSpace(query)) url += "&searchFilter=" + Uri.EscapeDataString(query.Trim());
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion.Trim());
        if (categoryId is > 0) url += "&categoryId=" + categoryId.Value;
        if (modLoaderType is > 0) url += "&modLoaderType=" + modLoaderType.Value;

        // 搜索是 GET（官方文档：GET /v1/mods/search），用 POST 会 405 然后静默空结果
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new CurseForgePage(Array.Empty<CurseForgePack>(), 0, index, size);

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (payload?.Data is null) return new CurseForgePage(Array.Empty<CurseForgePack>(), 0, index, size);

        var total = payload.Pagination?.TotalCount ?? payload.Pagination?.ResultCount ?? payload.Data.Count;
        return new CurseForgePage([.. payload.Data.Select(ToPack)], total, index, size);
    }

    /// <summary>某个内容类型的分类列表（按 classId 过滤掉别的类型）。</summary>
    public async Task<IReadOnlyList<(int Id, string Name)>> GetCategoriesAsync(
        CurseForgeKind kind, CancellationToken ct = default)
    {
        if (!HasKey) return [];

        using var response = await SendAsync(
            HttpMethod.Get, $"{Base}categories?gameId={GameMinecraft}&classId={(int)kind}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        try
        {
            var payload = await response.Content
                .ReadFromJsonAsync<DataResponse<List<CategoryItemDto>>>(JsonOptions, ct).ConfigureAwait(false);

            return payload?.Data is null
                ? []
                : [.. payload.Data
                    .Where(c => c.ClassId is null || c.ClassId == (int)kind)
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => (c.Id, c.Name!))];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private sealed record CategoryItemDto(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("classId")] int? ClassId);

    /// <summary>按 projectId 取一个项目（已知发行版提示用）。</summary>
    public async Task<CurseForgePack?> GetPackAsync(int modId, CancellationToken ct = default)
    {
        if (!HasKey) return null;

        using var response = await SendAsync(HttpMethod.Get, $"{Base}mods/{modId}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<DataResponse<ModDto>>(JsonOptions, ct)
                .ConfigureAwait(false);
            return payload?.Data is { } dto ? ToPack(dto) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            // 响应结构不对（代理返回 HTML / 数组等）时当作取不到，不炸调用方
            return null;
        }
    }

    /// <summary>
    /// 按 slug 精确查找项目 id。
    ///
    /// 补前置时只靠 searchFilter 关键词搜名字并不可靠：搜 "patchouli" 会返回一堆无关模组，
    /// 搜 "geckolib3" 更是直接搜不到（它的 slug 是 geckolib）。
    /// CurseForge 的搜索接口支持 slug= 精确匹配，优先用它。
    /// </summary>
    public async Task<int?> FindProjectIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (!HasKey || string.IsNullOrWhiteSpace(slug)) return null;

        var url = $"{Base}mods/search?gameId=432&slug={Uri.EscapeDataString(slug)}&pageSize=10";

        try
        {
            using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(JsonOptions, ct).ConfigureAwait(false);
            var data = payload?.Data;
            if (data is null || data.Count == 0) return null;

            // 优先完全一致的 slug，否则退一步用第一条
            var exact = data.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
            return (exact ?? data[0]).Id;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>项目详情（含描述与截图）。</summary>
    public async Task<CurseForgeModDetail?> GetModAsync(int modId, CancellationToken ct = default)
    {
        if (!HasKey) return null;

        using var response = await SendAsync(HttpMethod.Get, $"{Base}mods/{modId}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<DataResponse<ModDto>>(JsonOptions, ct).ConfigureAwait(false);
        var dto = payload?.Data;
        if (dto is null) return null;

        var screenshots = (dto.Screenshots ?? [])
            .Select(x => x.ThumbnailUrl ?? x.Url)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        return new CurseForgeModDetail(
            dto.Id,
            string.IsNullOrWhiteSpace(dto.Name) ? $"#{dto.Id}" : dto.Name!,
            dto.Summary ?? "",
            StripHtml(dto.Description ?? dto.Summary ?? ""),
            dto.Logo?.ThumbnailUrl ?? dto.Logo?.Url,
            screenshots,
            [.. (dto.Categories ?? []).Select(c => c.Name ?? "").Where(n => n.Length > 0)],
            dto.DownloadCount,
            dto.DateModified ?? dto.DateReleased,
            dto.Links?.WebsiteUrl);
    }

    /// <summary>该项目的全部文件（新到旧），详情页用来提供历史版本下载。</summary>
    public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(int modId, int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!HasKey) return [];

        var url = $"{Base}mods/{modId}/files?pageSize={Math.Clamp(pageSize, 1, 50)}";
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        var payload = await response.Content.ReadFromJsonAsync<FilesResponse>(JsonOptions, ct).ConfigureAwait(false);
        var files = payload?.Data;
        if (files is null) return [];

        return [.. files
            .OrderByDescending(f => f.FileDate)
            .Select(f => new CurseForgeFile(f.ModId, f.Id, f.FileName, f.DisplayName, f.DownloadUrl, f.GameVersions))];
    }

    /// <summary>把 CurseForge 的描述 HTML 压成纯文本（详情页只用 TextBlock 显示）。</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</p>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// 选一个和当前实例匹配的文件：优先"目标 MC 版本 + 相同加载器"，退而求其次取最新文件。
    /// </summary>
    public async Task<CurseForgeFile?> GetBestFileAsync(
        CurseForgePack pack, string? gameVersion, int? modLoaderType,
        CurseForgeKind kind = CurseForgeKind.Modpack, CancellationToken ct = default)
    {
        if (!HasKey) return null;

        // 关键：把 gameVersion / modLoaderType 交给服务端过滤。
        // CurseForge 的文件列表默认按时间倒序，热门模组前 50 条往往全是新版本的文件，
        // 客户端自己过滤会"一个都不剩"，于是 1.19.2 的实例被塞进 26.3 的文件。
        var query = new System.Text.StringBuilder($"{Base}mods/{pack.Id}/files?pageSize=50");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Append("&gameVersion=").Append(Uri.EscapeDataString(gameVersion));
        if (modLoaderType is not null)
            query.Append("&modLoaderType=").Append(modLoaderType.Value);

        var url = query.ToString();
        using var response = await SendAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var payload = await response.Content.ReadFromJsonAsync<FilesResponse>(JsonOptions, ct).ConfigureAwait(false);
        var files = payload?.Data;
        if (files is null || files.Count == 0) return null;

        var extension = kind == CurseForgeKind.Mod ? ".jar" : ".zip";

        // 关键：版本/加载器不匹配的文件一律不要。
        // 以前这里只"算分不淘汰"，于是 1.19.2 的实例会被塞进 Iceberg-26.3、LibX-1.20.1，
        // Forge 不加载它们，启动后照样报 "iceberg is not installed"。
        var mapped = files
            .Select(f => new CurseForgeFile(f.ModId, f.Id, f.FileName, f.DisplayName, f.DownloadUrl, f.GameVersions))
            .ToList();

        var loaderTag = modLoaderType is { } type ? LoaderTag(type) : null;

        var best = CurseForgeFileSelection.SelectBest(
            mapped,
            gameVersion,
            string.IsNullOrWhiteSpace(loaderTag) ? null : loaderTag,
            extension,
            requireCompatible: !string.IsNullOrWhiteSpace(gameVersion));

        if (best is null) return null;
        return best;
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

    /// <summary>
    /// 元数据请求的超时。HttpClient 自身的 Timeout 是 10 分钟（给下载用的），
    /// 元数据接口一旦卡住，界面上的按钮会一直处于"执行中"而变灰——所以这里单独限时。
    /// </summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-api-key", _key);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);

        return await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
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

    private sealed record SearchResponse(
        [property: JsonPropertyName("data")] List<ModDto>? Data,
        [property: JsonPropertyName("pagination")] PaginationDto? Pagination);

    private sealed record PaginationDto(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("resultCount")] int ResultCount,
        [property: JsonPropertyName("totalCount")] int TotalCount);
    private sealed record FilesResponse([property: JsonPropertyName("data")] List<CurseForgeFileDto>? Data);
    private sealed record DataResponse<T>([property: JsonPropertyName("data")] T? Data);

    private sealed record ModDto
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("slug")] public string? Slug { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; init; }
        [JsonPropertyName("dateModified")] public DateTimeOffset? DateModified { get; init; }
        [JsonPropertyName("dateReleased")] public DateTimeOffset? DateReleased { get; init; }
        [JsonPropertyName("logo")] public LogoDto? Logo { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("screenshots")] public List<ScreenshotDto>? Screenshots { get; init; }
        [JsonPropertyName("categories")] public List<CategoryDto>? Categories { get; init; }
        [JsonPropertyName("links")] public LinksDto? Links { get; init; }
    }

    private sealed record LogoDto(
        [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record ScreenshotDto(
        [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record CategoryDto([property: JsonPropertyName("name")] string? Name);

    private sealed record LinksDto([property: JsonPropertyName("websiteUrl")] string? WebsiteUrl);

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
