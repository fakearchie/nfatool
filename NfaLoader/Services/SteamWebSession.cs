using System.Security.Cryptography;

namespace NfaLoader.Services;

/// <summary>
/// Authenticated web session credentials for steamcommunity.com: on a "signed-in CM session", trade the EYA refresh token for an
/// access token and build the steamLoginSecure / sessionid cookies from it.
/// Note: the exchange must go through a signed-in CM session. GenerateAccessTokenForApp / finalizelogin on the anonymous web always return
/// AccessDenied(15); these tokens are only issued to an authenticated session.
/// Endpoints such as FileUploader and the profile form require the sessionid in the request body to match the one in the cookie, so SessionId is exposed too.
/// </summary>
internal sealed record SteamWebSession(string CookieHeader, string SessionId, string AccessToken)
{
    public static async Task<SteamWebSession> BuildAsync(
        SteamCmClient cmClient,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        var accessToken = await cmClient.GenerateAccessTokenForAppAsync(refreshToken, cancellationToken);
        var steamLoginSecure = Uri.EscapeDataString($"{steamId}||{accessToken}");
        var sessionId = RandomHex(12);
        var clientSessionId = RandomHex(8);

        var cookieHeader =
            $"steamLoginSecure={steamLoginSecure}; sessionid={sessionId}; clientsessionid={clientSessionId}";

        return new SteamWebSession(cookieHeader, sessionId, accessToken);
    }

    private static string RandomHex(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }
}
