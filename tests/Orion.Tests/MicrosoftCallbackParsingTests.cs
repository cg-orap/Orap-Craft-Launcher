using Orion.Core.Auth;

namespace Orion.Tests;

/// <summary>
/// The Microsoft login flow finishes in a browser, so whatever the user copies back must be
/// accepted. These tests cover the messy real-world shapes of that paste.
/// </summary>
public sealed class MicrosoftCallbackParsingTests
{
    private const string Code = "M.C50A_BAY.2.U.9f8a7b6c-1234-4d5e-8f90-abcdefabcdef";

    [Fact]
    public void ExtractsCodeFromAFullLiveConnectRedirectUrl()
    {
        var url = $"https://login.live.com/oauth20_desktop.srf?code={Code}&lc=2052&state=orion&session_state=abc";

        Assert.Equal(Code, MicrosoftAuthService.TryExtractCode(url));
    }

    [Fact]
    public void ExtractsCodeWhenTheUrlIsPercentEncoded()
    {
        var url = "https://login.live.com/oauth20_desktop.srf?code=M.C50A_BAY%2B2.U.a%3Db&state=x";
        var code = MicrosoftAuthService.TryExtractCode(url);

        Assert.NotNull(code);
        Assert.Contains("M.C50A_BAY+2.U.a=b", code!, StringComparison.Ordinal);
    }

    [Theory]
    // no scheme (people copy from odd viewers)
    [InlineData("login.live.com/oauth20_desktop.srf?code=M.C50A_BAY.2.U.abc1234567")]
    // wrapped in quotes / brackets / whitespace by chat apps
    [InlineData("\"https://login.live.com/oauth20_desktop.srf?code=M.C50A_BAY.2.U.abc1234567\"")]
    [InlineData("<https://login.live.com/oauth20_desktop.srf?code=M.C50A_BAY.2.U.abc1234567>")]
    [InlineData("  https://login.live.com/oauth20_desktop.srf?code=M.C50A_BAY.2.U.abc1234567  ")]
    // address bars wrap long URLs; the copy can contain line breaks
    [InlineData("https://login.live.com/oauth20_desktop.srf?code=M.C50A_BAY.2.U.abc1234567\r\n")]
    public void AcceptsCodeFromMessyPasteShapes(string input)
    {
        Assert.Equal("M.C50A_BAY.2.U.abc1234567", MicrosoftAuthService.TryExtractCode(input));
    }

    [Fact]
    public void AcceptsBareCodeFragmentWithoutHost()
    {
        Assert.Equal(Code, MicrosoftAuthService.TryExtractCode($"code={Code}"));
        Assert.Equal(Code, MicrosoftAuthService.TryExtractCode($"?code={Code}&lc=1"));
    }

    [Fact]
    public void AcceptsTheRawCodeShownByTheDesktopRedirectPage()
    {
        Assert.Equal(Code, MicrosoftAuthService.TryExtractCode(Code));
        Assert.Equal(Code, MicrosoftAuthService.TryExtractCode($"  {Code}\n"));
    }

    [Fact]
    public void ReportsCancelledLoginInsteadOfReturningACode()
    {
        var url = "https://login.live.com/oauth20_desktop.srf?error=access_denied&error_description=The+user+has+denied+access";

        Assert.Null(MicrosoftAuthService.TryExtractCode(url));

        var error = MicrosoftAuthService.TryGetAuthorizationError(url);
        Assert.NotNull(error);
        Assert.Contains("access_denied", error!, StringComparison.Ordinal);
        Assert.Contains("denied access", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsServerErrorsFromTheCallback()
    {
        var url = "https://login.live.com/oauth20_desktop.srf?error=invalid_request&error_description=Missing+scope";

        Assert.Null(MicrosoftAuthService.TryExtractCode(url));
        Assert.Contains("invalid_request", MicrosoftAuthService.TryGetAuthorizationError(url)!, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotMistakeAParameterValueForTheCodeParameter()
    {
        // "redirect_uri" contains "code" but not as a parameter name; "notcode" must not match either.
        var url = "https://login.live.com/oauth20_desktop.srf?redirect_uri=https%3A%2F%2Fx%2F&notcode=zzz";

        Assert.Null(MicrosoftAuthService.TryExtractCode(url));
    }

    [Fact]
    public void PrefersTheQueryCodeOverSimilarLookingText()
    {
        var url = "https://login.live.com/oauth20_desktop.srf?state=code=looks-like-a-code&code=M.C50A_BAY.2.U.real";

        Assert.Equal("M.C50A_BAY.2.U.real", MicrosoftAuthService.TryExtractCode(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("https://example.com/with space/inside")]
    [InlineData("short")]
    [InlineData("https://login.live.com/oauth20_desktop.srf")]
    public void RejectsUnusableInput(string input)
    {
        Assert.Null(MicrosoftAuthService.TryExtractCode(input));
        Assert.Null(MicrosoftAuthService.TryGetAuthorizationError(input));
    }

    [Fact]
    public void AuthorizationUrlIsStableAndCarriesTheRequiredParameters()
    {
        var service = new MicrosoftAuthService(new HttpClient());
        var uri = service.BuildAuthorizationUri("orion");
        var text = uri.ToString();

        Assert.StartsWith("https://login.live.com/oauth20_authorize.srf?", text, StringComparison.Ordinal);
        Assert.Contains("response_type=code", text, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Flogin.live.com%2Foauth20_desktop.srf", text, StringComparison.Ordinal);
        Assert.Contains("state=orion", text, StringComparison.Ordinal);
    }
}
