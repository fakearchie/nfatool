namespace NfaLoader.Models;

public sealed record JwtValidationResult(string SteamId, DateTimeOffset ExpiresAt)
{
    public TimeSpan Remaining => ExpiresAt - DateTimeOffset.Now;
}
