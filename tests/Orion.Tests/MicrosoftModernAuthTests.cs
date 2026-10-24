using System.Net;
using System.Text;
using Orion.Core.Auth;

namespace Orion.Tests;

/// <summary>
/// Tests for the v2.0 sign-in path that the embedded login window uses.
///
/// The launcher deliberately keeps a single interactive method (the in-window browser); these tests
/// cover the pieces the window depends on: the authorize URI it navigates to, the redirect query it
/// parses, and the code exchange / refresh that run against the v2.0 token endpoint.
/// </summary>
public sealed class MicrosoftModernAuthTests
{
    [Fact]
    public void ModernAuthorizeUriUsesTheV2EndpointAndLoopbackRedirect()
    {
        var options = new MicrosoftAuthOptions
        {
            ClientId = "modern-client",
            Authority = "https://login.microsoftonline.com/consumers",
        };
        var service = new MicrosoftAuthService(new HttpClient(), options);

        var uri = service.BuildModernAuthorizeUri("http://localhost:12345/", "state-1");
        // AbsoluteUri keeps the percent-encoding the browser must receive.
        var text = uri.AbsoluteUri;

        Assert.StartsWith("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?", text, StringComparison.Ordinal);
        Assert.Contains("client_id=modern-client", text, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A12345%2F", text, StringComparison.Ordinal);
        Assert.Contains("scope=XboxLive.signin%20offline_access", text, StringComparison.Ordinal);
        Assert.Contains("state=state-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectQueryIsParsedWithoutLosingEncoding()
    {
        var parameters = OAuthQuery.Parse("/?code=M.C50A%2B_BAY.2.U.x%3D&state=s%201&scope=a%20b");

        Assert.Equal("M.C50A+_BAY.2.U.x=", parameters["code"]);
        Assert.Equal("s 1", parameters["state"]);
        Assert.Equal("a b", parameters["scope"]);
    }

    [Fact]
    public void RedirectQueryIgnoresEmptyAndMalformedPairs()
    {
        var parameters = OAuthQuery.Parse("/?&&code=abc&novalue&=&state=");

        Assert.Equal("abc", parameters["code"]);
        Assert.Equal("", parameters["state"]);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void LoopbackPortIsAPlausibleEphemeralPort() =>
        Assert.InRange(MicrosoftAuthService.PickLoopbackPort(), 1, 65535);

    [Fact]
    public async Task V2AuthorizationCodeIsExchangedAndCompletesTheChain()
    {
        var stub = new ModernAuthStub();
        stub.PathOverrides["/oauth2/v2.0/token"] = (HttpStatusCode.OK,
            """{"access_token":"msa-access","refresh_token":"msa-refresh","expires_in":3600,"scope":"XboxLive.signin"}""");

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());
        var account = await service.SignInWithModernCodeAsync("the-code", "http://localhost:5555/");

        Assert.Equal("Notch", account.Username);
        Assert.Equal(MicrosoftAuthService.FlowLoopback, account.AuthFlow);
        Assert.Equal("msa-refresh", account.RefreshToken);

        var request = Assert.Single(stub.Requests, r => r.Path == "/oauth2/v2.0/token");
        Assert.Contains("code=the-code", request.Body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5555%2F", request.Body, StringComparison.Ordinal);
        // FormUrlEncodedContent encodes the space as '+'.
        Assert.Contains("scope=XboxLive.signin", request.Body, StringComparison.Ordinal);
        Assert.Contains("offline_access", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2ExchangeRequiresAConfiguredClientId()
    {
        // The built-in id belongs to the legacy flow, so the v2.0 call must explain that instead of
        // sending a request that Microsoft would reject with invalid_client.
        var service = new MicrosoftAuthService(new HttpClient(), new MicrosoftAuthOptions());

        var exception = await Assert.ThrowsAsync<MicrosoftAuthException>(
            () => service.SignInWithModernCodeAsync("the-code", "http://localhost:5555/"));

        Assert.Equal("missing_client_id", exception.ErrorCode);
        Assert.Contains("Azure", exception.Message, StringComparison.Ordinal);
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
            AuthFlow = MicrosoftAuthService.FlowLoopback,
            RefreshToken = "old-refresh",
        };

        var refreshed = await service.RefreshAsync(account);

        Assert.Equal("msa-refresh-2", refreshed.RefreshToken);
        Assert.Contains(stub.Requests, r => r.Path == "/oauth2/v2.0/token" && r.Body.Contains("scope=XboxLive.signin", StringComparison.Ordinal));
        // The legacy Live Connect token endpoint must not be used for v2.0 accounts.
        Assert.DoesNotContain(stub.Requests, r => r.Path == "/token");
    }

    [Fact]
    public async Task SkinUrlFromTheProfileIsStoredOnTheAccount()
    {
        var stub = new ModernAuthStub();
        stub.PathOverrides["/oauth2/v2.0/token"] = (HttpStatusCode.OK,
            """{"access_token":"msa-access","refresh_token":"r","expires_in":3600}""");
        stub.PathOverrides["/profile"] = (HttpStatusCode.OK, """
            {"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch",
             "skins":[{"id":"a","state":"ACTIVE","variant":"SLIM","url":"http://textures.minecraft.net/texture/abc"}]}
            """);

        var service = new MicrosoftAuthService(new HttpClient(stub), stub.Options());
        var account = await service.SignInWithModernCodeAsync("the-code", "http://localhost:5555/");

        Assert.Equal("SLIM", account.SkinModel);
        Assert.Equal("http://textures.minecraft.net/texture/abc", account.SkinUrl);
    }

    // ---------------------------------------------------------------- stub transport

    private sealed class ModernAuthStub : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> PathOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, string Body)> Requests { get; } = [];

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
                case "/oauth2/v2.0/token":
                    return Build(HttpStatusCode.OK, """{"access_token":"msa-access","refresh_token":"msa-refresh","expires_in":3600}""");

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
