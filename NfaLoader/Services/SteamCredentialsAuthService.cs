using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NfaLoader.Localization;

namespace NfaLoader.Services;

/// <summary>Steam authenticator (Steam Guard) types, keeping only the ones this flow handles.</summary>
internal enum SteamGuardType
{
    EmailCode = 2,            // Code sent to the email address
    DeviceCode = 3,           // Mobile authenticator TOTP code
    DeviceConfirmation = 4,   // Tap confirm in the mobile app (no code needed)
    EmailConfirmation = 5     // Click confirm in the email (no code needed)
}

internal sealed record SteamGuardPrompt(SteamGuardType Type, string? AssociatedMessage);

internal sealed record CredentialsAuthResult(string RefreshToken, string AccountName, string SteamId);

internal sealed class SteamCredentialsAuthException : InvalidOperationException
{
    public SteamCredentialsAuthException(string message) : base(message)
    {
    }
}

/// <summary>
/// Trades an account name and password for a refresh token (the EYA token) through the Steam Web API (IAuthenticationService). For debugging.
/// HTTPS only, no CM connection: GetPasswordRSAPublicKey → RSA-encrypt the password → BeginAuthSessionViaCredentials
/// → (if needed) handle Steam Guard → poll PollAuthSessionStatus for the refresh_token.
/// Key point: only with platform_type=SteamClient does the issued refresh token carry the client audience (aud includes client/web/renew/derive),
/// which gives it the same permissions as a normal EYA token, so it works for client sign-in.
/// </summary>
internal sealed class SteamCredentialsAuthService
{
    private const string BaseUrl = "https://api.steampowered.com/IAuthenticationService/";
    private const int EResultOk = 1;
    private const uint PlatformTypeSteamClient = 1; // EAuthTokenPlatformType_SteamClient
    private const uint PersistencePersistent = 1;   // ESessionPersistence_Persistent
    private const int OsTypeWindows10 = 16;         // EOSType
    private const string DeviceName = "nfa.pub Loader";
    private const int PollIntervalMs = 2500;
    private const int PollTimeoutSeconds = 120;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<CredentialsAuthResult> GetRefreshTokenAsync(
        string accountName,
        string password,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(Loc.T("Creds_Progress_GettingKey"));
        var rsaKey = await GetRsaKeyAsync(accountName, cancellationToken);
        var encryptedPassword = EncryptPassword(password, rsaKey.Modulus, rsaKey.Exponent);

        progress?.Report(Loc.T("Creds_Progress_Authenticating"));
        var begin = await BeginAuthAsync(accountName, encryptedPassword, rsaKey.Timestamp, cancellationToken);

        var clientId = begin.ClientId;
        var steamId = begin.SteamId;

        // Types that need a code (email/mobile authenticator) ask for the code and submit it first; types that only need a confirm tap on the phone or in the email go straight to polling.
        var codeType = begin.AllowedConfirmations.FirstOrDefault(
            c => c is SteamGuardType.EmailCode or SteamGuardType.DeviceCode);

        if (codeType is SteamGuardType.EmailCode or SteamGuardType.DeviceCode)
        {
            var code = await guardCodeProvider(new SteamGuardPrompt(codeType, begin.GuardMessage), cancellationToken);
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new OperationCanceledException();
            }

            progress?.Report(Loc.T("Creds_Progress_SubmittingCode"));
            await SubmitGuardCodeAsync(clientId, steamId, code.Trim(), codeType, cancellationToken);
            progress?.Report(Loc.T("Creds_Progress_Polling"));
        }
        else if (begin.AllowedConfirmations.Any(
            c => c is SteamGuardType.DeviceConfirmation or SteamGuardType.EmailConfirmation))
        {
            progress?.Report(Loc.T("Creds_Progress_WaitingConfirm"));
        }
        else
        {
            progress?.Report(Loc.T("Creds_Progress_Polling"));
        }

        var maxAttempts = PollTimeoutSeconds * 1000 / PollIntervalMs;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var poll = await PollAsync(clientId, begin.RequestId, cancellationToken);
            if (poll.NewClientId != 0)
            {
                clientId = poll.NewClientId;
            }

            if (!string.IsNullOrEmpty(poll.RefreshToken))
            {
                return new CredentialsAuthResult(
                    poll.RefreshToken,
                    string.IsNullOrWhiteSpace(poll.AccountName) ? accountName : poll.AccountName,
                    steamId.ToString(CultureInfo.InvariantCulture));
            }

            await Task.Delay(PollIntervalMs, cancellationToken);
        }

        throw new SteamCredentialsAuthException(Loc.T("Creds_Error_Timeout"));
    }

    // ---- Steps ----

    private static async Task<RsaKey> GetRsaKeyAsync(string accountName, CancellationToken cancellationToken)
    {
        var request = SteamProtoWriter.Build(writer => writer.WriteString(1, accountName));
        var body = await CallAsync("GetPasswordRSAPublicKey", request, useGet: true, cancellationToken);

        string? modulus = null;
        string? exponent = null;
        ulong timestamp = 0;

        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1: modulus = reader.ReadString(wireType); break;
                case 2: exponent = reader.ReadString(wireType); break;
                case 3: timestamp = reader.ReadVarint(wireType); break;
                default: reader.Skip(wireType); break;
            }
        }

        if (string.IsNullOrEmpty(modulus) || string.IsNullOrEmpty(exponent))
        {
            throw new SteamCredentialsAuthException(Loc.T("Creds_Error_NoRsaKey"));
        }

        return new RsaKey(FromHexEven(modulus), FromHexEven(exponent), timestamp);
    }

    private static string EncryptPassword(string password, byte[] modulus, byte[] exponent)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
        var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1);
        return Convert.ToBase64String(encrypted);
    }

    private static async Task<BeginResult> BeginAuthAsync(
        string accountName, string encryptedPassword, ulong timestamp, CancellationToken cancellationToken)
    {
        // device_details.platform_type=SteamClient → the refresh token carries the client audience.
        var deviceDetails = SteamProtoWriter.Build(details =>
        {
            details.WriteString(1, DeviceName);
            details.WriteUInt32(2, PlatformTypeSteamClient);
            details.WriteInt32(3, OsTypeWindows10);
        });

        var request = SteamProtoWriter.Build(writer =>
        {
            writer.WriteString(1, DeviceName);
            writer.WriteString(2, accountName);
            writer.WriteString(3, encryptedPassword);
            writer.WriteUInt64(4, timestamp);
            writer.WriteBool(5, true);                       // remember_login
            writer.WriteUInt32(6, PlatformTypeSteamClient);  // platform_type
            writer.WriteUInt32(7, PersistencePersistent);    // persistence
            writer.WriteBytes(9, deviceDetails);
        });

        var body = await CallAsync("BeginAuthSessionViaCredentials", request, useGet: false, cancellationToken);

        ulong clientId = 0;
        ulong steamId = 0;
        var requestId = Array.Empty<byte>();
        var confirmations = new List<SteamGuardType>();
        string? guardMessage = null;

        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    clientId = reader.ReadVarint(wireType);
                    break;

                case 2:
                    requestId = reader.ReadLengthDelimited(wireType);
                    break;

                case 4:
                    var (type, message) = ReadAllowedConfirmation(reader.ReadLengthDelimited(wireType));
                    if (type is >= 2 and <= 5)
                    {
                        confirmations.Add((SteamGuardType)type);
                        guardMessage ??= message;
                    }

                    break;

                case 5:
                    steamId = reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (clientId == 0)
        {
            throw new SteamCredentialsAuthException(Loc.T("Creds_Error_BeginFailed"));
        }

        return new BeginResult(clientId, requestId, steamId, confirmations, guardMessage);
    }

    private static (int Type, string? Message) ReadAllowedConfirmation(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        var type = 0;
        string? message = null;

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1: type = (int)reader.ReadVarint(wireType); break;
                case 2: message = reader.ReadString(wireType); break;
                default: reader.Skip(wireType); break;
            }
        }

        return (type, message);
    }

    private static async Task SubmitGuardCodeAsync(
        ulong clientId, ulong steamId, string code, SteamGuardType codeType, CancellationToken cancellationToken)
    {
        var request = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt64(1, clientId);
            writer.WriteFixed64(2, steamId);  // proto: steamid is fixed64 (wire type 1), not varint
            writer.WriteString(3, code);
            writer.WriteUInt32(4, (uint)(int)codeType);
        });

        await CallAsync("UpdateAuthSessionWithSteamGuardCode", request, useGet: false, cancellationToken);
    }

    private static async Task<PollResult> PollAsync(
        ulong clientId, byte[] requestId, CancellationToken cancellationToken)
    {
        var request = SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt64(1, clientId);
            writer.WriteBytes(2, requestId);
        });

        var body = await CallAsync("PollAuthSessionStatus", request, useGet: false, cancellationToken);

        ulong newClientId = 0;
        string? refreshToken = null;
        string? accountName = null;

        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1: newClientId = reader.ReadVarint(wireType); break;
                case 3: refreshToken = reader.ReadString(wireType); break;
                case 6: accountName = reader.ReadString(wireType); break;
                default: reader.Skip(wireType); break;
            }
        }

        return new PollResult(newClientId, refreshToken ?? string.Empty, accountName ?? string.Empty);
    }

    // ---- Web transport: input_protobuf_encoded (base64 protobuf), the response is protobuf, and the status is in the x-eresult header.
    //      GetPasswordRSAPublicKey is GET-only (POST returns 405); the other methods use POST. ----

    private static async Task<byte[]> CallAsync(
        string method, byte[] request, bool useGet, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(request);

        using var response = useGet
            ? await HttpClient.GetAsync(
                $"{BaseUrl}{method}/v1/?input_protobuf_encoded={Uri.EscapeDataString(encoded)}",
                cancellationToken)
            : await PostEncodedAsync(method, encoded, cancellationToken);

        var eresult = EResultOk;
        if (response.Headers.TryGetValues("x-eresult", out var eresultValues) &&
            int.TryParse(eresultValues.FirstOrDefault(), out var parsed))
        {
            eresult = parsed;
        }
        else if (!response.IsSuccessStatusCode)
        {
            eresult = -1;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (eresult != EResultOk)
        {
            var errorMessage = response.Headers.TryGetValues("x-error_message", out var messages)
                ? messages.FirstOrDefault()
                : null;
            throw new SteamCredentialsAuthException(MapEResult(eresult, errorMessage));
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PostEncodedAsync(
        string method, string encoded, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("input_protobuf_encoded", encoded)
        });

        return await HttpClient.PostAsync($"{BaseUrl}{method}/v1/", content, cancellationToken);
    }

    private static string MapEResult(int eresult, string? errorMessage)
    {
        return eresult switch
        {
            5 => Loc.T("Creds_Error_InvalidPassword"),        // InvalidPassword
            84 => Loc.T("Creds_Error_RateLimited"),            // RateLimitExceeded
            65 or 88 => Loc.T("Creds_Error_GuardMismatch"),    // InvalidLoginAuthCode / TwoFactorCodeMismatch
            _ => string.IsNullOrWhiteSpace(errorMessage)
                ? Loc.Tf("Creds_Error_Failed_Format", eresult)
                : Loc.Tf("Creds_Error_FailedMsg_Format", eresult, errorMessage)
        };
    }

    private static byte[] FromHexEven(string hex)
    {
        return Convert.FromHexString(hex.Length % 2 == 0 ? hex : "0" + hex);
    }

    private sealed record RsaKey(byte[] Modulus, byte[] Exponent, ulong Timestamp);

    private sealed record BeginResult(
        ulong ClientId,
        byte[] RequestId,
        ulong SteamId,
        List<SteamGuardType> AllowedConfirmations,
        string? GuardMessage);

    private sealed record PollResult(ulong NewClientId, string RefreshToken, string AccountName);
}
