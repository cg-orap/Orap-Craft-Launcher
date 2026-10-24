using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Orion.Tests;

/// <summary>
/// A minimal HTTP/1.1 server for download-engine tests.
///
/// <see cref="HttpListener"/> needs an administrative URL reservation on Windows, so a raw
/// <see cref="TcpListener"/> is used instead: it binds an ephemeral loopback port without any
/// privileges. Behaviour (range support, injected failures, 404s) is configurable per path.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public TestHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>Path (without leading slash) to body.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that deliberately ignore the <c>Range</c> header.</summary>
    public HashSet<string> IgnoreRange { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remaining failed responses to emit before succeeding.</summary>
    public Dictionary<string, int> FailuresBeforeSuccess { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests received, as <c>path|range-header</c>.</summary>
    public List<string> Requests { get; } = [];

    public static TestHttpServer Start() => new();

    public Uri Url(string path) => new(BaseUrl + path.TrimStart('/'));

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            await using var stream = client.GetStream();

            var headerBuffer = new List<byte>(1024);
            var one = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(one, _cts.Token).ConfigureAwait(false);
                if (read == 0) return;

                headerBuffer.Add(one[0]);
                if (headerBuffer.Count >= 4 &&
                    headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                    headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n')
                {
                    break;
                }

                if (headerBuffer.Count > 64 * 1024) return;
            }

            var header = Encoding.ASCII.GetString(headerBuffer.ToArray());
            var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return;

            var path = requestLine[1].TrimStart('/');
            var queryIndex = path.IndexOf('?');
            if (queryIndex >= 0) path = path[..queryIndex];

            string? rangeHeader = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line[..colon].Equals("Range", StringComparison.OrdinalIgnoreCase))
                {
                    rangeHeader = line[(colon + 1)..].Trim();
                }
            }

            lock (Requests) Requests.Add($"{path}|{rangeHeader ?? ""}");

            if (FailuresBeforeSuccess.TryGetValue(path, out var remaining) && remaining > 0)
            {
                FailuresBeforeSuccess[path] = remaining - 1;
                await WriteAsync(stream, "503 Service Unavailable", [], 0, isPartial: false).ConfigureAwait(false);
                return;
            }

            if (!Files.TryGetValue(path, out var body))
            {
                await WriteAsync(stream, "404 Not Found", Encoding.UTF8.GetBytes("missing"), 0, false).ConfigureAwait(false);
                return;
            }

            long start = 0;
            var wantRange = rangeHeader is not null && !IgnoreRange.Contains(path);

            if (wantRange)
            {
                // Format: bytes=<start>-
                var spec = rangeHeader!;
                var equals = spec.IndexOf('=');
                var value = equals >= 0 ? spec[(equals + 1)..] : spec;
                var dash = value.IndexOf('-');
                if (dash > 0 && long.TryParse(value[..dash], out var parsedStart))
                {
                    start = parsedStart;
                }

                if (start >= body.Length)
                {
                    await WriteAsync(stream, "416 Range Not Satisfiable", [], 0, false).ConfigureAwait(false);
                    return;
                }

                var slice = body[(int)start..];
                await WriteAsync(stream, "206 Partial Content", slice, start, true, body.Length).ConfigureAwait(false);
                return;
            }

            await WriteAsync(stream, "200 OK", body, 0, false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        string status,
        byte[] body,
        long rangeStart,
        bool isPartial,
        int totalLength = 0)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        builder.Append("Accept-Ranges: bytes\r\n");
        builder.Append("Connection: close\r\n");

        if (isPartial)
        {
            builder.Append("Content-Range: bytes ")
                .Append(rangeStart).Append('-').Append(rangeStart + body.Length - 1)
                .Append('/').Append(totalLength).Append("\r\n");
        }

        builder.Append("\r\n");

        var header = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(header).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _cts.Dispose();
    }
}
