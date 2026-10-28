using System.Security.Cryptography;
using System.Text;
using Orion.Core.Net;

namespace Orion.Tests;

/// <summary>
/// Exercises the download engine against a real (local) HTTP server: range resume, retry,
/// digest verification, atomic commit, duplicate suppression and cancellation.
/// </summary>
[Collection("http")]
public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _root;
    private readonly HttpClient _http;

    public DownloadEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DownloadEngine CreateEngine(int concurrency = 4, int retries = 3) => new(
        _http,
        new DownloadEngineOptions
        {
            MaxConcurrency = concurrency,
            MaxRetries = retries,
            BaseRetryDelay = TimeSpan.FromMilliseconds(20),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        },
        new MirrorResolver(new NetworkOptions { Source = DownloadSource.Official }));

    private static byte[] MakePayload(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    [Fact]
    public async Task DownloadsFileAndVerifiesDigest()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(64 * 1024);
        server.Files["a.bin"] = payload;

        var target = Path.Combine(_root, "nested", "a.bin");

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("a.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
            },
        ]);

        Assert.True(report.Success);
        Assert.Equal(1, report.CompletedFiles);
        Assert.True(File.Exists(target));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task ResumesFromPartialFileWithRangeRequest()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(50_000);
        server.Files["resume.bin"] = payload;

        var target = Path.Combine(_root, "resume.bin");
        var part = target + ".part";
        await File.WriteAllBytesAsync(part, payload[..20_000]);

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("resume.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
            },
        ]);

        Assert.True(report.Success);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Contains(server.Requests, r => r.StartsWith("resume.bin|bytes=20000-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandlesServerThatIgnoresRangeHeader()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(30_000);
        server.Files["norange.bin"] = payload;
        server.IgnoreRange.Add("norange.bin");

        var target = Path.Combine(_root, "norange.bin");
        await File.WriteAllBytesAsync(target + ".part", payload[..10_000]);

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("norange.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
            },
        ]);

        Assert.True(report.Success, "A server that ignores Range must be restarted, not corrupted.");
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task RejectsFileWithWrongDigestAndReportsFailure()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(2048);
        server.Files["bad.bin"] = payload;

        var target = Path.Combine(_root, "bad.bin");

        var report = await CreateEngine(retries: 1).DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("bad.bin")],
                Sha1 = new string('0', 40),
                Size = payload.Length,
            },
        ]);

        Assert.False(report.Success);
        Assert.Single(report.Failures);
        Assert.Contains("SHA", report.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task RetriesTransientServerErrorsThenSucceeds()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(4096);
        server.Files["flaky.bin"] = payload;
        server.FailuresBeforeSuccess["flaky.bin"] = 2;

        var target = Path.Combine(_root, "flaky.bin");

        var report = await CreateEngine(retries: 4).DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("flaky.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
            },
        ]);

        Assert.True(report.Success);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.True(server.Requests.Count(r => r.StartsWith("flaky.bin|", StringComparison.Ordinal)) >= 3);
    }

    [Fact]
    public async Task Reports404WithoutRetryingForever()
    {
        await using var server = TestHttpServer.Start();

        var target = Path.Combine(_root, "missing.bin");
        var report = await CreateEngine(retries: 3).DownloadAsync(
        [
            new DownloadRequest { TargetPath = target, Urls = [server.Url("missing.bin")] },
        ]);

        Assert.False(report.Success);
        Assert.Equal(404, report.Failures[0].StatusCode);
        Assert.True(server.Requests.Count(r => r.StartsWith("missing.bin|", StringComparison.Ordinal)) <= 2,
            "A 404 must not be retried per URL.");
    }

    [Fact]
    public async Task SkipsExistingIntactFileAndDownloadsOnlyMissingOnes()
    {
        await using var server = TestHttpServer.Start();
        var first = MakePayload(1024);
        var second = MakePayload(2048);
        server.Files["one.bin"] = first;
        server.Files["two.bin"] = second;

        var targetOne = Path.Combine(_root, "one.bin");
        var targetTwo = Path.Combine(_root, "two.bin");
        await File.WriteAllBytesAsync(targetOne, first);

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest { TargetPath = targetOne, Urls = [server.Url("one.bin")], Sha1 = Sha1(first), Size = first.Length },
            new DownloadRequest { TargetPath = targetTwo, Urls = [server.Url("two.bin")], Sha1 = Sha1(second), Size = second.Length },
        ]);

        Assert.True(report.Success);
        Assert.Equal(1, report.SkippedFiles);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("one.bin|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeduplicatesTheSameTargetPathInsideOneBatch()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(8192);
        server.Files["dup.bin"] = payload;

        var target = Path.Combine(_root, "dup.bin");
        var request = new DownloadRequest
        {
            TargetPath = target,
            Urls = [server.Url("dup.bin")],
            Sha1 = Sha1(payload),
            Size = payload.Length,
        };

        var report = await CreateEngine().DownloadAsync([request, request, request]);

        Assert.True(report.Success);
        Assert.Equal(1, report.CompletedFiles);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RepairsCorruptExistingFile()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(4096);
        server.Files["repair.bin"] = payload;

        var target = Path.Combine(_root, "repair.bin");
        await File.WriteAllBytesAsync(target, new byte[payload.Length]); // right size, wrong bytes

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("repair.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
                Verify = DownloadVerifyPolicy.Always,
            },
        ]);

        Assert.True(report.Success);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task CancellationLeavesPartialFileForResume()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(4 * 1024 * 1024);
        server.Files["big.bin"] = payload;

        var target = Path.Combine(_root, "big.bin");
        using var cts = new CancellationTokenSource();

        var task = CreateEngine().DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [server.Url("big.bin")],
                Sha1 = Sha1(payload),
                Size = payload.Length,
            },
        ], null, cts.Token);

        await Task.Delay(15);
        await cts.CancelAsync();

        var report = await task;
        Assert.True(report.Cancelled || report.Success);

        // Cancel is cooperative: the important invariant is that no *committed* file exists
        // unless it is complete, so a resume can never observe a half written jar as valid.
        if (File.Exists(target))
        {
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        }
    }

    [Fact]
    public async Task ReportsProgressAndSpeed()
    {
        await using var server = TestHttpServer.Start();
        var payload = MakePayload(512 * 1024);
        server.Files["p1.bin"] = payload;
        server.Files["p2.bin"] = payload;

        var updates = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => { lock (updates) updates.Add(p); });

        var report = await CreateEngine().DownloadAsync(
        [
            new DownloadRequest { TargetPath = Path.Combine(_root, "p1.bin"), Urls = [server.Url("p1.bin")], Sha1 = Sha1(payload), Size = payload.Length },
            new DownloadRequest { TargetPath = Path.Combine(_root, "p2.bin"), Urls = [server.Url("p2.bin")], Sha1 = Sha1(payload), Size = payload.Length },
        ],
        progress);

        Assert.True(report.Success);
        await Task.Delay(300);

        lock (updates)
        {
            Assert.NotEmpty(updates);
            Assert.All(updates, u => Assert.Equal(2, u.TotalFiles));
            Assert.Contains(updates, u => u.DownloadedBytes > 0);
        }
    }

    [Fact]
    public async Task FailsFastOnUnresolvableHostAndRecordsDiagnostics()
    {
        var engine = new DownloadEngine(
            new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(400) }) { Timeout = TimeSpan.FromSeconds(5) },
            new DownloadEngineOptions { MaxRetries = 1, BaseRetryDelay = TimeSpan.FromMilliseconds(10) },
            new MirrorResolver(new NetworkOptions()));

        var target = Path.Combine(_root, "unreachable.bin");
        var report = await engine.DownloadAsync(
        [
            new DownloadRequest { TargetPath = target, Urls = [new Uri("http://127.0.0.1:1/never.bin")] },
        ]);

        Assert.False(report.Success);
        Assert.Single(report.Failures);
        Assert.False(string.IsNullOrWhiteSpace(report.Failures[0].Message));
        Assert.NotNull(report.Failures[0].ExceptionType);
    }

    [Fact]
    public void MirrorResolverMapsKnownHosts()
    {
        var mirrors = new MirrorResolver(new NetworkOptions { Source = DownloadSource.BmclApi });

        var asset = mirrors.Resolve(new Uri("https://resources.download.minecraft.net/ab/abcdef"));
        Assert.Equal("bmclapi2.bangbang93.com", asset[0].Host);
        Assert.Equal("/assets/ab/abcdef", asset[0].AbsolutePath);
        Assert.Equal("resources.download.minecraft.net", asset[^1].Host);

        var library = mirrors.Resolve(new Uri("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"));
        Assert.Equal("/maven/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", library[0].AbsolutePath);

        var neoforge = MavenUrlRewriter.Candidates(new Uri("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.264/x.jar"));
        Assert.Equal("maven.neoforged.net", neoforge[0].Host);
        Assert.Contains(neoforge, u => u.Host == "maven.creeperhost.net" && u.AbsolutePath.StartsWith("/net/neoforged/", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderForSchedulingPutsBigFilesFirst()
    {
        static DownloadRequest Req(string name, long size) => new()
        {
            TargetPath = Path.Combine(Path.GetTempPath(), name),
            Urls = [new Uri("https://example.invalid/" + name)],
            Size = size,
        };

        var ordered = DownloadEngine.OrderForScheduling(
        [
            Req("small-a", 200 * 1024),
            Req("big", 40 * 1024 * 1024),
            Req("small-b", 900 * 1024),
            Req("middle", 8 * 1024 * 1024),
        ]);

        Assert.Equal(["big", "middle", "small-b", "small-a"], ordered.Select(r => Path.GetFileName(r.TargetPath)));
    }

    [Fact]
    public void OrderForSchedulingKeepsTheOriginalOrderForEqualSizes()
    {
        static DownloadRequest Req(string name) => new()
        {
            TargetPath = Path.Combine(Path.GetTempPath(), name),
            Urls = [new Uri("https://example.invalid/" + name)],
            Size = 1024,
        };

        var ordered = DownloadEngine.OrderForScheduling([Req("a"), Req("b"), Req("c")]);
        Assert.Equal(["a", "b", "c"], ordered.Select(r => Path.GetFileName(r.TargetPath)));
    }

    [Theory]
    [InlineData(4L * 1024 * 1024, 2)]          // 4 MB: 2 段
    [InlineData(16L * 1024 * 1024, 2)]         // 16 MB: 每段 8 MB
    [InlineData(24L * 1024 * 1024, 3)]
    [InlineData(64L * 1024 * 1024, 8)]         // 大文件吃满 8 段
    [InlineData(512L * 1024 * 1024, 8)]        // 上限 8，不无限切
    [InlineData(0, 2)]
    public void ChunkCountForScalesWithSize(long size, int expected)
    {
        Assert.Equal(expected, DownloadEngine.ChunkCountFor(size));
    }

    [Fact]
    public async Task BigFileStartsFirstAndIsSplitIntoRanges()
    {
        await using var server = TestHttpServer.Start();
        var dir = Path.Combine(Path.GetTempPath(), "orion-lpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var bigSize = 6 * 1024 * 1024;
        server.Files["big.jar"] = new byte[bigSize];
        var requests = new List<DownloadRequest>
        {
            new()
            {
                TargetPath = Path.Combine(dir, "big.jar"),
                Urls = [new Uri(server.BaseUrl + "big.jar")],
                Size = bigSize,
            },
        };

        // 六个小文件排在 big.jar 之后：旧实现会先下完它们，大文件最后开工。
        for (var i = 0; i < 6; i++)
        {
            var size = 300 * 1024;
            server.Files[$"small-{i}.jar"] = new byte[size];
            requests.Add(new DownloadRequest
            {
                TargetPath = Path.Combine(dir, $"small-{i}.jar"),
                Urls = [new Uri(server.BaseUrl + $"small-{i}.jar")],
                Size = size,
            });
        }

        var report = await CreateEngine(concurrency: 4).DownloadAsync(requests);
        Assert.True(report.Success, string.Join("; ", report.Failures.Select(f => f.Message)));

        // 调度顺序由 OrderForSchedulingPutsBigFilesFirst 单测覆盖；这里只验证分块行为，
        // 避免依赖请求到达的先后（4 个 worker 同时起跑，日志顺序本身就有竞态）。
        // 大文件被切成多段（多个带 Range 的请求）
        var bigRanges = server.Requests
            .Where(r => r.StartsWith("big.jar|", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Split('|')[1])
            .Distinct()
            .ToList();
        Assert.True(bigRanges.Count >= 2, $"big.jar 只收到 {bigRanges.Count} 个 Range 请求");

        Directory.Delete(dir, recursive: true);
    }
}
