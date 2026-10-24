using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;

namespace Orion.Core.Auth;

/// <summary>
/// Microsoft identity platform (v2.0) sign-in.
///
/// The launcher uses <b>one</b> interactive method: an embedded browser window that shows the real
/// Microsoft sign-in page and intercepts the OAuth redirect (see <c>LoginWindow</c>). When the user
/// configured their own public-client application id the browser runs the v2.0 authorize flow with
/// the <c>XboxLive.signin</c> scope, whose token Xbox Live accepts; otherwise the legacy
/// <c>login.live.com</c> flow is used with the built-in client id.
/// </summary>
public sealed partial class MicrosoftAuthService
{
    /// <summary>Sign-in method that produced an account, stored so refreshes use the same authority.</summary>
    public const string FlowLegacy = "legacy";
    public const string FlowDeviceCode = "devicecode";
    public const string FlowLoopback = "loopback";

    /// <summary>True when a user registered client id is configured (enables the v2.0 flow).</summary>
    public bool CanUseModernFlows => _options.HasModernClientId;

    /// <summary>Why the v2.0 flow is unavailable, or null when it is ready.</summary>
    public string? ModernFlowsUnavailableReason => CanUseModernFlows
        ? null
        : "未配置 Azure 应用 ID，登录将使用旧版流程；若 Xbox Live 拒绝令牌（HTTP 401），"
          + "请在「设置 → 微软登录」填写你自己注册的应用 ID（免费，约 2 分钟）。";

    /// <summary>
    /// Exchanges an authorization code from the v2.0 authorize endpoint (captured by the embedded
    /// browser) and completes the chain.
    /// </summary>
    public async Task<Account> SignInWithModernCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        RequireModernClient();

        if (string.IsNullOrWhiteSpace(code)) throw new MicrosoftAuthException("授权码为空。", "missing_code");

        var token = await RequestModernTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = _options.ModernScope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(
            token.AccessToken!,
            token.RefreshToken,
            token.ExpiresIn,
            FlowLoopback,
            null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks a free loopback port for the redirect URI.
    ///
    /// The embedded browser intercepts the navigation before it reaches the network, so no listener
    /// has to stay open; the port only has to be a plausible loopback port that Microsoft accepts
    /// for native clients (<c>http://localhost</c> with any port).
    /// </summary>
    public static int PickLoopbackPort()
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Random.Shared.Next(30000, 45000);
        }
    }

    public Uri BuildModernAuthorizeUri(string redirectUri, string state)
    {
        var query = new StringBuilder();
        query.Append("client_id=").Append(Uri.EscapeDataString(_options.ClientId));
        query.Append("&response_type=code");
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        query.Append("&scope=").Append(Uri.EscapeDataString(_options.ModernScope));
        query.Append("&prompt=select_account");
        query.Append("&state=").Append(Uri.EscapeDataString(state));

        return new Uri(_options.ModernAuthorizeEndpoint + "?" + query);
    }

    /// <summary>Refreshes a v2.0 token (accounts created by the v2.0 browser flow).</summary>
    private async Task<Account> RefreshModernAsync(Account account, CancellationToken ct)
    {
        var token = await RequestModernTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken!,
            ["scope"] = _options.ModernScope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(
            token.AccessToken!,
            token.RefreshToken ?? account.RefreshToken,
            token.ExpiresIn,
            account.AuthFlow ?? FlowLoopback,
            account,
            ct).ConfigureAwait(false);
    }

    private void RequireModernClient()
    {
        if (CanUseModernFlows) return;

        throw new MicrosoftAuthException(
            ModernFlowsUnavailableReason!,
            "missing_client_id",
            ModernFlowsUnavailableReason);
    }

    private async Task<ModernTokenResponse> RequestModernTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(_options.ModernTokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadErrorParts(body);
            throw new MicrosoftAuthException(
                $"微软登录失败：{description ?? error ?? $"HTTP {(int)response.StatusCode}"}",
                error ?? $"http_{(int)response.StatusCode}",
                description);
        }

        var token = JsonSerializer.Deserialize<ModernTokenResponse>(body)
                    ?? throw new MicrosoftAuthException("令牌响应无法解析。", "token_parse");

        if (string.IsNullOrEmpty(token.AccessToken))
            throw new MicrosoftAuthException("微软返回了空令牌。", "empty_access_token");

        AppLog.Info("auth", "modern-token-granted", ("scope", token.Scope ?? "<not reported>"));

        return token;
    }

    private sealed class ModernTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }
}
