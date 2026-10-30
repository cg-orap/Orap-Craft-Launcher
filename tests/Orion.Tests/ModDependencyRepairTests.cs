using System.IO.Compression;
using System.Net;
using System.Text;
using Orion.Core.Mods;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// 自动补前置：CurseForge 不提供 modId→项目 的映射，所以必须"下载后读 jar 里的 modId 核对"。
/// 这里用一个假的 HTTP 层验证：候选里混着同名但 modId 不对的项目，只能装对的那个。
/// </summary>
public sealed class ModDependencyRepairTests
{
    [Fact]
    public async Task InstallsOnlyTheCandidateWhoseJarProvidesTheModId()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), "orap-repair-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(modsRoot, "mods");
        var cache = Path.Combine(modsRoot, ".cache");
        Directory.CreateDirectory(mods);

        try
        {
            var handler = new FakeCurseForge(Environment.CurrentDirectory);
            using var http = new HttpClient(handler);
            var client = new CurseForgeClient(http, "test-key");
            var repair = new ModDependencyRepair(client, http);

            var missing = new[] { new RequiredDependency("simulated", "[1.0.1,)", "tracks") };
            var result = await repair.RepairAsync(modsRoot, missing, "1.21.1", 6, cache);

            Assert.Single(result.Installed);
            Assert.Empty(result.Failed);

            // 装进来的是 modId 正确的那一个
            var jar = Assert.Single(Directory.GetFiles(mods));
            Assert.Equal("simulated-neoforge-1.21.1-1.3.2.jar", Path.GetFileName(jar));

            var scan = ModDependencyScanner.Scan(mods);
            Assert.Empty(scan.Missing);
        }
        finally
        {
            try { Directory.Delete(modsRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReportsFailureWhenNothingMatches()
    {
        var modsRoot = Path.Combine(Path.GetTempPath(), "orap-repair-none-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(modsRoot, "mods");
        Directory.CreateDirectory(mods);

        try
        {
            // 先放一个"需要 does_not_exist 但没装"的模组，补完之后 rescan 仍能看见缺口
            WriteJar(Path.Combine(mods, "tracks.jar"),
                "[[mods]]\nmodId = \"tracks\"\nversion = \"1.0.1\"\n\n" +
                "[[dependencies.tracks]]\nmodId = \"does_not_exist\"\nmandatory = true\nversionRange = \"[1,)\"\n");

            using var http = new HttpClient(new EmptyHandler());
            var client = new CurseForgeClient(http, "test-key");
            var repair = new ModDependencyRepair(client, http);

            var missing = new[] { new RequiredDependency("does_not_exist", "[1,)", "tracks") };
            var result = await repair.RepairAsync(modsRoot, missing, "1.21.1", 6, Path.Combine(modsRoot, ".cache"));

            Assert.Empty(result.Installed);
            Assert.Single(result.Failed);
            Assert.Single(result.Remaining);
        }
        finally
        {
            try { Directory.Delete(modsRoot, recursive: true); } catch { }
        }
    }


    /// <summary>
    /// 真实网络探针（CF_PROBE=1 时运行）：用本机 DPAPI 保护的 Key，真的去 CurseForge 补一个前置。
    /// 验证的是"下载 → 读 modId 核对 → 落地"整条链路，而不是只验证 HTTP 调用成功。
    /// </summary>
    [Fact]
    public async Task LiveRepairInstallsARealPrerequisite()
    {
        if (Environment.GetEnvironmentVariable("CF_PROBE") != "1") return;

        const string settings = @"D:\gd\orion\settings.json";
        if (!File.Exists(settings)) return;

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
        var protectedKey = doc.RootElement.TryGetProperty("curseForgeKeyProtected", out var p) ? p.GetString() : null;
        var key = Orion.Core.Util.SecretProtector.Unprotect(protectedKey);
        Assert.False(string.IsNullOrWhiteSpace(key));

        var root = Path.Combine(Path.GetTempPath(), "orap-live-repair-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);

        try
        {
            WriteJar(Path.Combine(mods, "needs-simulated.jar"),
                "[[mods]]\nmodId = \"fixture\"\nversion = \"1.0.0\"\n\n" +
                "[[dependencies.fixture]]\nmodId = \"simulated\"\nmandatory = true\nversionRange = \"[1.0.1,)\"\n");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var client = new CurseForgeClient(http, key);
            var repair = new ModDependencyRepair(client, http);

            var scan = ModDependencyScanner.Scan(mods);
            Assert.Contains(scan.Missing, m => m.ModId == "simulated");

            var result = await repair.RepairAsync(root, scan.Missing, "1.21.1", 6, Path.Combine(root, ".cache"));

            Assert.True(result.Installed.Count > 0, "没有补到 simulated：" + string.Join("；", result.Failed));
            var detail = "已装文件：" + string.Join("、", Directory.GetFiles(mods).Select(Path.GetFileName));
            var ids = Directory.GetFiles(mods).SelectMany(f => ModDependencyRepair.ReadAllModIds(f))
                .Select(m => m.ModId + "@" + m.Version);
            Assert.True(result.Remaining.Count == 0,
                "remaining=" + string.Join(",", result.Remaining.Select(r => r.ModId)) +
                " | files=" + string.Join(",", Directory.GetFiles(mods).Select(Path.GetFileName)) +
                " | modIds=" + string.Join(",", ids) + " | " + detail);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>bundled 发行版：外层是 aeronautics_bundled，simulated/offroad 在 JarJar 内嵌的 jar 里。</summary>
    [Fact]
    public void BundledJarSatisfiesNestedModIds()
    {
        const string bundled = @"D:\gd\orion\instances\mechanomania-航空学-ac3356\minecraft\mods\create-aeronautics-bundled-1.21.1-1.3.2.jar";
        if (!File.Exists(bundled)) return;

        var all = ModDependencyRepair.ReadAllModIds(bundled);
        Assert.Contains(all, m => m.ModId == "aeronautics_bundled");
        Assert.Contains(all, m => m.ModId == "simulated");
        Assert.Contains(all, m => m.ModId == "offroad");

        var simulated = ModDependencyRepair.ReadModId(bundled, "simulated");
        Assert.NotNull(simulated);
        Assert.Equal("simulated", simulated!.Value.ModId);
        Assert.True(VersionRange.Satisfies(simulated.Value.Version, "[1.0.1,)"));
    }
    /// <summary>假 CurseForge：两个同名候选，只有第二个的 jar 里 modId = simulated。</summary>
    private sealed class FakeCurseForge(string ignored) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("mods/search", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                {"data":[
                  {"id":111,"name":"Simulated Skies","summary":"wrong mod","downloadCount":10},
                  {"id":222,"name":"Create Propulsion: Simulated","summary":"right mod","downloadCount":20}
                ],"pagination":{"index":0,"pageSize":5,"resultCount":2,"totalCount":2}}
                """);
            }

            if (url.Contains("/files?", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/files?pageSize", StringComparison.OrdinalIgnoreCase))
            {
                var modId = url.Contains("/mods/111/", StringComparison.Ordinal) ? 111 : 222;
                var fileName = modId == 111
                    ? "simulated-skies-1.0.jar"
                    : "simulated-neoforge-1.21.1-1.3.2.jar";

                return Json($$"""
                {"data":[{"id":1,"modId":{{modId}},"fileName":"{{fileName}}","releaseType":1,
                  "fileDate":"2026-01-01T00:00:00Z","downloadUrl":"https://cdn.example.invalid/{{fileName}}",
                  "gameVersions":["1.21.1","NeoForge"]}]}
                """);
            }

            if (url.Contains("download-url", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"data":"https://cdn.example.invalid/simulated-neoforge-1.21.1-1.3.2.jar"}""");
            }

            if (url.Contains("cdn.example.invalid", StringComparison.OrdinalIgnoreCase))
            {
                var isWrong = url.Contains("simulated-skies", StringComparison.OrdinalIgnoreCase);
                var bytes = BuildJar(isWrong ? "simulated_skies" : "simulated", "1.3.2");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }

            return Json("""{"data":[]}""");
        }

        private static Task<HttpResponseMessage> Json(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json"),
            });
    }

    private static void WriteJar(string path, string toml)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("META-INF/neoforge.mods.toml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(toml);
    }

    /// <summary>造一个带 mods.toml 的 jar（足够被校验逻辑读出来）。</summary>
    private static byte[] BuildJar(string modId, string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("META-INF/neoforge.mods.toml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write($"""
            [[mods]]
            modId = "{modId}"
            version = "{version}"
            """);
        }

        return stream.ToArray();
    }
}
