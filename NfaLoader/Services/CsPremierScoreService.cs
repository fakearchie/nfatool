using System.Globalization;
using System.Net;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class CsPremierScoreService
{
    private const uint ClientRequestPlayersProfile = 9127;
    private const uint PlayersProfile = 9128;
    private const uint MatchmakingClient2GCHello = 9109;
    private const uint MatchmakingGC2ClientHello = 9110;
    private const uint PremierRankTypeId = 11;
    // 9110 may be pushed unprompted before or after the GC welcome; the wait must cover the handshake window, and the receive layer also buffers messages that arrive early.
    // The "disconnect GC, then reconnect" retry loop is still kept (same as cooldown.js: 6 rounds, 11 seconds of waiting per round,
    // reconnect 2.5 seconds after disconnecting), plus an overall time limit so GC welcome retries within one round cannot drag out the total time.
    private const int MaxHelloCycles = 6;
    private static readonly TimeSpan HelloWaitTimeout = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan CachedHelloPollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GcReconnectDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan HelloTotalBudget = TimeSpan.FromSeconds(100);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<CsPremierScoreResult> QueryAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_BadSteam64"));
        }

        var accountId = CsGcSession.GetAccountId(steamId64);
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);

        try
        {
            var helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);

            var webSession = await SteamWebSession.BuildAsync(cmClient, refreshToken, steamId, cancellationToken);

            await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
            await CsGcSession.ConnectAsync(cmClient, cancellationToken);

            // Cooldown/VAC can only come from the GC's MatchmakingGC2ClientHello(9110): PlayersProfile for your own
            // account always has an empty penalty field. The 9110 waiter is attached before entering 730 so a 9110 pushed unprompted
            // during the welcome phase is not missed; here we also send the 9109 request, then request PlayersProfile as usual for the Premier rating/level.
            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingClient2GCHello,
                [],
                cancellationToken);

            var profileTask = cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                PlayersProfile,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                ClientRequestPlayersProfile,
                EncodePlayersProfileRequest(accountId),
                cancellationToken);

            var profileMessage = await profileTask;
            var profile = DecodePlayersProfile(accountId, profileMessage.Payload);

            var helloData = await helloTask;
            helloData ??= await WaitForMatchmakingHelloAsync(
                cmClient,
                CachedHelloPollTimeout,
                cancellationToken);
            var helloDeadline = DateTimeOffset.UtcNow + HelloTotalBudget;

            for (var cycle = 2;
                helloData is null && cycle <= MaxHelloCycles && DateTimeOffset.UtcNow < helloDeadline;
                cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.SetGamesPlayedAsync([], cancellationToken);
                    await Task.Delay(GcReconnectDelay, cancellationToken);
                    helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);
                    await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                    await CsGcSession.ConnectAsync(cmClient, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // GC reconnect failed: the Premier rating is already in hand, so return the cooldown as unknown.
                    break;
                }

                await cmClient.SendGcProtobufMessageAsync(
                    CsGcSession.Cs2AppId,
                    MatchmakingClient2GCHello,
                    [],
                    cancellationToken);
                helloData = await helloTask;
                helloData ??= await WaitForMatchmakingHelloAsync(
                    cmClient,
                    CachedHelloPollTimeout,
                    cancellationToken);
            }

            var premier = profile.Rankings.FirstOrDefault(ranking =>
                ranking.RankTypeId == PremierRankTypeId);

            return new CsPremierScoreResult(
                steamId,
                accountId,
                premier,
                profile.Rankings,
                helloData?.PenaltySeconds,
                helloData?.PenaltyReason,
                helloData?.VacBanned,
                profile.PlayerLevel,
                profile.InMatch);
        }
        finally
        {
            try
            {
                await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
            }
            catch
            {
                // Best-effort cleanup before logoff.
            }
        }
    }

    /// <summary>Waits one round for 9110; returns null on timeout (the caller disconnects GC, reconnects and tries again). 9110 and the PlayersProfile
    /// account entry are the same proto message type, so DecodeAccountProfile is reused directly.</summary>
    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return await WaitForMatchmakingHelloAsync(cmClient, HelloWaitTimeout, cancellationToken);
    }

    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingGC2ClientHello,
                timeout,
                cancellationToken,
                cacheUnmatched: true);
            return DecodeAccountProfile(message.Payload);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static byte[] EncodePlayersProfileRequest(uint accountId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(3, accountId);
            writer.WriteUInt32(4, 32);
        });
    }

    private static CsAccountProfile DecodePlayersProfile(uint requestedAccountId, byte[] body)
    {
        var profiles = new List<CsAccountProfile>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    profiles.Add(DecodeAccountProfile(reader.ReadLengthDelimited(wireType)));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        var profile = profiles.FirstOrDefault(value => value.AccountId == requestedAccountId)
            ?? profiles.FirstOrDefault();

        if (profile is null)
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_NoProfile"));
        }

        return profile;
    }

    private static CsAccountProfile DecodeAccountProfile(byte[] body)
    {
        uint accountId = 0;
        uint penaltySeconds = 0;
        uint penaltyReason = 0;
        var vacBanned = 0;
        int? playerLevel = null;
        var inMatch = false;
        var rankings = new List<CsRankingInfo>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    inMatch = true;
                    reader.Skip(wireType);
                    break;

                case 4:
                {
                    // penalty_seconds is actually signed: once the cooldown has expired the GC sends "negative seconds remaining", which protobuf sign-extends into
                    // a 64-bit varint. Reading it straight as uint truncates it to an absurd number near 2^32 (a freshly unbanned account once showed as "49707 days...").
                    // Read it as int32, and turn anything ≤0 (expired / no cooldown) into zero.
                    var penalty = unchecked((int)reader.ReadVarint(wireType));
                    penaltySeconds = penalty > 0 ? (uint)penalty : 0;
                    break;
                }

                case 5:
                    penaltyReason = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    vacBanned = (int)reader.ReadVarint(wireType);
                    break;

                case 7:
                case 20:
                    rankings.Add(DecodeRanking(reader.ReadLengthDelimited(wireType)));
                    break;

                case 17:
                    playerLevel = (int)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsAccountProfile(
            accountId,
            rankings,
            penaltySeconds,
            penaltyReason,
            vacBanned,
            playerLevel,
            inMatch);
    }

    private static CsRankingInfo DecodeRanking(byte[] body)
    {
        uint rankTypeId = 0;
        uint rankId = 0;
        uint wins = 0;
        uint? mapId = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    rankId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    wins = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    rankTypeId = (uint)reader.ReadVarint(wireType);
                    break;

                case 13:
                    mapId ??= DecodePerMapRankMapId(reader.ReadLengthDelimited(wireType));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsRankingInfo(rankTypeId, rankId, wins, mapId);
    }

    private static uint? DecodePerMapRankMapId(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return (uint)reader.ReadVarint(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private sealed record CsAccountProfile(
        uint AccountId,
        IReadOnlyList<CsRankingInfo> Rankings,
        uint PenaltySeconds,
        uint PenaltyReason,
        int VacBanned,
        int? PlayerLevel,
        bool InMatch);
}
