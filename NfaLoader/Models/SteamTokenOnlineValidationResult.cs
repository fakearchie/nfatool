namespace NfaLoader.Models;

public sealed record SteamTokenOnlineValidationResult(bool IsValid, string Status);

/// <summary>Steam refused the login token itself, as opposed to Steam being unreachable.</summary>
public sealed class SteamTokenRefusedException(string message) : InvalidOperationException(message);
