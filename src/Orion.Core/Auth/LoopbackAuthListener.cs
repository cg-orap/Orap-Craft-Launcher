using System.Net;
using System.Net.Sockets;
using System.Text;
using Orion.Core.Diagnostics;

namespace Orion.Core.Auth;

/// <summary>
/// Minimal loopback HTTP listener used to receive an OAuth redirect without any copy/paste.
///
/// Design notes:
/// * A raw <see cref="TcpListener"/> is used instead of <see cref="HttpListener"/> because the
///   latter needs an administrative URL reservation on Windows unless a specific URLACL already
///   exists. Binding <c>127.0.0.1:&lt;ephemeral port&gt;</c> needs no privileges at all.
/// * Only the first request is served, and only the query string is parsed; the response is a
///   small "you can close this window" page so the browser tab finishes cleanly.
/// </summary>
public sealed class LoopbackAuthListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private LoopbackAuthListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    /// <summary><c>http://localhost:&lt;port&gt;/</c> — the redirect URI to register for the app.</summary>
    public string RedirectUri => $"http://localhost:{Port}/";

    public static LoopbackAuthListener Start()
    {
        // Port 0 lets the OS pick a free port; OAuth loopback flows accept any port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        AppLog.Debug("auth", "loopback-started", ("port", port));
        return new LoopbackAuthListener(listener, port);
    }

    /// <summary>
    /// Waits for the browser to hit the redirect URI and returns the parsed query parameters.
    /// Throws <see cref="TimeoutException"/> when the user never finishes in time.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout);

        try
        {
            using var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var requestHead = await ReadRequestHeadAsync(stream, linked.Token).ConfigureAwait(false);
            var requestLine = requestHead.Split("\r\n", 2, StringSplitOptions.None)[0];
            var target = ParseRequestTarget(requestLine);
            var parameters = ParseQuery(target);

            await WriteLandingPageAsync(stream, parameters, linked.Token).ConfigureAwait(false);

            return parameters;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("等待浏览器回调超时。");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("本地回调端口不可用: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Reads the complete HTTP request head (request line plus headers, up to the blank line).
    ///
    /// Draining the whole head matters: closing a socket while unread request bytes remain makes
    /// Windows answer with a TCP reset, and the browser would report a failed/aborted connection
    /// instead of showing the landing page.
    /// </summary>
    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var builder = new StringBuilder(512);
        var single = new byte[1];

        while (builder.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0) break;

            builder.Append((char)single[0]);

            if (builder.Length >= 4 &&
                builder[^4] == '\r' && builder[^3] == '\n' && builder[^2] == '\r' && builder[^1] == '\n')
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string ParseRequestTarget(string requestLine)
    {
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    /// <summary>Parses <c>/?a=b&amp;c=d</c> into a dictionary (keys are case insensitive).</summary>
    public static IReadOnlyDictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var question = target.IndexOf('?');
        var query = question >= 0 ? target[(question + 1)..] : string.Empty;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static async Task WriteLandingPageAsync(
        NetworkStream stream,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var ok = parameters.ContainsKey("code");
        var title = ok ? "登录成功" : "登录未完成";
        var detail = ok
            ? "授权信息已发送给 Orion Launcher，请返回启动器窗口。"
            : parameters.TryGetValue("error_description", out var description)
                ? description
                : "没有收到授权码，请回到启动器重试。";

        var html = $$"""
            <!doctype html>
            <html lang="zh-CN"><head><meta charset="utf-8"><title>Orion Launcher · {{title}}</title>
            <style>
              body { font-family: 'Microsoft YaHei UI', 'Segoe UI', sans-serif; background:#10131a; color:#e9edf5;
                     display:flex; align-items:center; justify-content:center; height:100vh; margin:0 }
              .card { background:#181d27; border:1px solid #2c3646; border-radius:14px; padding:36px 44px; text-align:center }
              h1 { font-size:20px; margin:0 0 12px }
              p { color:#97a3b8; margin:0; font-size:13px }
            </style></head>
            <body><div class="card"><h1>{{title}}</h1><p>{{EncodeHtml(detail)}}</p></div></body></html>
            """;

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string EncodeHtml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

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

        _cts.Dispose();
    }
}
