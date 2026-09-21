namespace NfaLoader.Models;

internal static class CsLoadoutConstants
{
    public const uint AdjustEquipSlotsManual = 2531;

    public const uint SoCreate = 21;
    public const uint SoUpdate = 22;
    public const uint SoDestroy = 23;
    public const uint SoCacheSubscribed = 24;
    public const uint SoUpdateMultiple = 26;
    public const uint SoCacheSubscriptionRefresh = 28;
    public const int SoTypeEquipSlot = 3;
    public const int SoTypeDefaultEquippedDefinition = 43;
    public const uint SoOwnerTypeIndividual = 1;

    public const uint TeamTerrorist = 2;
    public const uint TeamCounterTerrorist = 3;

    public const ulong ItemIdDefaultItemMask = 0xF000000000000000;
    public const uint ItemDefinitionRevolver = 64;
    public const uint ItemDefinitionDeagle = 1;

    public const uint SecondarySlotDeagleDefault = 6;

    public static readonly uint[] SecondarySlots = [3, 4, 5, 6, 7];

    public static ulong BuildDefaultBaseItemId(uint itemDefinition) =>
        ItemIdDefaultItemMask | itemDefinition;

    // The game's built-in default weapon itemdef for each (team, slot). Taken from the flexible_loadout_default markers in items_game.txt
    // (team-specific entries told apart by used_by_classes), and checked slot by slot against the GC on a test account: the slots "absent" from the SO cache are exactly
    // the slots whose target == this table's default. With this, any slot's real weapon = the explicit SO entry (if present), otherwise this default. Verification no longer relies on
    // the "absent + GC responded" guess, so it neither reports an "already default" slot as failed nor reports success when the GC change did not apply.
    private static readonly IReadOnlyDictionary<(uint Team, uint Slot), uint> DefaultLoadout =
        new Dictionary<(uint, uint), uint>
        {
            // T: secondary0-4 / smg0-4 / rifle0-4
            [(TeamTerrorist, 2)] = 4,   [(TeamTerrorist, 3)] = 2,   [(TeamTerrorist, 4)] = 36,
            [(TeamTerrorist, 5)] = 30,  [(TeamTerrorist, 6)] = 1,
            [(TeamTerrorist, 8)] = 35,  [(TeamTerrorist, 9)] = 25,  [(TeamTerrorist, 10)] = 23,
            [(TeamTerrorist, 11)] = 19, [(TeamTerrorist, 12)] = 17,
            [(TeamTerrorist, 14)] = 13, [(TeamTerrorist, 15)] = 7,  [(TeamTerrorist, 16)] = 40,
            [(TeamTerrorist, 17)] = 39, [(TeamTerrorist, 18)] = 9,

            // CT
            [(TeamCounterTerrorist, 2)] = 32,  [(TeamCounterTerrorist, 3)] = 2,   [(TeamCounterTerrorist, 4)] = 36,
            [(TeamCounterTerrorist, 5)] = 3,   [(TeamCounterTerrorist, 6)] = 1,
            [(TeamCounterTerrorist, 8)] = 35,  [(TeamCounterTerrorist, 9)] = 25,  [(TeamCounterTerrorist, 10)] = 23,
            [(TeamCounterTerrorist, 11)] = 19, [(TeamCounterTerrorist, 12)] = 34,
            [(TeamCounterTerrorist, 14)] = 10, [(TeamCounterTerrorist, 15)] = 60, [(TeamCounterTerrorist, 16)] = 40,
            [(TeamCounterTerrorist, 17)] = 8,  [(TeamCounterTerrorist, 18)] = 9,
        };

    // The game's built-in default itemdef for the slot; slots not in the table (melee, Zeus, etc.) return false.
    public static bool TryGetImplicitDefault(uint team, uint slot, out uint itemDefinition) =>
        DefaultLoadout.TryGetValue((team, slot), out itemDefinition);
}
