using System.Globalization;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class CsLoadoutService
{
    private static readonly TimeSpan EquipSoWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly SemaphoreSlim EquipGate = new(1, 1);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Apply a whole preset in one go: write the stock weapon (itemdef) for every slot on both teams at once, then read it back to verify.
    public async Task<CsLoadoutApplyResult> ApplyPresetAsync(
        CsLoadoutPreset preset,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!await EquipGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new InvalidOperationException(Loc.T("Cs_Loadout_Busy"));
        }

        try
        {
            return await ApplyPresetCoreAsync(preset, refreshToken, steamId, cancellationToken);
        }
        finally
        {
            EquipGate.Release();
        }
    }

    private async Task<CsLoadoutApplyResult> ApplyPresetCoreAsync(
        CsLoadoutPreset preset,
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
            {
                throw new InvalidOperationException(Loc.T("Cs_Loadout_BadSteam64"));
            }

            var accountId = CsGcSession.GetAccountId(steamId64);

            var requested = new List<(uint Team, uint Slot, uint Def)>();
            foreach (var (slot, def) in preset.T)
            {
                requested.Add((CsLoadoutConstants.TeamTerrorist, slot, def));
            }
            foreach (var (slot, def) in preset.Ct)
            {
                requested.Add((CsLoadoutConstants.TeamCounterTerrorist, slot, def));
            }

            if (requested.Count == 0)
            {
                return new CsLoadoutApplyResult(0, 0, []);
            }

            // Pre-check: only send entries that are in the weapon catalog, usable by the team, and match the slot group; invalid entries (a hand-edited settings.json and so on) are counted as failures right away.
            // This is what makes the later "no explicit SO entry ⇒ counts as done" rule hold: for a valid request the GC either rewrites the SO, or the slot already holds the target weapon.
            var failures = new List<string>();
            var validRequests = new List<(uint Team, uint Slot, uint Def)>();
            foreach (var item in requested)
            {
                var weapon = CsWeaponCatalog.ByDef(item.Def);
                if (weapon is not null &&
                    weapon.UsableBy(item.Team == CsLoadoutConstants.TeamCounterTerrorist) &&
                    CsWeaponCatalog.SlotsForGroup(CsWeaponCatalog.GroupOf(weapon)).Contains(item.Slot))
                {
                    validRequests.Add(item);
                }
                else
                {
                    failures.Add(DescribeSlot(item.Team, item.Slot, item.Def));
                }
            }

            await using var cmClient = new SteamCmClient(HttpClient);
            await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);

            try
            {
                await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                var welcomePayload = await CsGcSession.ConnectAsync(cmClient, cancellationToken);

                // Read the current loadout (the SO cache embedded in welcome, widened to all slots). The SO cache only stores explicit entries that differ
                // from the game's built-in default layout; a missing slot holds its built-in default weapon. Resolve each slot's actual weapon with "explicit entry → use it if present, otherwise look up
                // the default table", and diff against that. This is authoritative and needs no "missing = done" guess (see the default table in CsLoadoutConstants).
                var explicitCurrent = new Dictionary<(uint Team, uint Slot), uint>();
                foreach (var entry in CsSoCacheParser.ParseLoadoutFromWelcome(welcomePayload, accountId))
                {
                    explicitCurrent[(entry.ClassId, entry.SlotId)] = entry.ItemDefinition;
                }

                // Slots that already hold the target weapon (after resolving) are not sent again; only the differences are sent.
                var toSend = new List<(uint Team, uint Slot, uint Def)>();
                foreach (var item in validRequests)
                {
                    if (ResolveSlot(explicitCurrent, item.Team, item.Slot) != item.Def)
                    {
                        toSend.Add(item);
                    }
                }

                if (toSend.Count == 0)
                {
                    AppLog.Info($"Preset loadout: {validRequests.Count} slots already match, nothing to change ({failures.Count} invalid entries).");
                    return new CsLoadoutApplyResult(requested.Count, validRequests.Count, failures);
                }

                var tappedMessages = new List<SteamCmClient.SteamGcClientMessage>();
                var tappedMessagesLock = new object();
                cmClient.SetGcMessageTap(message =>
                {
                    lock (tappedMessagesLock)
                    {
                        tappedMessages.Add(message);
                    }
                });

                // The incremental final state starts from the "initial explicit entries" and applies each SO update/delete the GC sends back.
                var finalExplicit = new Dictionary<(uint Team, uint Slot), uint>(explicitCurrent);
                try
                {
                    CsSoCacheParser.TryGetSoCacheVersionFromWelcome(welcomePayload, out var soCacheVersion);
                    var changeNum = BuildChangeNum(soCacheVersion);

                    var slotEntries = toSend
                        .Select(r => (r.Team, r.Slot, CsLoadoutConstants.BuildDefaultBaseItemId(r.Def)))
                        .ToList();

                    // Messages captured before sending (such as the SO cache trailing the handshake) are not a response to this 2531, so record a baseline and skip them.
                    int baselineTapCount;
                    lock (tappedMessagesLock)
                    {
                        baselineTapCount = tappedMessages.Count;
                    }

                    await cmClient.SendGcProtobufMessageAsync(
                        CsGcSession.Cs2AppId,
                        CsLoadoutConstants.AdjustEquipSlotsManual,
                        EncodeAdjustEquipSlotsMulti(slotEntries, changeNum),
                        cancellationToken);

                    try
                    {
                        // The GC answers every 2531 with an ACK of the same number, and SO updates arrive in the same batch as the ACK; waiting for any response is enough.
                        await WaitForEquipResponseAsync(
                            cmClient,
                            tappedMessages,
                            tappedMessagesLock,
                            baselineTapCount,
                            cancellationToken);

                        // A full loadout can come back as several SO updates, so wait a little after the first one to collect the trailing updates before merging.
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // The equip request was already sent and the GC has most likely applied it; reporting "cancelled" here would make the user think nothing changed.
                        throw new InvalidOperationException(Loc.T("Cs_Loadout_CancelledAfterSend"));
                    }

                    ApplyTappedLoadoutMessages(finalExplicit, tappedMessages, tappedMessagesLock, baselineTapCount, accountId);
                }
                finally
                {
                    cmClient.SetGcMessageTap(null);
                }

                // Verify: each slot's actual weapon = the explicit entry (if present), otherwise the slot's built-in default. Missing always means default (CS2 semantics),
                // so whether or not the GC applied it, this reflects what is really in game. A slot that "is already default" is not reported as a failure (the root cause of the old
                // non-30/30 results), and a change the GC did not apply is not reported as a success (if incremental updates are lost, the authoritative welcome re-read below corrects it).
                bool IsSatisfied((uint Team, uint Slot, uint Def) item) =>
                    ResolveSlot(finalExplicit, item.Team, item.Slot) == item.Def;

                // If some slots still do not match, request welcome again to read back the full SO cache (tested: a repeated hello always resends the full cache), and use that authoritative snapshot
                // to rebuild the whole set of explicit entries. A plain merge would leave stale values for entries the GC deleted and give false reports.
                if (!validRequests.All(IsSatisfied))
                {
                    try
                    {
                        var freshWelcome = await CsGcSession.RequestWelcomeAsync(
                            cmClient,
                            TimeSpan.FromSeconds(15),
                            cancellationToken);
                        finalExplicit = new Dictionary<(uint Team, uint Slot), uint>();
                        foreach (var entry in CsSoCacheParser.ParseLoadoutFromWelcome(freshWelcome, accountId))
                        {
                            finalExplicit[(entry.ClassId, entry.SlotId)] = entry.ItemDefinition;
                        }
                    }
                    catch (TimeoutException)
                    {
                        // If the fallback read-back times out, keep the current verdict and still return the result.
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException(Loc.T("Cs_Loadout_CancelledAfterSend"));
                    }
                }

                var confirmed = 0;
                var implicitConfirmed = 0;
                foreach (var item in validRequests)
                {
                    if (IsSatisfied(item))
                    {
                        confirmed++;
                        if (!finalExplicit.ContainsKey((item.Team, item.Slot)))
                        {
                            implicitConfirmed++;
                        }
                    }
                    else
                    {
                        failures.Add(DescribeSlot(item.Team, item.Slot, item.Def));
                    }
                }

                var result = new CsLoadoutApplyResult(requested.Count, confirmed, failures);
                AppLog.Info(
                    $"Preset loadout: requested {result.Requested}, confirmed {result.Confirmed}" +
                    $" ({implicitConfirmed} of them already at built-in default), failed {failures.Count}.");
                return result;
            }
            finally
            {
                try
                {
                    await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
                }
                catch
                {
                    // best-effort
                }
            }
        }
        catch (Exception ex)
        {
            if (IsSteamSessionConflict(ex))
            {
                var conflict = new InvalidOperationException(
                    (ex.Data["CmConflict"] as string) == "SessionReplaced"
                        ? ex.Message
                        : Loc.T("Cs_Loadout_CmDisconnected"),
                    ex);
                AppLog.Error($"Preset loadout failed: {conflict.Message}");
                throw conflict;
            }

            AppLog.Error($"Preset loadout failed: {ex.Message}");
            throw;
        }
    }

    // Wait for any GC response to 2531 (ACK or SO update). Does not throw on timeout: the later "merge + fresh welcome fallback" gives the verdict.
    private static async Task WaitForEquipResponseAsync(
        SteamCmClient cmClient,
        List<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + EquipSoWaitTimeout;
        var processedCount = startIndex;
        var waitTypes = new[]
        {
            CsLoadoutConstants.AdjustEquipSlotsManual,
            CsLoadoutConstants.SoUpdateMultiple,
            CsLoadoutConstants.SoCacheSubscribed,
            CsLoadoutConstants.SoUpdate,
            CsLoadoutConstants.SoCreate,
            CsLoadoutConstants.SoDestroy
        };

        while (DateTimeOffset.UtcNow < deadline)
        {
            List<SteamCmClient.SteamGcClientMessage> pendingMessages;
            lock (tappedMessagesLock)
            {
                pendingMessages = tappedMessages
                    .Skip(processedCount)
                    .ToList();
                processedCount = tappedMessages.Count;
            }

            foreach (var message in pendingMessages)
            {
                if (IsEquipResponseMessage(message.MessageType))
                {
                    return;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var waitSlice = remaining < TimeSpan.FromSeconds(1)
                ? remaining
                : TimeSpan.FromSeconds(1);

            foreach (var msgType in waitTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.WaitForGcMessageAsync(
                        CsGcSession.Cs2AppId,
                        msgType,
                        waitSlice,
                        cancellationToken,
                        cacheUnmatched: true);
                    return;
                }
                catch (TimeoutException)
                {

                }
            }
        }
    }

    private static bool IsEquipResponseMessage(uint msgType) =>
        msgType == CsLoadoutConstants.AdjustEquipSlotsManual ||
        CsSoCacheParser.IsLoadoutSoMessage(msgType);

    // Apply the GC SO messages captured after sending to the explicit entry state in arrival order (skipping the handshake messages before the baseline).
    private static void ApplyTappedLoadoutMessages(
        Dictionary<(uint Team, uint Slot), uint> state,
        IReadOnlyList<SteamCmClient.SteamGcClientMessage> tappedMessages,
        object tappedMessagesLock,
        int startIndex,
        uint accountId)
    {
        List<SteamCmClient.SteamGcClientMessage> messages;
        lock (tappedMessagesLock)
        {
            messages = tappedMessages.Skip(startIndex).ToList();
        }

        foreach (var message in messages)
        {
            if (CsSoCacheParser.IsLoadoutSoMessage(message.MessageType))
            {
                CsSoCacheParser.ApplyLoadoutSoMessage(state, message.MessageType, message.Payload, accountId);
            }
        }
    }

    // A slot's actual weapon: the explicit SO entry first, otherwise the slot's built-in default; returns 0 if neither exists (a slot not in the table).
    private static uint ResolveSlot(
        IReadOnlyDictionary<(uint Team, uint Slot), uint> explicitEntries,
        uint team,
        uint slot) =>
        explicitEntries.TryGetValue((team, slot), out var def)
            ? def
            : CsLoadoutConstants.TryGetImplicitDefault(team, slot, out var fallback)
                ? fallback
                : 0;

    private static string DescribeSlot(uint team, uint slot, uint def)
    {
        var teamName = team == CsLoadoutConstants.TeamTerrorist ? "T" : "CT";
        var name = CsWeaponCatalog.ByDef(def)?.LocalizedName ?? def.ToString(CultureInfo.InvariantCulture);
        return $"{teamName} #{slot} {name}";
    }

    // Decide using the language-neutral marker SteamCmClient puts on the exception, not the localized Message text.
    private static bool IsSteamSessionConflict(Exception ex) =>
        ex.Data["CmConflict"] is string;

    private static uint BuildChangeNum(ulong soCacheVersion) =>
        soCacheVersion != 0
            ? (uint)((soCacheVersion + 1) & 0xFFFFFFFF)
            : 1;

    // Batch equip message with its own itemId per slot (the whole loadout is sent at once).
    private static byte[] EncodeAdjustEquipSlotsMulti(
        IReadOnlyList<(uint ClassId, uint SlotId, ulong ItemId)> slots,
        uint changeNum) =>
        SteamProtoWriter.Build(writer =>
        {
            foreach (var (classId, slotId, itemId) in slots)
            {
                writer.WriteBytes(1, SteamProtoWriter.Build(slotWriter =>
                {
                    slotWriter.WriteUInt32(1, classId);
                    slotWriter.WriteUInt32(2, slotId);
                    slotWriter.WriteUInt64(3, itemId);
                }));
            }

            writer.WriteUInt32(2, changeNum);
        });
}
