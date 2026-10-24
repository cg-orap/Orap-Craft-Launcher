using System.Net;
using System.Text;
using Orion.Core.Auth;

namespace Orion.Tests;

/// <summary>
/// Exercises the whole Microsoft authentication chain against a stubbed HTTP endpoint.
///
/// No real account is involved: this verifies protocol handling (request shapes, token
/// propagation, error mapping), not the live service. The live sign-in remains an explicitly
/// unverified item in docs/TEST-REPORT.md.
/// </summary>
public sealed class MicrosoftAuthFlowTests
{
    /// <summary>Routes requests by path and records the bodies, so request shapes can be asserted.</summary>
    private sealed class AuthStub : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Authorization)> Requests { get; } = [];

        /// <summary>When set, every request fails with this response (used for token errors).</summary>
        public (HttpStatusCode Status, string Body)? GlobalOverride { get; set; }

        /// <summary>Per path failures that leave the rest of the happy path intact.</summary>
        public Dictionary<string, (HttpStatusCode Status, string Body)> PathOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path, body, request.Headers.Authorization?.Parameter));

            if (PathOverrides.TryGetValue(path, out var pathOverride))
                return Build(pathOverride.Status, pathOverride.Body);

            if (GlobalOverride is { } global)
                return Build(global.Status, global.Body);

            var (status, json) = path switch
            {
                "/token" => (HttpStatusCode.OK,
                    """{"access_token":"msa-access","refresh_token":"msa-refresh","expires_in":3600,"token_type":"bearer"}"""),

                "/xbl" => (HttpStatusCode.OK,
                    """{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"uhs-1234"}]}}"""),

                "/xsts" => (HttpStatusCode.OK,
                    """{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"uhs-1234"}]}}"""),

                "/mc" => (HttpStatusCode.OK,
                    """{"access_token":"minecraft-access","expires_in":86400,"username":"ignored"}"""),

                "/profile" => (HttpStatusCode.OK,
                    """{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch","skins":[{"id":"s1","state":"ACTIVE","variant":"CLASSIC"}]}"""),

                _ => (HttpStatusCode.NotFound, """{"error":"not_found"}"""),
            };

            return Build(status, json);
        }

        private static HttpResponseMessage Build(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static MicrosoftAuthOptions StubOptions() => new()
    {
        ClientId = "test-client",
        RedirectUri = "https://login.live.com/oauth20_desktop.srf",
        TokenEndpoint = "https://stub.test/token",
        XboxLiveEndpoint = "https://stub.test/xbl",
        XstsEndpoint = "https://stub.test/xsts",
        MinecraftEndpoint = "https://stub.test/mc",
        ProfileEndpoint = "https://stub.test/profile",
    };

    [Fact]
    public async Task FullChainProducesAMicrosoftAccount()
    {
        var stub = new AuthStub();
        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());

        var account = await service.AuthenticateWithCodeAsync("the-code");

        Assert.Equal(AccountType.Microsoft, account.Type);
        Assert.Equal("Notch", account.Username);
        Assert.Equal("069a79f444e94726a5befca90e38aaf5", account.Uuid);
        Assert.Equal("minecraft-access", account.AccessToken);
        Assert.Equal("msa-refresh", account.RefreshToken);
        Assert.Equal("uhs-1234", account.XboxUserId);
        Assert.Equal("CLASSIC", account.SkinModel);
        Assert.False(account.IsExpired);
        Assert.Equal("msa", account.UserType);

        // The Microsoft access token is sent as "d=<token>" to Xbox Live.
        var xbl = stub.Requests.Single(r => r.Path == "/xbl");
        Assert.Contains("d=msa-access", xbl.Body, StringComparison.Ordinal);

        // The Minecraft step consumes the "XBL3.0 x=<uhs>;<xsts>" identity token.
        var mc = stub.Requests.Single(r => r.Path == "/mc");
        Assert.Contains("XBL3.0 x=uhs-1234;xsts-token", mc.Body, StringComparison.Ordinal);

        // The profile request carries the Minecraft bearer token.
        var profile = stub.Requests.Single(r => r.Path == "/profile");
        Assert.Equal("minecraft-access", profile.Authorization);

        // The authorization code is posted with the configured client and redirect.
        var token = stub.Requests.Single(r => r.Path == "/token");
        Assert.Contains("grant_type=authorization_code", token.Body, StringComparison.Ordinal);
        Assert.Contains("code=the-code", token.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=test-client", token.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredAuthorizationCodeSurfacesAsCodeRejection()
    {
        var stub = new AuthStub
        {
            GlobalOverride = (HttpStatusCode.BadRequest,
                """{"error":"invalid_grant","error_description":"The provided value for the 'code' parameter is not valid. The code has expired."}"""),
        };

        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.AuthenticateWithCodeAsync("expired-code"));

        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Contains("expired", exception.ErrorDescription!, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.IsAuthorizationCodeRejected, "an expired code must never be retried");

        var message = MicrosoftAuthMessages.Describe(exception);
        Assert.Contains("重新点击", message, StringComparison.Ordinal);
        Assert.Contains("授权码", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedClientIdIsExplained()
    {
        var stub = new AuthStub
        {
            GlobalOverride = (HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"bad app"}"""),
        };

        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());
        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.AuthenticateWithCodeAsync("code"));

        Assert.False(exception.IsAuthorizationCodeRejected);
        Assert.Contains("客户端 ID", MicrosoftAuthMessages.Describe(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountWithoutMinecraftOwnershipIsReported()
    {
        var stub = new AuthStub();
        stub.PathOverrides["/profile"] = (HttpStatusCode.NotFound, """{"error":"NOT_FOUND"}""");

        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());
        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.AuthenticateWithCodeAsync("code"));

        Assert.Equal("not_entitled", exception.ErrorCode);
        Assert.Contains("未拥有 Minecraft", MicrosoftAuthMessages.Describe(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshUsesTheRefreshTokenAndKeepsTheProfile()
    {
        var stub = new AuthStub();
        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());

        var existing = new Account
        {
            Id = "069a79f444e94726a5befca90e38aaf5",
            Username = "Notch",
            Uuid = "069a79f444e94726a5befca90e38aaf5",
            Type = AccountType.Microsoft,
            RefreshToken = "old-refresh",
            AccessToken = "old-access",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        var refreshed = await service.RefreshAsync(existing);

        Assert.Equal("minecraft-access", refreshed.AccessToken);
        Assert.Equal("Notch", refreshed.Username);
        Assert.False(refreshed.IsExpired);

        var tokenRequest = stub.Requests.Single(r => r.Path == "/token");
        Assert.Contains("grant_type=refresh_token", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=old-refresh", tokenRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshWithoutTokenAsksForANewLogin()
    {
        var service = new MicrosoftAuthService(new HttpClient(new AuthStub()), StubOptions());
        var account = new Account
        {
            Id = "x",
            Username = "x",
            Uuid = "x",
            Type = AccountType.Microsoft,
        };

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.RefreshAsync(account));
        Assert.Equal("no_refresh_token", exception.ErrorCode);
        Assert.Contains("重新登录", MicrosoftAuthMessages.Describe(exception), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("temporarily_unavailable", "稍等")]
    [InlineData("xbl_failed", "Xbox")]
    [InlineData("not_entitled", "未拥有")]
    [InlineData("unknown_thing", "原始消息")]
    public void ErrorMessagesAreActionable(string errorCode, string expectedFragment)
    {
        var exception = new MicrosoftAuthException("原始消息", errorCode);
        var message = MicrosoftAuthMessages.Describe(exception);

        Assert.Contains(expectedFragment, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidGrantWithoutCodeWordingIsNotTreatedAsCodeRejection()
    {
        // invalid_grant is also used for a revoked refresh token; only the code wording means
        // "the pasted authorization code can never be reused".
        var exception = new MicrosoftAuthException("x", "invalid_grant", "The refresh token has been revoked.");
        Assert.False(exception.IsAuthorizationCodeRejected);

        var codeException = new MicrosoftAuthException("x", "invalid_grant", "The code has expired.");
        Assert.True(codeException.IsAuthorizationCodeRejected);
    }

    [Fact]
    public async Task XboxLiveRejectionKeepsTheProviderDetailAndExplainsTheCauses()
    {
        // Xbox Live answers a rejected ticket with 400 and usually an empty body; whatever detail
        // exists must reach the exception (and the log) instead of being dropped.
        var stub = new AuthStub();
        stub.PathOverrides["/xbl"] = (HttpStatusCode.BadRequest, """{"Identity":"0","XErr":2148916233}""");

        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());
        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.AuthenticateWithCodeAsync("code"));

        Assert.Equal("xbl_failed", exception.ErrorCode);
        Assert.Contains("2148916233", exception.ErrorDescription!, StringComparison.Ordinal);

        var message = MicrosoftAuthMessages.Describe(exception);
        Assert.Contains("xbox.com", message, StringComparison.Ordinal);
        Assert.Contains("Xbox 档案", message, StringComparison.Ordinal);
        Assert.Contains("2148916233", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XboxLiveRejectionWithEmptyBodyStillExplainsTheCauses()
    {
        var stub = new AuthStub();
        stub.PathOverrides["/xbl"] = (HttpStatusCode.BadRequest, "");

        var service = new MicrosoftAuthService(new HttpClient(stub), StubOptions());
        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(() => service.AuthenticateWithCodeAsync("code"));

        Assert.Equal("xbl_failed", exception.ErrorCode);
        Assert.Null(exception.ErrorDescription);

        var message = MicrosoftAuthMessages.Describe(exception);
        Assert.Contains("Xbox 档案", message, StringComparison.Ordinal);
    }
}
