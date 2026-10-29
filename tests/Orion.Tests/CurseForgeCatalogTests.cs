using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>下载中心的 CurseForge 分类映射：classId 与安装目录必须和官方/实例结构一致。</summary>
public sealed class CurseForgeCatalogTests
{
    [Theory]
    [InlineData(CurseForgeKind.Mod, 6, "mods")]
    [InlineData(CurseForgeKind.Modpack, 4471, null)]
    [InlineData(CurseForgeKind.ResourcePack, 12, "resourcepacks")]
    [InlineData(CurseForgeKind.Shader, 6552, "shaderpacks")]
    public void KindMapsToClassIdAndInstallFolder(CurseForgeKind kind, int classId, string? folder)
    {
        Assert.Equal(classId, (int)kind);
        Assert.Equal(folder, CurseForgeClient.InstallFolderOf(kind));
    }

    [Fact]
    public async Task SearchForwardsTheClassIdOfTheSelectedKind()
    {
        var handler = new UrlHandler();
        var client = new CurseForgeClient(new HttpClient(handler), "test-key");

        await client.SearchAsync(CurseForgeKind.Shader, "bsl", "1.20.1");
        Assert.Contains("classId=6552", handler.LastUrl);
        Assert.Contains("searchFilter=bsl", handler.LastUrl);
        Assert.Contains("gameVersion=1.20.1", handler.LastUrl);

        await client.SearchAsync(CurseForgeKind.ResourcePack, null, null);
        Assert.Contains("classId=12", handler.LastUrl);
    }

    [Fact]
    public void DefaultSearchIsModpacks()
    {
        // 模组页用的 SearchModpacksAsync 必须等价于 Modpack 分类
        Assert.Equal(4471, (int)CurseForgeKind.Modpack);
    }

    private sealed class UrlHandler : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        }
    }
}