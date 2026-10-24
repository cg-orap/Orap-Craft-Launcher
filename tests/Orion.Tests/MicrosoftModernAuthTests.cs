using System.Net;
using System.Text;
using System.Text.Json;
using Orion.Core.Auth;

namespace Orion.Tests;

/// <summary>
/// Tests for the modern Microsoft sign-in mechanisms (device code + loopback), which are the ones
/// mainstream launchers use because they never require the user to copy a URL back.
/// </summary>
public sealed class MicrosoftModernAuthTests
{
    // ---------------------------------------------------------------- loopback listener

    [Fact]
    public async Task LoopbackListenerReceivesTheOAuthCallback()
    {
        await using var listener = LoopbackAuthListener.Start();
        Assert.Contains("http://localhost:", listener.RedirectUri, StringComparison.Ordinal);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var callbackUrl = $"{listener.RedirectUri}?code=the-code&state=abc123&lc=2052";

        var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10));
        using var response = await http.GetAsync(callbackUrl);
        var parameters = await wait;

        Assert.Equal("the-code", parameters["code"]);
        Assert.Equal("abc123", parameters["state"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("登录成功", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopbackListenerReportsOAuthErrorsToTheBrowser()
    {
        await using var listener = LoopbackAuthListener.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var wait = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10));
        using var response = await http.GetAsync($"{listener.RedirectUri}?error=access_denied&error_description=cancelled+by+user");
        var parameters = await wait;

        Assert.Equal("access_denied", parameters["error"]);
        Assert.Equal("cancelled by user", parameters["error_description"]);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("登录未完成", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopbackListenerTimesOutWithoutACallback()
    {
        await using var listener = LoopbackAuthListener.Start();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            listener.WaitForCallbackAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void LoopbackQueryParsingHandlesEncoding()
    {
        var parameters = LoopbackAuthListener.ParseQuery("/?code=M.C50A%2B_BAY.2.U.x%3D&state=s%201&scope=a%20b");

        Assert.Equal("M.C50A+_BAY.2.U.x=", parameters["code"]);
        Assert.Equal("s 1", parameters["state"]);
        Assert.Equal("a b", parameters["scope"]);
    }

    [Fact]
    public void ModernAuthorizeUriUsesTheV2EndpointAndLoopbackRedirect()
    {
        var options = new MicrosoftAuthOptions { ClientId = "modern-client", Authority = "https://login.microsoftonline.com/consumers" };
        var service = new MicrosoftAuthService(new HttpClient(), options);

        var uri = service.BuildModernAuthorizeUri("http://localhost:12345/", "state-1");
        // AbsoluteUri keeps the percent-encoding that the browser must receive; ToString() would
        // unescape some characters for display.
        var text = uri.AbsoluteUri;

        Assert.StartsWith("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?", text, StringComparison.Ordinal);
        Assert.Contains("client_id=modern-client", text, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A12345%2F", text, StringComparison.Ordinal);
        Assert.Contains("scope=XboxLive.signin%20offline_access", text, StringComparison.Ordinal);
        Assert.Contains("state=state-1", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- device code

    [Fact]
    public async Task DeviceCodeIsRefusedWithoutAUserRegisteredClientId()
    {
        // The built-in id belongs to the legacy Live Connect flow.
        var service = new MicrosoftAuthService(new HttpClient(), new MicrosoftAuthOptions());

        Assert.False(service.CanUseModernFlows);
        Assert.NotNull(service.ModernFlowsUnavailableReason);

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.StartDeviceCodeAsync());
        Assert.Equal("missing_client_id", exception.ErrorCode);
        Assert.Contains("Azure", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceCodeFlowPollsUntilAuthorisedThenBuildsTheAccount()
    {
        var stub = new ModernAuthStub
        {
            DeviceCodeResponse = """
                {"device_code":"dev-code","user_code":"ABCD-1234","verification_uri":"https://microsoft.com/link",
                 "expires_in":900,"interval":1,"message":"use the code"}
                """,
            TokenResponses =
            {
                (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
                (HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
                (HttpStatusCode.OK, """{"access_token":"msa-access","refresh_token":"msa-refresh","expires_in":3600}"""),
            },
        };

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());

        var session = await service.StartDeviceCodeAsync();
        Assert.Equal("ABCD-1234", session.UserCode);
        Assert.Equal("https://microsoft.com/link", session.VerificationUri);

        var account = await service.WaitForDeviceCodeAsync(session);

        Assert.Equal("Notch", account.Username);
        Assert.Equal("msa-refresh", account.RefreshToken);
        Assert.Equal(MicrosoftAuthService.FlowDeviceCode, account.AuthFlow);
        Assert.Equal(3, stub.TokenPolls);
    }

    [Fact]
    public async Task DeclinedDeviceCodeLoginIsExplained()
    {
        var stub = new ModernAuthStub
        {
            DeviceCodeResponse = """{"device_code":"dev-code","user_code":"AB-CD","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":1}""",
            TokenResponses = { (HttpStatusCode.BadRequest, """{"error":"authorization_declined","error_description":"user said no"}""") },
        };

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());
        var session = await service.StartDeviceCodeAsync();

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.WaitForDeviceCodeAsync(session));
        Assert.Equal("authorization_declined", exception.ErrorCode);
        Assert.Contains("取消", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredDeviceCodeIsExplained()
    {
        var stub = new ModernAuthStub
        {
            DeviceCodeResponse = """{"device_code":"dev-code","user_code":"AB-CD","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":1}""",
            TokenResponses = { (HttpStatusCode.BadRequest, """{"error":"expired_token"}""") },
        };

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());
        var session = await service.StartDeviceCodeAsync();

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.WaitForDeviceCodeAsync(session));
        Assert.Equal("expired_token", exception.ErrorCode);
        Assert.Contains("重新", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModernRefreshUsesTheV2TokenEndpoint()
    {
        var stub = new ModernAuthStub();
        stub.PathOverrides["/oauth2/v2.0/token"] = (HttpStatusCode.OK,
            """{"access_token":"msa-access-2","refresh_token":"msa-refresh-2","expires_in":3600}""");

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());

        var account = new Account
        {
            Id = "u",
            Username = "Notch",
            Uuid = "u",
            Type = AccountType.Microsoft,
            AuthFlow = MicrosoftAuthService.FlowDeviceCode,
            RefreshToken = "old-refresh",
        };

        var refreshed = await service.RefreshAsync(account);

        Assert.Equal("msa-refresh-2", refreshed.RefreshToken);
        Assert.Contains(stub.Requests, r => r.Path == "/oauth2/v2.0/token" && r.Body.Contains("scope=XboxLive.signin", StringComparison.Ordinal));
        // The legacy Live Connect token endpoint must not be used for v2.0 accounts.
        Assert.DoesNotContain(stub.Requests, r => r.Path == "/token");
    }

    // ---------------------------------------------------------------- stub transport

    private sealed class ModernAuthStub : HttpMessageHandler
    {
        public string DeviceCodeResponse { get; set; } =
            """{"device_code":"dev-code","user_code":"ABCD-1234","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":1}""";

        public List<(HttpStatusCode Status, string Body)> TokenResponses { get; } = [];

        public Dictionary<string, (HttpStatusCode Status, string Body)> PathOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, string Body)> Requests { get; } = [];

        public int TokenPolls { get; private set; }

        public MicrosoftAuthOptions Options() => new()
        {
            ClientId = "modern-client",
            Authority = "https://stub.test",
            TokenEndpoint = "https://stub.test/token",
            XboxLiveEndpoint = "https://stub.test/xbl",
            XstsEndpoint = "https://stub.test/xsts",
            MinecraftEndpoint = "https://stub.test/mc",
            ProfileEndpoint = "https://stub.test/profile",
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path, body));

            if (PathOverrides.TryGetValue(path, out var pathOverride))
                return Build(pathOverride.Status, pathOverride.Body);

            switch (path)
            {
                case "/oauth2/v2.0/devicecode":
                    return Build(HttpStatusCode.OK, DeviceCodeResponse);

                case "/oauth2/v2.0/token":
                {
                    var index = Math.Min(TokenPolls, Math.Max(0, TokenResponses.Count - 1));
                    TokenPolls++;
                    var response = TokenResponses.Count == 0
                        ? (HttpStatusCode.OK, """{"access_token":"msa-access","refresh_token":"msa-refresh","expires_in":3600}""")
                        : TokenResponses[index];
                    return Build(response.Item1, response.Item2);
                }

                case "/xbl":
                    return Build(HttpStatusCode.OK, """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"uhs-1234"}]}}""");

                case "/xsts":
                    return Build(HttpStatusCode.OK, """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"uhs-1234"}]}}""");

                case "/mc":
                    return Build(HttpStatusCode.OK, """{"access_token":"minecraft-access","expires_in":86400}""");

                case "/profile":
                    return Build(HttpStatusCode.OK, """{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch"}""");

                default:
                    return Build(HttpStatusCode.NotFound, """{"error":"not_found"}""");
            }
        }

        private static HttpResponseMessage Build(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
