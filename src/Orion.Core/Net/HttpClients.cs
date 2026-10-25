using System.Net;

namespace Orion.Core.Net;

/// <summary>User configurable network settings.</summary>
public sealed class NetworkOptions
{
    public const string DefaultUserAgent = "OrapCraftLauncher/0.1.0 (Minecraft: Java Edition launcher; +https://example.invalid/orion)";

    public string UserAgent { get; set; } = DefaultUserAgent;

    /// <summary>Optional proxy, e.g. <c>http://127.0.0.1:7890</c>. Empty disables the proxy.</summary>
    public string? ProxyUrl { get; set; }

    public int MaxConnectionsPerServer { get; set; } = 64;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(12);

    public DownloadSource Source { get; set; } = DownloadSource.Official;

    /// <summary>Extra mirror base used when <see cref="Source"/> is <see cref="DownloadSource.Custom"/>.</summary>
    public string? CustomMirrorBase { get; set; }

    public NetworkOptions Clone() => (NetworkOptions)MemberwiseClone();
}

/// <summary>
/// Central place that builds <see cref="HttpClient"/> instances.
///
/// TLS certificate validation is deliberately left at the platform default: there is no
/// switch, environment variable or setting that disables it.
/// </summary>
public static class HttpClients
{
    public static HttpClient Create(NetworkOptions options) => new(CreateHandler(options), disposeHandler: true)
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", string.IsNullOrWhiteSpace(options.UserAgent) ? NetworkOptions.DefaultUserAgent : options.UserAgent },
        },
    };

    public static SocketsHttpHandler CreateHandler(NetworkOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Clamp(options.MaxConnectionsPerServer, 1, 64),
            ConnectTimeout = options.ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            EnableMultipleHttp2Connections = true,
        };

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            if (!Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var proxyUri))
                throw new ArgumentException($"代理地址无效: {options.ProxyUrl}", nameof(options));

            handler.Proxy = new WebProxy(proxyUri)
            {
                BypassProxyOnLocal = true,
            };
            handler.UseProxy = true;
        }

        return handler;
    }
}
