using System.Text;
using System.Text.Json;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

public sealed class JwtTokenService
{
    /// <summary>Length of an Ed25519 signature (64 bytes) in base64url without padding. Steam signs its tokens with EdDSA.</summary>
    internal const int EdDsaSignatureLength = 86;

    public JwtValidationResult Validate(string refreshToken)
    {
        var info = Inspect(refreshToken);
        if (!info.IsValid)
        {
            throw new InvalidOperationException(info.Status);
        }

        return new JwtValidationResult(
            info.SteamId ?? throw new InvalidOperationException(Loc.T("Jwt_Status_MissingSteamId")),
            info.ExpiresAt ?? throw new InvalidOperationException(Loc.T("Jwt_Status_MissingExpiry")));
    }

    public JwtTokenInfo Inspect(string refreshToken)
    {
        var parts = refreshToken.Split('.');
        if (parts.Length != 3)
        {
            return Invalid(Loc.T("Jwt_Status_BadFormat"));
        }

        // Only the payload is decoded below, but Steam checks the whole string. Text stuck to the header or the signature, or a
        // signature cut short, would otherwise show as a valid token here and then fail at Steam or leave Steam at its sign-in screen.
        if (Array.Exists(parts, part => part.Length == 0 || !part.All(IsBase64UrlChar)) ||
            !TryReadHeaderAlgorithm(parts[0], out var algorithm))
        {
            return Invalid(Loc.T("Jwt_Status_IncompleteOrExtra"));
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (Exception)
        {
            return Invalid(Loc.T("Jwt_Status_Unparseable"));
        }

        // Input can come from untrusted sources such as the clipboard, so payload field types cannot be assumed:
        // JsonElement's GetString/GetInt64/TryGetInt64 all throw when the ValueKind does not match,
        // so every ValueKind is checked first here, and a malformed token returns Invalid instead of throwing.
        using (payload)
        {
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid(Loc.T("Jwt_Status_Unparseable"));
        }

        // The SteamID and expiry are still returned, so the account card and support can see which account the cut-off token belongs to.
        if (algorithm == "EdDSA" && parts[2].Length != EdDsaSignatureLength)
        {
            return Invalid(Loc.T("Jwt_Status_IncompleteOrExtra"), GetSteamId(root), GetExpiresAt(root));
        }

        if (!root.TryGetProperty("iss", out var issuer) ||
            issuer.ValueKind != JsonValueKind.String ||
            issuer.GetString() != "steam")
        {
            return Invalid(Loc.T("Jwt_Status_NotSteamToken"));
        }

        if (!HasClientAudience(root))
        {
            return Invalid(Loc.T("Jwt_Status_MissingClientAudience"), GetSteamId(root), GetExpiresAt(root));
        }

        if (!root.TryGetProperty("sub", out var subject))
        {
            return Invalid(Loc.T("Jwt_Status_MissingSteamId"), expiresAt: GetExpiresAt(root));
        }

        if (!root.TryGetProperty("exp", out var exp))
        {
            return Invalid(Loc.T("Jwt_Status_MissingExpiry"), GetSteamId(root));
        }

        var steamId = subject.ValueKind == JsonValueKind.String ? subject.GetString() : null;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return Invalid(Loc.T("Jwt_Status_EmptySteamId"), expiresAt: GetExpiresAt(root));
        }

        if (exp.ValueKind != JsonValueKind.Number ||
            !exp.TryGetInt64(out var expSeconds) ||
            FromUnixSecondsSafe(expSeconds) is not { } expiresAt)
        {
            return Invalid(Loc.T("Jwt_Status_ExpiryUnparseable"), steamId);
        }

        var now = DateTimeOffset.Now;
        var remaining = expiresAt - now;
        if (expiresAt <= now)
        {
            return new JwtTokenInfo(steamId, expiresAt, false, Loc.T("Jwt_Status_Expired"), remaining);
        }

        if (root.TryGetProperty("nbf", out var notBeforeElement) &&
            notBeforeElement.ValueKind == JsonValueKind.Number &&
            notBeforeElement.TryGetInt64(out var nbfSeconds) &&
            FromUnixSecondsSafe(nbfSeconds) is { } notBefore &&
            notBefore > now)
        {
            return new JwtTokenInfo(steamId, expiresAt, false, Loc.T("Jwt_Status_NotYetValid"), remaining);
        }

        return new JwtTokenInfo(steamId, expiresAt, true, Loc.T("Jwt_Status_Valid"), remaining);
        }
    }

    private static bool HasClientAudience(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var audience))
        {
            return false;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            return audience.GetString() == "client";
        }

        if (audience.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in audience.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() == "client")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    /// <summary>Decodes the header, which must be a JSON object, and reads its "alg" (null when it has none).</summary>
    private static bool TryReadHeaderAlgorithm(string encodedHeader, out string? algorithm)
    {
        algorithm = null;
        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(encodedHeader));
            if (header.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (header.RootElement.TryGetProperty("alg", out var alg) && alg.ValueKind == JsonValueKind.String)
            {
                algorithm = alg.GetString();
            }

            return true;
        }
        catch (Exception)
        {
            // Same as the payload: pasted text is untrusted, so a header that does not decode is reported, never thrown.
            return false;
        }
    }

    private static string Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 += new string('=', 4 - padding);
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static JwtTokenInfo Invalid(
        string status,
        string? steamId = null,
        DateTimeOffset? expiresAt = null)
    {
        var remaining = expiresAt.HasValue ? expiresAt.Value - DateTimeOffset.Now : (TimeSpan?)null;
        return new JwtTokenInfo(steamId, expiresAt, false, status, remaining);
    }

    private static string? GetSteamId(JsonElement root)
    {
        return root.TryGetProperty("sub", out var subject) && subject.ValueKind == JsonValueKind.String
            ? subject.GetString()
            : null;
    }

    private static DateTimeOffset? GetExpiresAt(JsonElement root)
    {
        return root.TryGetProperty("exp", out var exp) &&
            exp.ValueKind == JsonValueKind.Number &&
            exp.TryGetInt64(out var seconds)
            ? FromUnixSecondsSafe(seconds)
            : null;
    }

    private static DateTimeOffset? FromUnixSecondsSafe(long seconds)
    {
        // DateTimeOffset.FromUnixTimeSeconds only accepts 0001-01-01 to 9999-12-31 and throws ArgumentOutOfRangeException outside that range.
        return seconds is >= -62_135_596_800 and <= 253_402_300_799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}

