using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;

namespace Orion.Core.Auth;

/// <summary>A pending device-code sign-in (RFC 8628) as returned by Microsoft.</summary>
public sealed record DeviceCodeSession(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? Message,
    int ExpiresInSeconds,
    int IntervalSeconds)
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset ExpiresAt => StartedAt.AddSeconds(Math.Max(60, ExpiresInSeconds));
}

/// <summary>
/// Microsoft identity platform (v2.0) sign-in flows.
///
/// These are the mechanisms mainstream launchers use, and neither of them requires the user to
/// copy a URL back into the app:
///
/// * <b>device code</b> — the launcher shows a short code, the user enters it at
///   <c>https://microsoft.com/link</c> on any device, and the launcher polls for completion;
/// * <b>loopback</b> — the launcher opens the system browser with a <c>http://localhost:port</c>
///   redirect and catches the callback on its own local listener.
///
/// Both need an application registered as a public client (device code: enable "Allow public
/// client flows"; loopback: add the <c>http://localhost</c> redirect). The legacy built-in client
/// id cannot be used with them, so callers must provide one.
/// </summary>
public sealed partial class MicrosoftAuthService
{
    private const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Sign-in method that produced an account, stored so refreshes use the same authority.</summary>
    public const string FlowLegacy = "legacy";
    public const string FlowDeviceCode = "devicecode";
    public const string FlowLoopback = "loopback";

    /// <summary>True when a user registered client id is configured (required by the v2.0 flows).</summary>
    public bool CanUseModernFlows => _options.HasModernClientId;

    /// <summary>Human readable reason the modern flows are unavailable, or null when they are.</summary>
    public string? ModernFlowsUnavailableReason => CanUseModernFlows
        ? null
        : "设备代码流 / 浏览器自动回调需要在「设置 → 微软登录」里填写你自己注册的 Azure 应用 ID（免费，约 2 分钟）。"
          + "内置的客户端 ID 只支持旧版 Live Connect 流程。";

    #region Device code

    /// <summary>Starts a device-code sign-in: returns the code the user must enter in the browser.</summary>
    public async Task<DeviceCodeSession> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        RequireModernClient();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["scope"] = _options.ModernScope,
        });

        using var response = await _http.PostAsync(_options.DeviceCodeEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadErrorParts(body);
            throw new MicrosoftAuthException(
                $"无法启动设备代码登录（{(int)response.StatusCode}）：{description ?? error ?? body}",
                error,
                description);
        }

        var payload = JsonSerializer.Deserialize<DeviceCodeResponse>(body)
                      ?? throw new MicrosoftAuthException("设备代码响应无法解析。", "devicecode_parse");

        if (string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.UserCode))
            throw new MicrosoftAuthException("设备代码响应缺少必要字段。", "devicecode_invalid");

        var session = new DeviceCodeSession(
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUri ?? "https://microsoft.com/link",
            payload.Message,
            payload.ExpiresIn > 0 ? payload.ExpiresIn : 900,
            payload.Interval > 0 ? payload.Interval : 5);

        AppLog.Info("auth", "devicecode-started", ("expiresIn", session.ExpiresInSeconds), ("interval", session.IntervalSeconds));
        return session;
    }

    /// <summary>Polls Microsoft until the user finishes (or declines) the sign-in.</summary>
    public async Task<Account> WaitForDeviceCodeAsync(
        DeviceCodeSession session,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        RequireModernClient();

        var interval = TimeSpan.FromSeconds(Math.Clamp(session.IntervalSeconds, 2, 30));

        while (DateTimeOffset.Now < session.ExpiresAt)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["grant_type"] = DeviceCodeGrant,
                ["device_code"] = session.DeviceCode,
            });

            using var response = await _http.PostAsync(_options.ModernTokenEndpoint, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var token = JsonSerializer.Deserialize<ModernTokenResponse>(body)
                            ?? throw new MicrosoftAuthException("设备代码换取的令牌无法解析。", "devicecode_token_parse");

                if (string.IsNullOrEmpty(token.AccessToken))
                    throw new MicrosoftAuthException("设备代码登录返回了空令牌。", "empty_access_token");

                progress?.Report("授权完成，正在与 Xbox Live / Minecraft 服务交换令牌…");
                return await CompleteLoginAsync(token.AccessToken!, token.RefreshToken, token.ExpiresIn, FlowDeviceCode, null, ct).ConfigureAwait(false);
            }

            var (error, description) = ReadErrorParts(body);

            switch (error?.ToLowerInvariant())
            {
                case "authorization_pending":
                    progress?.Report("等待你在浏览器中完成登录…");
                    continue;

                case "slow_down":
                    // Microsoft asked us to poll less often.
                    interval += TimeSpan.FromSeconds(5);
                    progress?.Report($"轮询过快，改为每 {interval.TotalSeconds:0} 秒检查一次…");
                    continue;

                case "authorization_declined":
                    throw new MicrosoftAuthException("你在浏览器中取消了本次登录。", "authorization_declined", description);

                case "expired_token":
                    throw new MicrosoftAuthException("设备代码已过期，请重新点击登录。", "expired_token", description);

                case "bad_verification_code":
                    throw new MicrosoftAuthException("设备代码无效，请重新点击登录。", "bad_verification_code", description);

                default:
                    throw new MicrosoftAuthException(
                        $"设备代码登录失败：{description ?? error ?? $"HTTP {(int)response.StatusCode}"}",
                        error ?? $"http_{(int)response.StatusCode}",
                        description);
            }
        }

        throw new MicrosoftAuthException("设备代码已过期，请重新点击登录。", "expired_token");
    }

    /// <summary>Convenience: start a device-code sign-in, publish the code, then wait for completion.</summary>
    public async Task<Account> SignInWithDeviceCodeAsync(
        Action<DeviceCodeSession> onCodeReady,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var session = await StartDeviceCodeAsync(ct).ConfigureAwait(false);
        onCodeReady(session);
        return await WaitForDeviceCodeAsync(session, progress, ct).ConfigureAwait(false);
    }

    #endregion

    #region Loopback

    /// <summary>
    /// Opens the system browser and catches the redirect on a local loopback listener, so the
    /// sign-in completes without any copy/paste.
    /// </summary>
    public async Task<Account> SignInWithLoopbackAsync(
        Action<Uri>? onBrowserOpened = null,
        IProgress<string>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        RequireModernClient();

        await using var listener = LoopbackAuthListener.Start();
        var state = Guid.NewGuid().ToString("N");

        var authorizeUri = BuildModernAuthorizeUri(listener.RedirectUri, state);
        onBrowserOpened?.Invoke(authorizeUri);

        progress?.Report($"已打开浏览器。若没有自动打开，请手动访问：{authorizeUri}");

        try
        {
            OpenInBrowser(authorizeUri);
        }
        catch (Exception ex)
        {
            AppLog.Warn("auth", "loopback-browser-open-failed", ("error", ex.Message));
        }

        progress?.Report("等待浏览器完成登录…");
        var parameters = await listener.WaitForCallbackAsync(timeout ?? TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

        if (parameters.TryGetValue("error", out var error))
        {
            parameters.TryGetValue("error_description", out var description);
            throw new MicrosoftAuthException(
                $"登录未完成：{description ?? error}",
                error,
                description);
        }

        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new MicrosoftAuthException("回调里没有授权码。", "missing_code");

        if (parameters.TryGetValue("state", out var returnedState) &&
            !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new MicrosoftAuthException("回调 state 不匹配，已拒绝该响应。", "state_mismatch");
        }

        progress?.Report("已收到授权码，正在交换令牌…");
        var token = await RequestModernTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = listener.RedirectUri,
            ["scope"] = _options.ModernScope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(token.AccessToken!, token.RefreshToken, token.ExpiresIn, FlowLoopback, null, ct).ConfigureAwait(false);
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

    private static void OpenInBrowser(Uri uri)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var process = Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    #endregion

    #region Shared token helpers

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

        return token;
    }

    /// <summary>
    /// Exchanges an authorization code from the v2.0 authorize endpoint (used by the embedded
    /// browser) and completes the chain. This is the flow that yields a token with the
    /// <c>XboxLive.signin</c> scope, which Xbox Live actually accepts.
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

    /// <summary>Refreshes a v2.0 token (device code / loopback accounts).</summary>
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
            account.AuthFlow ?? FlowModernFallback(account),
            account,
            ct).ConfigureAwait(false);
    }

    private static string FlowModernFallback(Account account)
        => account.AuthFlow ?? FlowDeviceCode;

    #endregion

    #region Wire models

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private sealed class ModernTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    #endregion
}
