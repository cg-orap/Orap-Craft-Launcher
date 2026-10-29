using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>CurseForge 详情与历史版本：文件列表解析、按时间倒序、详情字段映射。</summary>
public sealed class CurseForgeDetailTests
{
    [Fact]
    public async Task FilesAreParsedAndSortedNewestFirst()
    {
        const string json = """
        {
          "data": [
            { "id": 11, "modId": 900, "fileName": "old.jar", "fileDate": "2023-03-01T00:00:00Z",
              "gameVersions": ["1.19.2", "Fabric"] },
            { "id": 22, "modId": 900, "fileName": "new.jar", "fileDate": "2025-01-15T00:00:00Z",
              "gameVersions": ["1.21", "Fabric"] }
          ]
        }
        """;

        var handler = new JsonHandler(json);
        var client = new CurseForgeClient(new HttpClient(handler), "test-key");

        var files = await client.GetFilesAsync(900);

        Assert.Equal(2, files.Count);
        Assert.Equal("new.jar", files[0].FileName);   // 新的在前
        Assert.Equal("old.jar", files[1].FileName);
        Assert.Contains("mods/900/files", handler.LastUrl);
    }

    [Fact]
    public async Task DetailParsesSummaryDescriptionAndCategories()
    {
        const string json = """
        {
          "data": {
            "id": 900,
            "name": "Sodium",
            "summary": "渲染优化",
            "description": "<p>第一段</p><p>第二段 &amp; 更多</p>",
            "downloadCount": 123456,
            "dateModified": "2025-02-01T00:00:00Z",
            "logo": { "thumbnailUrl": "https://example.invalid/logo.png" },
            "categories": [ { "name": "Optimization" }, { "name": "Fabric" } ],
            "screenshots": [ { "thumbnailUrl": "https://example.invalid/shot.png" } ],
            "links": { "websiteUrl": "https://example.invalid/sodium" }
          }
        }
        """;

        var handler = new JsonHandler(json);
        var client = new CurseForgeClient(new HttpClient(handler), "test-key");

        var detail = await client.GetModAsync(900);

        Assert.NotNull(detail);
        Assert.Equal("Sodium", detail!.Name);
        Assert.Equal(123456, detail.Downloads);
        Assert.Equal("https://example.invalid/logo.png", detail.IconUrl);
        Assert.Equal(["Optimization", "Fabric"], detail.Categories);
        Assert.Single(detail.Screenshots);
        Assert.Contains("第一段", detail.Description);
        Assert.DoesNotContain("<p>", detail.Description);          // HTML 已压成文本
        Assert.Contains("&", detail.Description);                  // 实体已解码
    }

    [Fact]
    public async Task WithoutKeyBothDetailCallsReturnEmpty()
    {
        var handler = new JsonHandler("""{"data":null}""");
        var client = new CurseForgeClient(new HttpClient(handler), null);

        Assert.Null(await client.GetModAsync(1));
        Assert.Empty(await client.GetFilesAsync(1));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = "";
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}