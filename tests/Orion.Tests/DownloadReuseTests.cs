using System.Net;
using System.Security.Cryptography;
using System.Text;
using Orion.Core.Net;
using Xunit;

namespace Orion.Tests;

/// <summary>
/// Verified reuse: a cached file is only kept when its SHA-1 matches the metadata. A same-sized but
/// corrupted file must be repaired instead of being inherited by every instance of that version.
/// </summary>
public sealed class DownloadReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orion-reuse-" + Guid.NewGuid().ToString("N")[..8]);

    public DownloadReuseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static DownloadEngine Engine(out StubHandler handler)
    {
        handler = new StubHandler(Encoding.UTF8.GetBytes("payload-0123456789"));
        return new DownloadEngine(
            new HttpClient(handler),
            new DownloadEngineOptions { MaxConcurrency = 2, MaxRetries = 0 },
            new MirrorResolver(new NetworkOptions()));
    }

    [Fact]
    public async Task ValidCachedFileIsReusedWithoutAnyRequest()
    {
        var engine = Engine(out var handler);
        var body = Encoding.UTF8.GetBytes("payload-0123456789");
        var target = Path.Combine(_root, "lib.jar");
        await File.WriteAllBytesAsync(target, body);

        var report = await engine.DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [new Uri("https://example.invalid/lib.jar")],
                Sha1 = Sha1Of(body),
                Size = body.Length,
                Verify = DownloadVerifyPolicy.Always,
            },
        ], null, default);

        Assert.True(report.Success);
        Assert.Equal(1, report.SkippedFiles);
        Assert.Equal(body.Length, report.SkippedBytes);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CorruptedFileWithMatchingSizeIsDownloadedAgain()
    {
        var engine = Engine(out var handler);
        var body = Encoding.UTF8.GetBytes("payload-0123456789");
        var target = Path.Combine(_root, "lib.jar");

        // Same length, wrong content: only a hash check can notice.
        await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("PAYLOAD-0123456789"));

        var report = await engine.DownloadAsync(
        [
            new DownloadRequest
            {
                TargetPath = target,
                Urls = [new Uri("https://example.invalid/lib.jar")],
                Sha1 = Sha1Of(body),
                Size = body.Length,
                Verify = DownloadVerifyPolicy.Always,
            },
        ], null, default);

        Assert.True(report.Success);
        Assert.Equal(0, report.SkippedFiles);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(body, await File.ReadAllBytesAsync(target));
    }

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }
}