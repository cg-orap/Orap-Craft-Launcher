using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>CurseForge 翻页：index/pageSize 必须正确换算，并且要能读到 totalCount。</summary>
public sealed class CurseForgePagingTests
{
    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(1, 20, 20)]
    [InlineData(3, 20, 60)]
    [InlineData(2, 50, 100)]
    public async Task IndexIsOffsetByPageSize(int page, int pageSize, int expectedIndex)
    {
        var handler = new CaptureHandler("""
        {"data":[],"pagination":{"index":0,"pageSize":20,"resultCount":0,"totalCount":1234}}
        """);

        var client = new CurseForgeClient(new HttpClient(handler), "test-key");
        await client.SearchPageAsync(CurseForgeKind.Mod, null, null, page, pageSize);

        Assert.Contains($"index={expectedIndex}", handler.LastUrl);
        Assert.Contains($"pageSize={pageSize}", handler.LastUrl);
    }

    [Fact]
    public async Task TotalCountIsReturnedForPagination()
    {
        var handler = new CaptureHandler("""
        {"data":[{"id":1,"name":"Sodium","summary":"s","downloadCount":5}],
         "pagination":{"index":0,"pageSize":20,"resultCount":1,"totalCount":4321}}
        """);

        var client = new CurseForgeClient(new HttpClient(handler), "test-key");
        var page = await client.SearchPageAsync(CurseForgeKind.Mod, "sodium", null, 0, 20);

        Assert.Single(page.Packs);
        Assert.Equal(4321, page.TotalCount);
        Assert.Equal(20, page.PageSize);
    }

    [Fact]
    public async Task WithoutKeyPageIsEmptyButWellFormed()
    {
        var handler = new CaptureHandler("""{"data":[]}""");
        var client = new CurseForgeClient(new HttpClient(handler), null);

        var page = await client.SearchPageAsync(CurseForgeKind.Shader, null, null, 2, 20);
        Assert.Empty(page.Packs);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class CaptureHandler(string json) : HttpMessageHandler
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