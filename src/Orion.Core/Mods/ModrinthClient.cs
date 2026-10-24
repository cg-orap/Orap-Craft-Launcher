using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;
using Orion.Core.Instances;
using Orion.Core.Net;
using Orion.Core.Util;

namespace Orion.Core.Mods;

public sealed class ModrinthOptions
{
    public string BaseUrl { get; set; } = "https://api.modrinth.com/v2";
    public string UserAgent { get; set; } = NetworkOptions.DefaultUserAgent;
}

public sealed class ModrinthSearchQuery
{
    public string Query { get; set; } = "";
    public string? GameVersion { get; set; }
    public LoaderKind? Loader { get; set; }
    public string ProjectType { get; set; } = "mod";
    public int Limit { get; set; } = 20;
    public int Offset { get; set; }
    public string SortBy { get; set; } = "relevance";

    /// <summary>Modrinth category slug (e.g. <c>optimization</c>); null means no category filter.</summary>
    public string? Category { get; set; }

    internal string BuildFacets()
    {
        var facets = new List<string> { $"[\"project_type:{ProjectType}\"]" };

        if (!string.IsNullOrWhiteSpace(GameVersion))
            facets.Add($"[\"versions:{GameVersion}\"]");

        if (Loader is { } loader && LoaderCategory(loader) is { } category)
            facets.Add($"[\"categories:{category}\"]");

        // A user picked category is a second, independent facet (Modrinth ANDs facets).
        if (!string.IsNullOrWhiteSpace(Category) &&
            !facets.Any(f => f.Contains($":{Category}\"", StringComparison.OrdinalIgnoreCase)))
        {
            facets.Add($"[\"categories:{Category}\"]");
        }

        return "[" + string.Join(",", facets) + "]";
    }

    internal static string? LoaderCategory(LoaderKind loader) => loader switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        LoaderKind.Quilt => "quilt",
        _ => null,
    };
}

public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = [];
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
    [JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];
    [JsonPropertyName("project_type")] public string? ProjectType { get; set; }

    /// <summary>Full description (markdown) - only returned by the single-project endpoint.</summary>
    [JsonPropertyName("body")] public string? Body { get; set; }

    public string Identifier => string.IsNullOrWhiteSpace(Slug) ? ProjectId : Slug;
}

public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("date_published")] public DateTimeOffset? DatePublished { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = [];
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = [];

    public ModrinthFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];

    public string? Sha1 => Hashes.TryGetValue("sha1", out var value) ? value : null;
    public string? Sha512 => Hashes.TryGetValue("sha512", out var value) ? value : null;
}

/// <summary>
/// Minimal Modrinth API v2 client (search, versions, install).
///
/// Modrinth's API is public and documents a User-Agent requirement; no API key is used and
/// nothing is scraped. Downloads are verified against the SHA-1 the API returns.
/// </summary>
public sealed class ModrinthClient
{
    private readonly HttpClient _http;
    private readonly ModrinthOptions _options;
    private readonly DownloadEngine? _downloader;
    private readonly MirrorResolver? _mirrors;

    public ModrinthClient(HttpClient http, ModrinthOptions? options = null, DownloadEngine? downloader = null, MirrorResolver? mirrors = null)
    {
        _http = http;
        _options = options ?? new ModrinthOptions();
        _downloader = downloader;
        _mirrors = mirrors;
    }

    public async Task<ModrinthSearchResult> SearchAsync(ModrinthSearchQuery query, CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl}/search?query={Uri.EscapeDataString(query.Query)}"
                  + $"&limit={Math.Clamp(query.Limit, 1, 100)}&offset={Math.Max(0, query.Offset)}"
                  + $"&index={Uri.EscapeDataString(query.SortBy)}"
                  + $"&facets={Uri.EscapeDataString(query.BuildFacets())}";

        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModrinthSearchResult>(json, JsonUtil.Options) ?? new ModrinthSearchResult();
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectIdentifier,
        string? gameVersion = null,
        LoaderKind? loader = null,
        CancellationToken ct = default)
    {
        var url = $"{_options.BaseUrl}/project/{Uri.EscapeDataString(projectIdentifier)}/version";
        var parameters = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameVersion))
            parameters.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));

        if (loader is { } kind && ModrinthSearchQuery.LoaderCategory(kind) is { } category)
            parameters.Add("loaders=" + Uri.EscapeDataString($"[\"{category}\"]"));

        if (parameters.Count > 0) url += "?" + string.Join("&", parameters);

        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<ModrinthVersion>>(json, JsonUtil.Options) ?? [];
    }

    public async Task<ModrinthProject?> GetProjectAsync(string projectIdentifier, CancellationToken ct = default)
    {
        try
        {
            var json = await GetStringAsync($"{_options.BaseUrl}/project/{Uri.EscapeDataString(projectIdentifier)}", ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ModrinthProject>(json, JsonUtil.Options);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Downloads the primary file of <paramref name="version"/> into <paramref name="modsDirectory"/>.</summary>
    public async Task<string> InstallAsync(ModrinthVersion version, string modsDirectory, CancellationToken ct = default)
    {
        var file = version.PrimaryFile ?? throw new InvalidOperationException("该版本没有可下载的文件。");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Modrinth 返回的下载地址无效。");

        Directory.CreateDirectory(modsDirectory);
        var target = Path.Combine(modsDirectory, SanitizeFileName(file.Filename));

        if (File.Exists(target) && await FileHashing.VerifyAsync(target, file.Sha1, HashAlgorithmKind.Sha1, ct).ConfigureAwait(false))
        {
            AppLog.Info("mods", "already-installed", ("file", target));
            return target;
        }

        var urls = _mirrors?.Resolve(uri) ?? [uri];

        if (_downloader is not null)
        {
            var report = await _downloader.DownloadAsync(
                [
                    new DownloadRequest
                    {
                        TargetPath = target,
                        Urls = urls,
                        Sha1 = file.Sha1,
                        Size = file.Size,
                        Label = file.Filename,
                        Verify = DownloadVerifyPolicy.Always,
                    },
                ],
                null,
                ct).ConfigureAwait(false);

            if (!report.Success)
                throw new HttpRequestException($"下载 Mod 失败: {report.Failures.FirstOrDefault()?.Message}");
        }
        else
        {
            using var response = await _http.GetAsync(urls[0], ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);
            await stream.CopyToAsync(output, ct).ConfigureAwait(false);

            if (!await FileHashing.VerifyAsync(target, file.Sha1, HashAlgorithmKind.Sha1, ct).ConfigureAwait(false))
            {
                File.Delete(target);
                throw new InvalidDataException("Mod 文件校验失败。");
            }
        }

        AppLog.Info("mods", "installed", ("file", target), ("version", version.VersionNumber));
        return target;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Modrinth 请求失败 (HTTP {(int)response.StatusCode}): {body}");

        return body;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) builder.Append(invalid.Contains(c) ? '_' : c);
        var result = builder.ToString();
        return result.Length == 0 ? "mod.jar" : result;
    }
}
