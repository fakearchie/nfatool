using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamTokenOnlineValidationService
{
    private readonly JwtTokenService _jwtTokenService = new();

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Asks Steam whether it accepts the token. IsValid is false when the token or the account is dead. A rate limit
    /// or another answer that says nothing about the token is thrown as <see cref="SteamCmException"/>. Anything else
    /// thrown, apart from the caller's own cancel, means Steam could not be reached.
    /// </summary>
    public async Task<SteamTokenOnlineValidationResult> ValidateAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var tokenInfo = _jwtTokenService.Inspect(refreshToken);
        if (!tokenInfo.IsValid)
        {
            return new SteamTokenOnlineValidationResult(false, tokenInfo.Status);
        }

        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return new SteamTokenOnlineValidationResult(false, Loc.T("Jwt_Status_MissingSteamId"));
        }

        await using var cmClient = new SteamCmClient(HttpClient);
        try
        {
            await cmClient.ConnectAndLogOnAsync(refreshToken, tokenInfo.SteamId, cancellationToken);
            AppLog.Info($"Token check for {tokenInfo.SteamId}: Steam accepted the token.");
            return new SteamTokenOnlineValidationResult(true, Loc.T("Token_Result_Accepted"));
        }
        catch (SteamCmException ex)
        {
            AppLog.Warn($"Token check for {tokenInfo.SteamId}: Steam answered EResult={ex.Result} ({ex.ResultName}).");
            if (ex.IsTokenFailure || ex.IsAccountFailure)
            {
                return new SteamTokenOnlineValidationResult(false, ex.Message);
            }

            throw;
        }
    }
}
