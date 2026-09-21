using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NfaLoader.Services;

/// <summary>
/// Steam mobile authenticator (Steam Guard mobile) TOTP code generator: derives the 5-character code from shared_secret (base64).
/// Same algorithm as the common community implementations (node-steam-totp and others): HMAC-SHA1(secret, floor(unixTime/30) big-endian) → dynamic truncation
/// → map each digit to the Steam alphabet. Used during bulk import to pass 2FA for accounts that carry a shared_secret (no manual code entry).
/// </summary>
internal static class SteamTotp
{
    // Steam's code alphabet (26 characters), matching what the mobile authenticator shows.
    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>Generates the current 5-character Steam Guard code from a base64 shared_secret; returns null if the secret is empty or invalid.</summary>
    public static string? GenerateAuthCode(string? sharedSecretBase64, long? unixTimeSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(sharedSecretBase64))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(sharedSecretBase64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (key.Length == 0)
        {
            return null;
        }

        var time = unixTimeSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, time / 30L);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        // Dynamic truncation: the low 4 bits of the last byte give the offset; read a 31-bit integer from the 4 bytes there.
        var offset = hash[19] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24)
                 | ((hash[offset + 1] & 0xFF) << 16)
                 | ((hash[offset + 2] & 0xFF) << 8)
                 | (hash[offset + 3] & 0xFF);

        Span<char> chars = stackalloc char[5];
        for (var i = 0; i < 5; i++)
        {
            chars[i] = Alphabet[code % Alphabet.Length];
            code /= Alphabet.Length;
        }

        return new string(chars);
    }
}
