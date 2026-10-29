using System.Text.Json;
using Orion.Core.Mods;
using Orion.Core.Util;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 需要网络的真实 CurseForge 探针：只在环境变量 CF_PROBE=1 时运行（默认跳过，CI 不受影响）。
/// 它读本机真正的 settings.json，用 DPAPI 解出 Key 后走一遍搜索 → 选文件 → 拿下载地址 → HEAD。
/// </summary>
public sealed class CurseForgeLiveProbeTests
{
    private const string SettingsFile = @"D:\gd\orion\settings.json";

    [Fact]
    public async Task RealKeySearchesPacksAndResolvesADownloadableZip()
    {
        if (Environment.GetEnvironmentVariable("CF_PROBE") != "1") return;
        if (!File.Exists(SettingsFile)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        var protectedKey = doc.RootElement.TryGetProperty("curseForgeKeyProtected", out var p) ? p.GetString() : null;
        var key = SecretProtector.Unprotect(protectedKey);
        Assert.False(string.IsNullOrWhiteSpace(key), "settings.json 里没有可解密的 CurseForge Key");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = new CurseForgeClient(http, key);

        var packs = await client.SearchModpacksAsync("create", null, 5);
        Assert.NotEmpty(packs);

        var pack = packs[0];
        var file = await client.GetBestFileAsync(pack, null, null);
        Assert.NotNull(file);

        var url = await client.GetDownloadUrlAsync(file!);
        Assert.False(string.IsNullOrWhiteSpace(url));

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.True(head.IsSuccessStatusCode, "下载地址 HEAD 失败: " + (int)head.StatusCode);
        Assert.True(head.Content.Headers.ContentLength is > 0, "下载地址没有返回大小");
    }
}