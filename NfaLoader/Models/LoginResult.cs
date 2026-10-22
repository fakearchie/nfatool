namespace NfaLoader.Models;

public sealed record LoginResult(string AccountName, string SteamId, DateTimeOffset ExpiresAt)
{
    public TimeSpan Remaining => ExpiresAt - DateTimeOffset.Now;

    /// <summary>Where Steam's connection log ended when Steam was started, for checking that it really signed in.</summary>
    public NfaLoader.Services.SteamSignInMark? SignInMark { get; init; }
}
