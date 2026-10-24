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

    /// <summary>
    /// Microsoft identity platform (v2.0) authority. The modern device-code and loopback flows
    /// target this, because the legacy Live Connect endpoints (<c>login.live.com/oauth20_*</c>)
    /// are the reason login kept failing for users.
    /// </summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/consumers";

    /// <summary>Scope for the v2.0 flows: Xbox Live sign-in plus a refresh token.</summary>
    public string ModernScope { get; set; } = "XboxLive.signin offline_access";

    public string DeviceCodeEndpoint => Authority.TrimEnd('/') + "/oauth2/v2.0/devicecode";
    public string ModernTokenEndpoint => Authority.TrimEnd('/') + "/oauth2/v2.0/token";
    public string ModernAuthorizeEndpoint => Authority.TrimEnd('/') + "/oauth2/v2.0/authorize";

    /// <summary>
    /// The built-in client id belongs to the legacy Live Connect flow. Device code and loopback
    /// require an application registered by the user (public client), so this flag lets the UI
    /// explain that instead of failing with an opaque <c>invalid_client</c>.
    /// </summary>
    public bool HasModernClientId =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !ClientId.Equals("00000000402b5328", StringComparison.OrdinalIgnoreCase);

    /// <summary>Subtracted from the reported token lifetime so we refresh slightly early.</summary>
    public TimeSpan ExpirySafetyMargin { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Authentication failure carrying the OAuth error identifier so callers can react correctly
/// (for example "the authorization code expired, a retry with the same code can never work").
/// </summary>
public sealed class MicrosoftAuthException(
    string message,
    string? errorCode = null,
    string? errorDescription = null,
    Exception? inner = null) : Exception(message, inner)
{
    /// <summary>OAuth error identifier such as <c>invalid_grant</c>, or an internal marker.</summary>
    public string? ErrorCode { get; } = errorCode;

    /// <summary>Provider supplied description, already decoded and safe to show.</summary>
    public string? ErrorDescription { get; } = errorDescription;

    /// <summary>
    /// True when the provider rejected the authorization code itself (expired, already used or
    /// malformed). Retrying with the same code is pointless — the user must log in again.
    /// </summary>
    public bool IsAuthorizationCodeRejected =>
        string.Equals(ErrorCode, "invalid_grant", StringComparison.OrdinalIgnoreCase)
        && (ErrorDescription ?? string.Empty).Contains("code", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns an authentication failure into an actionable message.
///
/// The important case is <c>invalid_grant</c> on the authorization code: an OAuth code is
/// single use and expires within minutes, so a retry with the same code can never succeed. The
/// user must be told to log in again instead of clicking "finish" repeatedly.
/// </summary>
public static class MicrosoftAuthMessages
{
    public static string Describe(MicrosoftAuthException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.IsAuthorizationCodeRejected)
        {
            return "登录已超时：授权码只能使用一次且只有几分钟有效期。\n"
                   + "请重新点击「在浏览器中登录」，登录完成后立刻点「从剪贴板登录」（或在输入框里粘贴回调链接）。";
        }

        return exception.ErrorCode?.ToLowerInvariant() switch
        {
            // The identity platform reports most client configuration problems as AADSTS codes,
            // which are far more actionable than the generic OAuth error identifier.
            "invalid_client" or "unauthorized_client" => DescribeClientProblem(exception),

            "invalid_request" =>
                "登录请求被微软拒绝（invalid_request）：通常是回调地址或权限范围不匹配。\n"
                + DescribeClientProblem(exception),

            "temporarily_unavailable" or "http_429" or "http_500" or "http_502" or "http_503" or "http_504" =>
                "微软登录服务暂时不可用，请稍等片刻后重试。",

            "authorization_declined" => "你取消了微软登录。需要登录时请重新点击「设备代码登录」。",

            "expired_token" => "设备代码已过期。请重新点击「设备代码登录」获取新的代码。",

            "not_entitled" => "该 Microsoft 账户未拥有 Minecraft: Java Edition，无法启动正版游戏。",

            "no_refresh_token" => "该账户没有可用的刷新令牌，需要重新登录一次。",

            "xbl_failed" or "xbl_invalid" => DescribeXboxLiveProblem(exception),

            "xsts_invalid" => DescribeXboxLiveProblem(exception),

            "profile_failed" or "profile_invalid" =>
                "已取得令牌，但读取 Minecraft 档案失败，请稍后重试。",

            _ => exception.Message,
        };
    }

    /// <summary>
    /// Explains an Xbox Live rejection.
    ///
    /// Xbox Live answers a rejected RPS ticket with <c>400</c> and usually an empty body, which is
    /// the single most common failure after a successful Microsoft sign-in. The realistic causes
    /// are all account-side, so the message lists them in the order they should be checked.
    /// </summary>
    private static string DescribeXboxLiveProblem(MicrosoftAuthException exception)
    {
        var detail = exception.ErrorDescription;

        // A 401 (rather than 400) means the ticket could not be validated at all. With the legacy
        // Live Connect flow that almost always means the token was issued without the Xbox Live
        // scope, which no amount of account fixing can solve - the v2.0 flow must be used.
        if (detail is not null && detail.Contains("401", StringComparison.Ordinal))
        {
            return "微软登录成功，但 Xbox Live 拒绝了这个令牌（HTTP 401）。\n"
                   + "这说明旧版 Live Connect 流程签发的令牌没有 Xbox Live 授权范围——这是微软对老流程的限制，"
                   + "与你的账号无关（同一账号在 HMCL / PCL 里能登录，是因为它们使用新的 v2.0 流程）。\n"
                   + "解决办法：\n"
                   + "① 到「设置 → 微软登录」填写一个你自己免费注册的 Azure 应用 ID（约 2 分钟，见「如何获取？」）；\n"
                   + "② 回到首页点「用浏览器登录」——仍然是同一个窗口、选择账户即完成，不需要复制任何内容；\n"
                   + "③ 或者直接用「设备代码登录」。\n"
                   + "填写 ID 后，登录窗口会改用 XboxLive.signin 权限，Xbox Live 就会接受。";
        }

        if (detail is not null && detail.Contains("400", StringComparison.Ordinal))
        {
            var hint = "微软登录成功，但 Xbox Live 拒绝了该账户的令牌（HTTP 400）。请按顺序排查：\n"
                       + "1) 该微软账户还没有 Xbox 档案 —— 打开 https://www.xbox.com 用同一账户登录一次后重试；\n"
                       + "2) 账户所在国家/地区不支持 Xbox Live；\n"
                       + "3) 未成年人账户需要先加入 Microsoft 家庭并由成人批准；\n"
                       + "4) 若刚登录成功不久，请稍等 1–2 分钟再重试（新档案生效有延迟）。";

            return WithDetail(hint, detail);
        }

        var lines = new List<string>
        {
            "微软账户已登录，但 Xbox Live 拒绝了该账户的令牌。请按顺序排查：",
            "1) 打开 https://www.xbox.com 用同一账户登录一次，确认能创建/进入 Xbox 档案；",
            "2) 检查账户国家/地区是否支持 Xbox Live；",
            "3) 未成年人账户需要先加入 Microsoft 家庭并由成人批准；",
            "4) 若仍失败，请改用「设备代码登录」或填写自己的 Azure 应用 ID 后重试。",
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add("微软返回的详细信息：" + detail);
        }

        return string.Join("\n", lines);
    }

    /// <summary>Appends the provider's raw detail when it adds information.</summary>
    private static string WithDetail(string message, string detail)
    {
        // "HTTP 400" alone is not interesting; a real body (XErr etc.) is.
        var body = detail.Contains('|') ? detail[(detail.IndexOf('|') + 1)..].Trim() : null;
        return body is { Length: > 0 } ? message + "\n微软返回的详细信息：" + body : message;
    }

    /// <summary>Turns Azure AD error codes (AADSTS…) into concrete configuration instructions.</summary>
    private static string DescribeClientProblem(MicrosoftAuthException exception)
    {
        var description = exception.ErrorDescription ?? string.Empty;

        if (description.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase))
        {
            return "微软找不到这个应用 ID（AADSTS700016）。\n"
                   + "请确认「设置 → 微软登录」里填的是你自己在 Azure 注册的应用（客户端）ID，"
                   + "并且该应用的「受支持的账户类型」选择「个人 Microsoft 账户」或「任何组织目录中的账户和个人 Microsoft 账户」。";
        }

        if (description.Contains("AADSTS7000218", StringComparison.OrdinalIgnoreCase))
        {
            return "该应用不允许公共客户端登录（AADSTS7000218）。\n"
                   + "请在 Azure 应用页面的「身份验证」中启用“允许公共客户端流”，然后重试。";
        }

        if (description.Contains("AADSTS50011", StringComparison.OrdinalIgnoreCase))
        {
            return "该应用没有登记本启动器使用的回调地址（AADSTS50011）。\n"
                   + "请在 Azure 应用 →「身份验证」→ 平台选择「公共客户端/本机(移动和桌面)」，"
                   + "添加重定向 URI：http://localhost  然后重试。";
        }

        if (description.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("consent", StringComparison.OrdinalIgnoreCase))
        {
            return "该应用尚未获得授权（需要 consent）。\n"
                   + "请在 Azure 应用页面为 Microsoft Graph / Xbox Live 授予用户同意，或在首次登录时接受授权页面。";
        }

        if (description.Contains("AADSTS90002", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("tenant", StringComparison.OrdinalIgnoreCase))
        {
            return "应用所在目录与登录入口不匹配（应使用 consumers 端点登录个人账户）。\n"
                   + "请确认应用注册在支持个人 Microsoft 账户的目录下。";
        }

        return "微软拒绝了该应用的登录请求：" + (string.IsNullOrWhiteSpace(description) ? exception.Message : description)
               + "\n请检查「设置 → 微软登录」里填写的客户端 ID 是否正确，以及该应用是否已启用“允许公共客户端流”。";
    }
}

/// <summary>
/// Microsoft account authentication:
/// Live Connect OAuth 2.0 authorization code → Xbox Live user token → XSTS token →
/// Minecraft services token → Minecraft profile.
///
/// Only documented, first-party endpoints are used. No token is ever written to a log.
/// </summary>
public sealed partial class MicrosoftAuthService
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
    /// True when <paramref name="url"/> is the configured redirect target, i.e. the navigation the
    /// embedded browser must intercept to capture the authorization code.
    ///
    /// Comparison ignores the scheme case and a trailing slash, because Microsoft returns
    /// <c>https://login.live.com/oauth20_desktop.srf?</c> without the slash used in the request.
    /// </summary>
    public static bool IsRedirectTo(string? url, string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(redirectUri)) return false;

        var trimmedUrl = url.Trim();
        var trimmedRedirect = redirectUri.Trim().TrimEnd('/');

        if (!trimmedUrl.StartsWith(trimmedRedirect, StringComparison.OrdinalIgnoreCase)) return false;

        // The next character must end the path (query/fragment) so ".../srf" cannot match
        // ".../srfevil".
        if (trimmedUrl.Length == trimmedRedirect.Length) return true;

        var next = trimmedUrl[trimmedRedirect.Length];
        return next is '?' or '#' or '/';
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

    /// <summary>
    /// Exchanges a legacy (Live Connect) authorization code for tokens.
    ///
    /// The code arrives URL-encoded inside the redirect URL and is normally decoded before the
    /// token request. Some issuers already percent-encode the code itself, and decoding then
    /// corrupts it — Microsoft answers with a generic "the 'code' parameter is not valid. The code
    /// has expired." Therefore the decoded form is tried first and the raw form second. Only one
    /// of them can ever be accepted, and a rejected attempt does not consume the valid code.
    /// </summary>
    public async Task<Account> AuthenticateWithCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new MicrosoftAuthException("授权码为空。");

        var candidates = new List<string> { code };

        var rawForm = Uri.EscapeDataString(code.Replace("%", "%25", StringComparison.Ordinal)
                                                   .Replace(" ", "%20", StringComparison.Ordinal));
        if (!string.Equals(rawForm, code, StringComparison.Ordinal) &&
            code.Contains('%', StringComparison.Ordinal))
        {
            // Re-encode the percent signs so the server receives the literal characters it issued.
            candidates.Add(code.Replace("%", "%25", StringComparison.Ordinal));
        }

        MicrosoftAuthException? last = null;

        foreach (var candidate in candidates)
        {
            try
            {
                var token = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["client_id"] = _options.ClientId,
                    ["code"] = candidate,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = _options.RedirectUri,
                    ["scope"] = _options.Scope,
                }, ct).ConfigureAwait(false);

                if (candidates.Count > 1)
                {
                    AppLog.Info("auth", "legacy-code-variant-accepted",
                        ("variant", ReferenceEquals(candidate, code) ? "decoded" : "raw"));
                }

                return await CompleteLoginAsync(
                    token.AccessToken!,
                    token.RefreshToken,
                    token.ExpiresIn,
                    FlowLegacy,
                    null,
                    ct).ConfigureAwait(false);
            }
            catch (MicrosoftAuthException ex) when (ex.IsAuthorizationCodeRejected)
            {
                last = ex;
                AppLog.Warn("auth", "legacy-code-variant-rejected",
                    ("variant", ReferenceEquals(candidate, code) ? "decoded" : "raw"),
                    ("detail", ex.ErrorDescription ?? "none"));
            }
        }

        throw last ?? new MicrosoftAuthException("微软拒绝了该授权码。", "invalid_grant");
    }

    /// <summary>Exchanges a stored refresh token for a fresh account (Microsoft token rotation).</summary>
    public async Task<Account> RefreshAsync(Account account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new MicrosoftAuthException("该账户没有可用的刷新令牌，需要重新登录。", "no_refresh_token");

        // Accounts created by the v2.0 flows must be refreshed against the same authority.
        if (account.AuthFlow is FlowDeviceCode or FlowLoopback)
            return await RefreshModernAsync(account, ct).ConfigureAwait(false);

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token",
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scope,
        }, ct).ConfigureAwait(false);

        return await CompleteLoginAsync(
            token.AccessToken!,
            token.RefreshToken,
            token.ExpiresIn,
            flow: account.AuthFlow ?? FlowLegacy,
            previous: account,
            ct).ConfigureAwait(false);
    }

    private async Task<Account> CompleteLoginAsync(
        string msaAccessToken,
        string? refreshToken,
        int expiresIn,
        string flow,
        Account? previous,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(msaAccessToken))
            throw new MicrosoftAuthException("微软返回的访问令牌为空。", "empty_access_token");

        // 1. Xbox Live user token
        var (xblToken, uhs) = await RequestXboxLiveTokenAsync(msaAccessToken, ct).ConfigureAwait(false);

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
            RefreshToken = string.IsNullOrEmpty(refreshToken) ? previous?.RefreshToken : refreshToken,
            XboxUserId = xsts.UserHash ?? uhs,
            AuthFlow = flow,
            ExpiresAt = expiresAt,
            SkinModel = profile?.Skins?.FirstOrDefault()?.Variant,
            LastUsedAt = DateTimeOffset.UtcNow,
        };

        AppLog.Info("auth", "microsoft-login-ok",
            ("name", account.Username),
            ("uuid", account.Uuid),
            ("flow", flow),
            ("expiresAt", account.ExpiresAt));

        return account;
    }

    private async Task<LiveTokenResponse> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(_options.TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = ReadErrorParts(body);
            var detail = string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}";

            throw new MicrosoftAuthException(
                $"微软登录失败: {detail ?? $"HTTP {(int)response.StatusCode}"}",
                error ?? $"http_{(int)response.StatusCode}",
                description,
                new HttpRequestException($"HTTP {(int)response.StatusCode}"));
        }

        var token = JsonSerializer.Deserialize<LiveTokenResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new MicrosoftAuthException("无法解析微软返回的令牌。", "parse_error");

        // The granted scope decides whether Xbox Live will accept the token. The legacy flow
        // silently downgrades the scope for some clients, which shows up later as a 401 from
        // user.auth.xboxlive.com - logging it makes that diagnosable.
        AppLog.Info("auth", "legacy-token-granted",
            ("scope", string.IsNullOrWhiteSpace(token.Scope) ? "<not reported>" : token.Scope),
            ("tokenType", token.TokenType ?? "?"));

        return token;
    }

    /// <summary>
    /// Exchanges an MSA access token for an Xbox Live user token.
    ///
    /// <c>RpsTicket</c> takes a single-letter source prefix: <c>d=</c> for a Microsoft account
    /// token, <c>t=</c> for an Azure AD token. Some issuers accept neither form (the ticket is
    /// simply not valid for Xbox Live), so the variants are tried in order — each rejected attempt
    /// costs one request and the first success wins.
    /// </summary>
    private async Task<(string Token, string UserHash)> RequestXboxLiveTokenAsync(string accessToken, CancellationToken ct)
    {
        string[] prefixes = ["d=", "t=", ""];
        MicrosoftAuthException? last = null;

        foreach (var prefix in prefixes)
        {
            try
            {
                var result = await TryRequestXboxLiveTokenAsync(accessToken, prefix, ct).ConfigureAwait(false);

                if (prefix != "d=")
                {
                    AppLog.Warn("auth", "xbl-ticket-prefix-fallback",
                        ("prefix", prefix.Length == 0 ? "<none>" : prefix));
                }

                return result;
            }
            catch (MicrosoftAuthException ex) when (ex.ErrorCode == "xbl_failed")
            {
                last = ex;
            }
        }

        throw last ?? new MicrosoftAuthException("Xbox Live 认证失败。", "xbl_failed");
    }

    private async Task<(string Token, string UserHash)> TryRequestXboxLiveTokenAsync(
        string accessToken,
        string prefix,
        CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = prefix + accessToken,
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
        {
            // Xbox Live answers a rejected ticket with 400/401 and (usually) an empty body, so the
            // status plus whatever detail exists is all the diagnosis we can get. Log it: without
            // this the user only sees "Xbox Live 认证失败" with no way to report the cause.
            var detail = SummarizeBody(body);
            AppLog.Warn("auth", "xbl-rejected",
                ("status", (int)response.StatusCode),
                ("prefix", prefix.Length == 0 ? "<none>" : prefix),
                ("detail", detail ?? "empty"));

            var described = detail is null
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode} | {detail}";

            throw new MicrosoftAuthException(
                $"Xbox Live 认证失败 (HTTP {(int)response.StatusCode})",
                "xbl_failed",
                described);
        }

        var parsed = JsonSerializer.Deserialize<XboxTokenResponse>(body);
        var token = parsed?.Token;
        var uhs = parsed?.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash;

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(uhs))
            throw new MicrosoftAuthException("Xbox Live 返回的令牌无效。", "xbl_invalid", SummarizeBody(body));

        return (token, uhs);
    }

    /// <summary>Short, log-safe summary of a response body (never contains credentials).</summary>
    private static string? SummarizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var trimmed = body.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
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
        var (error, description) = ReadErrorParts(body);
        if (error is null) return null;

        return string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}";
    }

    /// <summary>Splits an OAuth error body into its identifier and human readable description.</summary>
    private static (string? Error, string? Description) ReadErrorParts(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error)) return (null, null);

            var description = document.RootElement.TryGetProperty("error_description", out var d)
                ? d.GetString()?.Replace('+', ' ')
                : null;

            return (error.ToString(), description);
        }
        catch (JsonException)
        {
            return (null, null);
        }
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

        /// <summary>Scopes the provider actually granted — logged to diagnose missing Xbox access.</summary>
        [JsonPropertyName("scope")] public string? Scope { get; set; }
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
