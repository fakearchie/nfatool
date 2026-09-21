using NfaLoader.Models;

namespace NfaLoader.Services;

// One loadout entry (team class, slot, itemdef). Shared by SO cache parsing and the one-click loadout read-back.
internal readonly record struct CsLoadoutEntry(uint ClassId, uint SlotId, uint ItemDefinition);

internal static class CsSoCacheParser
{
    public static List<CsLoadoutEntry> ParseLoadoutFromWelcome(byte[] welcomePayload, uint accountId)
    {
        var entries = new List<CsLoadoutEntry>();
        var reader = new SteamProtoReader(welcomePayload);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 3)
            {
                ParseSoCacheSubscribed(reader.ReadLengthDelimited(wireType), accountId, entries);
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return entries;
    }

    public static bool TryGetOwnerSoidFromWelcome(byte[] welcomePayload, out uint ownerType, out ulong ownerId)
    {
        ownerType = 0;
        ownerId = 0;
        var reader = new SteamProtoReader(welcomePayload);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 3)
            {
                reader.Skip(wireType);
                continue;
            }

            if (TryGetOwnerSoidFromCacheSubscribed(reader.ReadLengthDelimited(wireType), out ownerType, out ownerId))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetSoCacheVersionFromWelcome(byte[] welcomePayload, out ulong version)
    {
        version = 0;
        var reader = new SteamProtoReader(welcomePayload);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field != 3)
            {
                reader.Skip(wireType);
                continue;
            }

            if (TryGetSoCacheVersionFromCacheSubscribed(reader.ReadLengthDelimited(wireType), out version))
            {
                return true;
            }
        }

        return false;
    }

    // Applies one GC SO message, in order, to the loadout slot state: added/modified → write, destroyed/removed → delete the key.
    // SODestroy and CMsgSOMultipleObjects.objects_removed (field 5) mean "the GC deleted the slot entry",
    // which in testing happens when a slot goes back to the game's built-in default weapon; merging them as equipped entries would give the check stale values.
    public static void ApplyLoadoutSoMessage(
        IDictionary<(uint ClassId, uint SlotId), uint> state,
        uint msgType,
        byte[] payload,
        uint accountId)
    {
        try
        {
            var upserts = new List<CsLoadoutEntry>();
            var removals = new List<(uint ClassId, uint SlotId)>();

            switch (msgType)
            {
                case CsLoadoutConstants.SoCacheSubscribed:
                    ParseSoCacheSubscribed(payload, accountId, upserts);
                    break;

                case CsLoadoutConstants.SoUpdateMultiple:
                    ParseSoMultipleObjects(payload, accountId, upserts, removals);
                    break;

                case CsLoadoutConstants.SoUpdate or CsLoadoutConstants.SoCreate:
                    TryAddLoadoutEntryFromSingleObject(payload, accountId, upserts);
                    break;

                case CsLoadoutConstants.SoDestroy:
                    ParseRemovalFromSingleObject(payload, accountId, removals);
                    break;
            }

            foreach (var entry in upserts)
            {
                state[(entry.ClassId, entry.SlotId)] = entry.ItemDefinition;
            }

            foreach (var key in removals)
            {
                state.Remove(key);
            }
        }
        catch
        {
            // One message failing to parse should not stop the whole merge.
        }
    }

    public static bool IsLoadoutSoMessage(uint msgType) =>
        msgType is CsLoadoutConstants.SoCacheSubscribed
            or CsLoadoutConstants.SoUpdateMultiple
            or CsLoadoutConstants.SoUpdate
            or CsLoadoutConstants.SoCreate
            or CsLoadoutConstants.SoDestroy;

    private static bool TryGetOwnerSoidFromCacheSubscribed(byte[] body, out uint ownerType, out ulong ownerId)
    {
        ownerType = 0;
        ownerId = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 4)
            {
                return TryDecodeOwnerSoid(reader.ReadLengthDelimited(wireType), out ownerType, out ownerId);
            }

            reader.Skip(wireType);
        }

        return false;
    }

    private static bool TryGetSoCacheVersionFromCacheSubscribed(byte[] body, out ulong version)
    {
        version = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 3)
            {
                version = wireType == 1
                    ? reader.ReadFixed64(wireType)
                    : reader.ReadVarint(wireType);
                return true;
            }

            reader.Skip(wireType);
        }

        return false;
    }

    private static bool TryDecodeOwnerSoid(byte[] body, out uint ownerType, out ulong ownerId)
    {
        ownerType = 0;
        ownerId = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    ownerType = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    ownerId = reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return ownerType != 0 && ownerId != 0;
    }

    private static void ParseSoMultipleObjects(
        byte[] body,
        uint accountId,
        List<CsLoadoutEntry> upserts,
        List<(uint ClassId, uint SlotId)> removals)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            // CMsgSOMultipleObjects: objects_modified=2, objects_added=4, objects_removed=5, version=3 (fixed64).
            if (field is 2 or 4 or 5 && wireType == 2)
            {
                var obj = reader.ReadLengthDelimited(wireType);
                if (field == 5)
                {
                    TryAddRemovalFromObject(obj, accountId, removals, typeIdField: 1, objectDataField: 2);
                }
                else
                {
                    TryAddLoadoutEntryFromMultipleObject(obj, accountId, upserts);
                }
            }
            else
            {
                reader.Skip(wireType);
            }
        }
    }

    // CMsgSOSingleObject (SODestroy): type_id=2, object_data=3. A destroyed object only carries the key fields.
    private static void ParseRemovalFromSingleObject(
        byte[] body,
        uint accountId,
        List<(uint ClassId, uint SlotId)> removals)
    {
        TryAddRemovalFromObject(body, accountId, removals, typeIdField: 2, objectDataField: 3);
    }

    private static void TryAddRemovalFromObject(
        byte[] body,
        uint accountId,
        List<(uint ClassId, uint SlotId)> removals,
        int typeIdField,
        int objectDataField)
    {
        var typeId = 0;
        byte[]? objectData = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == typeIdField && wireType == 0)
            {
                typeId = (int)reader.ReadVarint(wireType);
            }
            else if (field == objectDataField && wireType == 2)
            {
                objectData = reader.ReadLengthDelimited(wireType);
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        if (objectData is null || objectData.Length == 0)
        {
            return;
        }

        uint classId;
        uint slotId;
        switch (typeId)
        {
            case CsLoadoutConstants.SoTypeEquipSlot:
            {
                if (!TryDecodeEquipSlot(objectData, out var slot) ||
                    (slot.AccountId != 0 && slot.AccountId != accountId))
                {
                    return;
                }

                classId = slot.ClassId;
                slotId = slot.SlotId;
                break;
            }

            case CsLoadoutConstants.SoTypeDefaultEquippedDefinition:
            {
                // A destroyed object may have no item_definition, so the full decode that requires a non-zero def can't be reused.
                if (!TryDecodeDefaultEquippedKey(objectData, out var acct, out classId, out slotId) ||
                    (acct != 0 && acct != accountId))
                {
                    return;
                }

                break;
            }

            default:
                return;
        }

        if (classId is not (CsLoadoutConstants.TeamTerrorist or CsLoadoutConstants.TeamCounterTerrorist))
        {
            return;
        }

        removals.Add((classId, slotId));
    }

    private static bool TryDecodeDefaultEquippedKey(
        byte[] body,
        out uint accountId,
        out uint classId,
        out uint slotId)
    {
        accountId = 0;
        classId = 0;
        slotId = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    classId = (uint)reader.ReadVarint(wireType);
                    break;

                case 4:
                    slotId = (uint)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return classId != 0 && slotId != 0;
    }

    private static void TryAddLoadoutEntryFromMultipleObject(
        byte[] body,
        uint accountId,
        List<CsLoadoutEntry> entries)
    {
        var typeId = 0;
        byte[]? objectData = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    typeId = (int)reader.ReadVarint(wireType);
                    break;

                case 2:
                    objectData = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        TryAppendDecodedEntry(typeId, objectData, accountId, entries);
    }

    private static void TryAddLoadoutEntryFromSingleObject(
        byte[] body,
        uint accountId,
        List<CsLoadoutEntry> entries)
    {
        var typeId = 0;
        byte[]? objectData = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    typeId = (int)reader.ReadVarint(wireType);
                    break;

                case 3:
                    objectData = reader.ReadLengthDelimited(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        TryAppendDecodedEntry(typeId, objectData, accountId, entries);
    }

    private static void TryAppendDecodedEntry(
        int typeId,
        byte[]? objectData,
        uint accountId,
        List<CsLoadoutEntry> entries)
    {
        if (objectData is null || objectData.Length == 0)
        {
            return;
        }

        if (typeId == CsLoadoutConstants.SoTypeEquipSlot)
        {
            TryDecodeEquipSlotEntry(objectData, accountId, entries);
            return;
        }

        if (typeId != CsLoadoutConstants.SoTypeDefaultEquippedDefinition ||
            !TryDecodeDefaultEquippedDefinition(objectData, out var entry) ||
            entry.AccountId != accountId)
        {
            return;
        }

        TryAddValidatedEntry(entries, entry.ClassId, entry.SlotId, entry.ItemDefinition);
    }

    private static void ParseSoCacheSubscribed(byte[] body, uint accountId, List<CsLoadoutEntry> entries)
    {
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 2)
            {
                ParseSubscribedType(reader.ReadLengthDelimited(wireType), accountId, entries);
            }
            else
            {
                reader.Skip(wireType);
            }
        }
    }

    private static void ParseSubscribedType(byte[] body, uint accountId, List<CsLoadoutEntry> entries)
    {
        var typeId = 0;
        var objectDataList = new List<byte[]>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    typeId = (int)reader.ReadVarint(wireType);
                    break;

                case 2:
                    objectDataList.Add(reader.ReadLengthDelimited(wireType));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (typeId == 0)
        {
            return;
        }

        foreach (var objectData in objectDataList)
        {
            TryAppendDecodedEntry(typeId, objectData, accountId, entries);
        }
    }

    private static bool TryDecodeEquipSlotEntry(
        byte[] body,
        uint accountId,
        List<CsLoadoutEntry> entries)
    {
        if (!TryDecodeEquipSlot(body, out var slot))
        {
            return false;
        }

        if (slot.AccountId != 0 && slot.AccountId != accountId)
        {
            return false;
        }

        var itemDefinition = slot.ItemDefinition;
        if (itemDefinition == 0)
        {
            itemDefinition = TryGetDefaultItemDefinition(slot.ItemId, out var defaultDefinition)
                ? defaultDefinition
                : slot.ItemId is > 0 and <= 10_000
                    ? (uint)slot.ItemId
                    : 0;
        }

        return TryAddValidatedEntry(entries, slot.ClassId, slot.SlotId, itemDefinition);
    }

    private static bool TryGetDefaultItemDefinition(ulong itemId, out uint itemDefinition)
    {
        if ((itemId & CsLoadoutConstants.ItemIdDefaultItemMask) !=
            CsLoadoutConstants.ItemIdDefaultItemMask)
        {
            itemDefinition = 0;
            return false;
        }

        itemDefinition = (uint)(itemId & ~CsLoadoutConstants.ItemIdDefaultItemMask);
        return itemDefinition is > 0 and <= 10_000;
    }

    private static bool TryDecodeEquipSlot(
        byte[] body,
        out (uint AccountId, uint ClassId, uint SlotId, ulong ItemId, uint ItemDefinition) slot)
    {
        slot = default;
        uint accountId = 0;
        uint classId = 0;
        uint slotId = 0;
        ulong itemId = 0;
        uint itemDefinition = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    classId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    slotId = (uint)reader.ReadVarint(wireType);
                    break;

                case 4:
                    itemId = reader.ReadVarint(wireType);
                    break;

                case 5:
                    itemDefinition = (uint)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (classId == 0 || slotId == 0)
        {
            return false;
        }

        slot = (accountId, classId, slotId, itemId, itemDefinition);
        return true;
    }

    private static bool TryAddValidatedEntry(
        List<CsLoadoutEntry> entries,
        uint classId,
        uint slotId,
        uint itemDefinition)
    {
        if (classId is not (CsLoadoutConstants.TeamTerrorist or CsLoadoutConstants.TeamCounterTerrorist))
        {
            return false;
        }

        // Accept every weapon loadout slot: 1=melee, 2-7=pistols, 8-13=mid-tier, 14-19=rifles, 34=Zeus.
        // (The R8 feature's planner only looks at the pistol slots, so widening this doesn't affect it, but it lets the full loadout read-back check cover rifles and SMGs.)
        if (slotId is not (1 or (>= 2 and <= 19) or 34))
        {
            return false;
        }

        if (itemDefinition is 0 or > 10_000)
        {
            return false;
        }

        entries.Add(new CsLoadoutEntry(classId, slotId, itemDefinition));
        return true;
    }

    private static bool TryDecodeDefaultEquippedDefinition(
        byte[] body,
        out (uint AccountId, uint ItemDefinition, uint ClassId, uint SlotId) entry)
    {
        entry = default;
        uint accountId = 0;
        uint itemDefinition = 0;
        uint classId = 0;
        uint slotId = 0;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    itemDefinition = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    classId = (uint)reader.ReadVarint(wireType);
                    break;

                case 4:
                    slotId = (uint)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (accountId == 0 || classId == 0 || slotId == 0 || itemDefinition == 0)
        {
            return false;
        }

        entry = (accountId, itemDefinition, classId, slotId);
        return true;
    }
}
