using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;

namespace Orion.Core.Auth;

public sealed class MicrosoftAuthOptions
{
    /// <summary>
    /// Public client id used for the classic Live Connect "desktop" flow. Deployments that
    /// distribute the launcher widely should register their own Azure application and set
    /// this value (plus <see cref="RedirectUri"/>) instead of sharing this one.
    /// </summary>
    public string ClientId { get; set; } = "00000000402b5328";

    public string RedirectUri { get; set; } = "https://login.live.com/oauth20_desktop.srf";

    public string Scope { get; set; } = "service::user.auth.xboxlive.com::MBI_SSL";

    public string AuthorizeEndpoint { get; set; } = "https://login.live.com/oauth20_authorize.srf";
    public string TokenEndpoint { get; set; } = "https://login.live.com/oauth20_token.srf";
    public string XboxLiveEndpoint { get; set; } = "https://user.auth.xboxlive.com/user/authenticate";
    public string XstsEndpoint { get; set; } = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public string MinecraftEndpoint { get; set; } = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public string ProfileEndpoint { get; set; } = "https://api.minecraftservices.com/minecraft/profile";

    public string XboxRelyingParty { get; set; } = "http://auth.xboxlive.com";
    public string XstsRelyingParty { get; set; } = "rp://api.minecraftservices.com/";

    /// <summary>Subtracted from the reported token lifetime so we refresh slightly early.</summary>
    public TimeSpan ExpirySafetyMargin { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class MicrosoftAuthException(string message, string? errorCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Microsoft account authentication:
/// Live Connect OAuth 2.0 authorization code → Xbox Live user token → XSTS token →
/// Minecraft services token → Minecraft profile.
///
/// Only documented, first-party endpoints are used. No token is ever written to a log.
/// </summary>
public sealed class MicrosoftAuthService
{
    private readonly HttpClient _http;
    private readonly MicrosoftAuthOptions _options;

    public MicrosoftAuthService(HttpClient http, MicrosoftAuthOptions? options = null)
    {
        _http = http;
        _options = options ?? new MicrosoftAuthOptions();
    }

    public MicrosoftAuthOptions Options => _options;

    /// <summary>The URL the user must open in a browser to sign in.</summary>
    public Uri BuildAuthorizationUri(string state = "orion")
    {
        var query = new StringBuilder();
        query.Append("client_id=").Append(Uri.EscapeDataString(_options.ClientId));
        query.Append("&response_type=code");
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(_options.RedirectUri));
        query.Append("&scope=").Append(Uri.EscapeDataString(_options.Scope));
        query.Append("&display=touch&locale=zh_CN&prompt=select_account");
        if (!string.IsNullOrEmpty(state)) query.Append("&state=").Append(Uri.EscapeDataString(state));

        return new Uri(_options.AuthorizeEndpoint + "?" + query);
    }

    /// <summary>
    /// Extracts the authorization code from whatever the user pastes back.
    ///
    /// Real-world paste material is messy, so all of these are accepted:
    /// a full <c>https://login.live.com/oauth20_desktop.srf?code=...</c> link,
    /// the same link without its scheme, a bare <c>code=...</c> fragment,
    /// text surrounded by quotes / angle brackets / line breaks, and the code on its own
    /// (which is what the Live Connect desktop page displays for manual copying).
    ///
    /// Returns <c>null</c> when the input carries an OAuth error instead of a code — use
    /// <see cref="TryGetAuthorizationError"/> to explain why.
    /// </summary>
    public static string? TryExtractCode(string? input)
    {
        var text = Normalize(input);
        if (text is null) return null;

        // A cancelled/failed login still produces a redirect URL; never treat it as a code.
        if (TryGetAuthorizationError(text) is not null) return null;

        var fromUrl = FirstParameter(text, "code");
        if (!string.IsNullOrWhiteSpace(fromUrl)) return fromUrl;

        return LooksLikeBareCode(text) ? text : null;
    }

    /// <summary>
    /// Returns a human readable description when the pasted URL is an OAuth error callback
    /// (<c>error=access_denied&amp;error_description=...</c>), otherwise <c>null</c>.
    /// </summary>
    public static string? TryGetAuthorizationError(string? input)
    {
        var text = Normalize(input);
        if (text is null) return null;

        var error = FirstParameter(text, "error");
        if (string.IsNullOrWhiteSpace(error)) return null;

        // Query strings encode spaces as '+'; the message is shown to the user, so decode them.
        var description = FirstParameter(text, "error_description")?.Replace('+', ' ');
        return string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}";
    }

    /// <summary>
    /// Trims quotes/brackets and collapses the line breaks browsers insert when a long URL is
    /// copied from a wrapped address bar.
    /// </summary>
    private static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim()
            .Trim('"', '\'', '<', '>', '“', '”', '‘', '’', '「', '」')
            .Trim();

        text = text.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal)
                   .Replace("\t", string.Empty, StringComparison.Ordinal)
                   .Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>Finds <c>key=value</c> anywhere in a URL or fragment, tolerating a missing scheme.</summary>
    private static string? FirstParameter(string text, string key)
    {
        var token = key + "=";
        var index = 0;

        while (index < text.Length)
        {
            var at = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;

            index = at + token.Length;

            // Only treat it as a parameter when it starts a query/fragment segment, so that a
            // value like "...&redirect=code" cannot be mistaken for the parameter itself.
            if (at > 0 && text[at - 1] is not ('?' or '&' or '#' or ';')) continue;

            var start = at + token.Length;
            var end = start;
            while (end < text.Length && text[end] is not ('&' or '#' or '"' or '\'' or ' ' or '\t')) end++;

            var raw = text[start..end];
            if (raw.Length == 0) continue;

            return SafeUnescape(raw);
        }

        return null;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return value;
        }
    }

    /// <summary>
    /// Heuristic for a raw code: Microsoft codes look like <c>M.C50A_BAY.2.U.xxxx</c>, i.e. only
    /// URL-safe characters. Anything containing spaces or a path separator is not a bare code.
    /// </summary>
    private static bool LooksLikeBareCode(string text)
        => text.Length is >= 10 and <= 4096
           && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/' or '=');

    public async Task<Account> AuthenticateWithCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new MicrosoftAuthException("授权码为空。");

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(token, null, ct).ConfigureAwait(false);
    }

    /// <summary>Exchanges a stored refresh token for a fresh account (Microsoft token rotation).</summary>
    public async Task<Account> RefreshAsync(Account account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new MicrosoftAuthException("该账户没有可用的刷新令牌，需要重新登录。", "no_refresh_token");

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token",
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(token, account, ct).ConfigureAwait(false);
    }

    private async Task<Account> CompleteLoginAsync(LiveTokenResponse token, Account? previous, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.AccessToken))
            throw new MicrosoftAuthException("微软返回的访问令牌为空。", "empty_access_token");

        // 1. Xbox Live user token
        var (xblToken, uhs) = await RequestXboxLiveTokenAsync(token.AccessToken, ct).ConfigureAwait(false);

        // 2. XSTS token
        var xsts = await RequestXstsTokenAsync(xblToken, ct).ConfigureAwait(false);

        // 3. Minecraft services token
        var minecraft = await RequestMinecraftTokenAsync(xsts.Token, xsts.UserHash ?? uhs, ct).ConfigureAwait(false);

        // 4. Profile (name + uuid). A 404 here means the account owns no Java Edition.
        var profile = await RequestProfileAsync(minecraft.AccessToken, ct).ConfigureAwait(false);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, minecraft.ExpiresIn)) - _options.ExpirySafetyMargin;

        var account = new Account
        {
            Id = profile?.Id ?? previous?.Id ?? Guid.NewGuid().ToString("N"),
            Username = profile?.Name ?? previous?.Username ?? "Player",
            Uuid = profile?.Id ?? previous?.Uuid ?? "",
            Type = AccountType.Microsoft,
            AccessToken = minecraft.AccessToken,
            RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? previous?.RefreshToken : token.RefreshToken,
            XboxUserId = xsts.UserHash ?? uhs,
            ExpiresAt = expiresAt,
            SkinModel = profile?.Skins?.FirstOrDefault()?.Variant,
            LastUsedAt = DateTimeOffset.UtcNow,
        };

        AppLog.Info("auth", "microsoft-login-ok", ("name", account.Username), ("uuid", account.Uuid), ("expiresAt", account.ExpiresAt));
        return account;
    }

    private async Task<LiveTokenResponse> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(_options.TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryReadError(body);
            throw new MicrosoftAuthException(
                $"微软登录失败: {error ?? $"HTTP {(int)response.StatusCode}"}",
                error,
                new HttpRequestException($"HTTP {(int)response.StatusCode}"));
        }

        var token = JsonSerializer.Deserialize<LiveTokenResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new MicrosoftAuthException("无法解析微软返回的令牌。", "parse_error");

        return token;
    }

    private async Task<(string Token, string UserHash)> RequestXboxLiveTokenAsync(string accessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + accessToken,
            },
            RelyingParty = _options.XboxRelyingParty,
            TokenType = "JWT",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.XboxLiveEndpoint)
        {
            Content = JsonContent(payload),
        };
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new MicrosoftAuthException($"Xbox Live 认证失败 (HTTP {(int)response.StatusCode})。", "xbl_failed");

        var parsed = JsonSerializer.Deserialize<XboxTokenResponse>(body);
        var token = parsed?.Token;
        var uhs = parsed?.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash;

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
            throw new MicrosoftAuthException("Xbox Live 返回的令牌无效。", "xbl_invalid");

        return (token, uhs);
    }

    private async Task<(string Token, string? UserHash)> RequestXstsTokenAsync(string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = _options.XstsRelyingParty,
            TokenType = "JWT",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.XstsEndpoint)
        {
            Content = JsonContent(payload),
        };
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var xerr = TryReadXerr(body);
            var message = xerr switch
            {
                2148916233 => "该微软账户尚未创建 Xbox 档案，请先在 xbox.com 登录一次。",
                2148916235 => "Xbox Live 在您所在的国家或地区不可用。",
                2148916236 or 2148916237 => "该账户需要在 Xbox 网站上完成成人身份验证。",
                2148916238 => "该账户是未成年人账户，需要先加入 Microsoft 家庭。",
                _ => $"XSTS 授权失败 (HTTP {(int)response.StatusCode}{(xerr is null ? "" : $", XErr {xerr}")})。",
            };

            throw new MicrosoftAuthException(message, xerr?.ToString());
        }

        var parsed = JsonSerializer.Deserialize<XboxTokenResponse>(body);
        if (string.IsNullOrEmpty(parsed?.Token))
            throw new MicrosoftAuthException("XSTS 返回的令牌无效。", "xsts_invalid");

        return (parsed.Token, parsed.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash);
    }

    private async Task<MinecraftTokenResponse> RequestMinecraftTokenAsync(string xstsToken, string userHash, CancellationToken ct)
    {
        var payload = new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.MinecraftEndpoint)
        {
            Content = JsonContent(payload),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryReadError(body);
            throw new MicrosoftAuthException(
                $"Minecraft 服务认证失败 (HTTP {(int)response.StatusCode}{(error is null ? "" : $": {error}")})。",
                error);
        }

        var parsed = JsonSerializer.Deserialize<MinecraftTokenResponse>(body);
        if (string.IsNullOrEmpty(parsed?.AccessToken))
            throw new MicrosoftAuthException("Minecraft 服务返回的令牌无效。", "mc_invalid");

        return parsed;
    }

    private async Task<MinecraftProfile?> RequestProfileAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.ProfileEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MicrosoftAuthException("该 Microsoft 账户未拥有 Minecraft: Java Edition。", "not_entitled");

        if (!response.IsSuccessStatusCode)
            throw new MicrosoftAuthException($"无法读取 Minecraft 档案 (HTTP {(int)response.StatusCode})。", "profile_failed");

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<MinecraftProfile>(body);

        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            throw new MicrosoftAuthException("Minecraft 档案数据无效。", "profile_invalid");

        return profile;
    }

    private static StringContent JsonContent<T>(T payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string? TryReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var description = document.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                return string.IsNullOrWhiteSpace(description) ? error.ToString() : $"{error}: {description}";
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static long? TryReadXerr(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("XErr", out var xerr) && xerr.TryGetInt64(out var value))
                return value;
        }
        catch (JsonException)
        {
        }

        return null;
    }

    #region Wire models

    private sealed class LiveTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }

    private sealed class XboxTokenResponse
    {
        [JsonPropertyName("Token")] public string? Token { get; set; }
        [JsonPropertyName("DisplayClaims")] public DisplayClaims? DisplayClaims { get; set; }
    }

    private sealed class DisplayClaims
    {
        [JsonPropertyName("xui")] public List<XuiClaim>? Xui { get; set; }
    }

    private sealed class XuiClaim
    {
        [JsonPropertyName("uhs")] public string? UserHash { get; set; }
    }

    private sealed class MinecraftTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }

    private sealed class MinecraftProfile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("skins")] public List<MinecraftSkin>? Skins { get; set; }
    }

    private sealed class MinecraftSkin
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("variant")] public string? Variant { get; set; }
    }

    #endregion
}

/// <summary>Offline ("single player") accounts.</summary>
public static class OfflineAccounts
{
    /// <summary>Vanilla-style name rules: 3-16 characters, letters/digits/underscore.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length is >= 3 and <= 16 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static Account Create(string username)
    {
        if (!IsValidName(username))
            throw new ArgumentException("离线用户名需为 3-16 位字母、数字或下划线。", nameof(username));

        return new Account
        {
            Id = "offline:" + username.ToLowerInvariant(),
            Username = username,
            Uuid = Util.OfflineUuid.FromPlayerName(username).ToString("N"),
            Type = AccountType.Offline,

            // The conventional offline token: the client will not contact Mojang's session
            // services, which also means online-mode servers will reject it. This launcher
            // never attempts to bypass online-mode authentication.
            AccessToken = "0",
            XboxUserId = null,
            ExpiresAt = null,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
    }
}
