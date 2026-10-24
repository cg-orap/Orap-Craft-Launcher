using Orion.Core.Auth;
using Orion.Core.Diagnostics;

namespace Orion.App.Services;

/// <summary>
/// One in-window Microsoft sign-in attempt.
///
/// Holds the authorize URI for the embedded browser plus the redirect the window must intercept,
/// and exchanges the captured code for tokens. When the user has configured their own public-client
/// application id the v2.0 authorize endpoint (<c>XboxLive.signin</c>) is used, because that is the
/// token Xbox Live accepts; otherwise the legacy Live Connect flow with the built-in client id.
/// </summary>
public sealed class MicrosoftBrowserLogin
{
    private readonly MicrosoftAuthService _auth;

    public MicrosoftBrowserLogin(MicrosoftAuthService auth)
    {
        _auth = auth;
        State = Guid.NewGuid().ToString("N");

        if (auth.CanUseModernFlows)
        {
            RedirectUri = $"http://localhost:{MicrosoftAuthService.PickLoopbackPort()}/";
            AuthorizeUri = auth.BuildModernAuthorizeUri(RedirectUri, State);
            IsModern = true;
        }
        else
        {
            RedirectUri = auth.Options.RedirectUri;
            AuthorizeUri = auth.BuildAuthorizationUri();
            IsModern = false;
        }

        AppLog.Info("auth", "browser-login-start", ("flow", IsModern ? "v2.0" : "legacy"), ("host", AuthorizeUri.Host));
    }

    public bool IsModern { get; }

    public string RedirectUri { get; }

    public string State { get; }

    public Uri AuthorizeUri { get; }

    /// <summary>True when the intercepted navigation belongs to this attempt.</summary>
    public bool IsRedirect(string url) => MicrosoftAuthService.IsRedirectTo(url, RedirectUri);

    /// <summary>Exchanges the captured authorization code and returns the finished account.</summary>
    public Task<Account> ExchangeAsync(string code, CancellationToken ct = default)
        => IsModern
            ? _auth.SignInWithModernCodeAsync(code, RedirectUri, ct)
            : _auth.AuthenticateWithCodeAsync(code, ct);
}
