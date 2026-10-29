using Orion.Core.Instances;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>CurseForge 客户端的离线行为：没 Key 不发请求、加载器映射、文件名清洗。</summary>
public sealed class CurseForgeClientTests
{
    [Fact]
    public async Task WithoutKeySearchReturnsEmptyAndSendsNothing()
    {
        var handler = new CountingHandler();
        var client = new CurseForgeClient(new HttpClient(handler), apiKey: null);

        Assert.False(client.HasKey);
        Assert.Empty(await client.SearchModpacksAsync("create", "1.20.1"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, 1)]
    [InlineData(LoaderKind.Fabric, 4)]
    [InlineData(LoaderKind.Quilt, 5)]
    [InlineData(LoaderKind.NeoForge, 6)]
    public void LoaderTypeMatchesTheApiEnum(LoaderKind kind, int expected) =>
        Assert.Equal(expected, CurseForgeClient.LoaderTypeOf(kind));

    [Fact]
    public void ApiKeyCanBeSetAtRuntime()
    {
        var client = new CurseForgeClient(new HttpClient(new CountingHandler()), apiKey: "  ");
        Assert.False(client.HasKey);

        client.SetApiKey("  $2a$10$abc  ");
        Assert.True(client.HasKey);

        client.SetApiKey(null);
        Assert.False(client.HasKey);
    }

    [Fact]
    public async Task SearchParsesPacksFromTheApiPayload()
    {
        const string json = """
        {
          "data": [
            { "id": 1234, "name": "Create: Above and Beyond", "summary": "tech pack",
              "downloadCount": 987654, "logo": { "thumbnailUrl": "https://example.invalid/logo.png" },
              "dateModified": "2024-05-01T10:00:00Z" }
          ],
          "pagination": { "index": 0, "pageSize": 20, "resultCount": 1, "totalCount": 1 }
        }
        """;

        var handler = new JsonHandler(json);
        var client = new CurseForgeClient(new HttpClient(handler), "test-key");

        var packs = await client.SearchModpacksAsync("create", "1.20.1");

        var pack = Assert.Single(packs);
        Assert.Equal(1234, pack.Id);
        Assert.Equal("Create: Above and Beyond", pack.Name);
        Assert.Equal(987654, pack.Downloads);
        Assert.Equal("https://example.invalid/logo.png", pack.LogoUrl);
        Assert.Contains("classId=4471", handler.LastUrl);
        Assert.Contains("searchFilter=create", handler.LastUrl);
        Assert.Equal("test-key", handler.LastApiKey);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);   // 搜索是 GET，POST 会 405
    }

    [Fact]
    public async Task BestFilePrefersMatchingVersionThenLoader()
    {
        const string json = """
        {
          "data": [
            { "id": 1, "modId": 7, "fileName": "old-forge.zip", "releaseType": 1,
              "fileDate": "2023-01-01T00:00:00Z", "gameVersions": ["1.19.2", "Forge"] },
            { "id": 2, "modId": 7, "fileName": "new-fabric.zip", "releaseType": 1,
              "fileDate": "2024-06-01T00:00:00Z", "gameVersions": ["1.21", "Fabric"] },
            { "id": 3, "modId": 7, "fileName": "match.zip", "releaseType": 1,
              "fileDate": "2024-01-01T00:00:00Z", "gameVersions": ["1.20.1", "Forge"] }
          ]
        }
        """;

        var handler = new JsonHandler(json);
        var client = new CurseForgeClient(new HttpClient(handler), "test-key");
        var pack = new CurseForgePack(7, "Pack", "", null, 0, null);

        var file = await client.GetBestFileAsync(pack, "1.20.1", CurseForgeClient.LoaderTypeOf(LoaderKind.Forge));

        Assert.NotNull(file);
        Assert.Equal("match.zip", file!.FileName);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastApiKey { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.FirstOrDefault() : null;

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}