namespace NfaLoader.Models;

// A loadout preset: a "numeric slot → itemdef" map for each team.
// Stored in settings.json (AppSettings.LoadoutPresets), edited on the loadout page and reused by one-click equip.
// Records stock weapons only (itemdef), no skins: equipping sends 0xF000000000000000|itemdef.
internal sealed class CsLoadoutPreset
{
    public string Name { get; set; } = "";

    // key = numeric loadout slot (1 melee / 2-7 pistols / 8-13 mid-tier / 14-19 rifles / 34 Zeus), value = itemdef.
    public Dictionary<uint, uint> T { get; set; } = new();

    public Dictionary<uint, uint> Ct { get; set; } = new();

    public CsLoadoutPreset Clone() => new()
    {
        Name = Name,
        T = new Dictionary<uint, uint>(T),
        Ct = new Dictionary<uint, uint>(Ct)
    };

    public Dictionary<uint, uint> SlotsFor(bool counterTerrorist) => counterTerrorist ? Ct : T;

    // The project's built-in default loadout (initial value for new users or when there are no settings). Slot 2 starting pistol, 3-6 other pistols, 8-12 mid-tier, 14-18 rifles.
    public static CsLoadoutPreset Default() => new()
    {
        T = new Dictionary<uint, uint>
        {
            [2] = 4, [3] = 2, [4] = 36, [5] = 1, [6] = 64,
            [8] = 17, [9] = 29, [10] = 23, [11] = 24, [12] = 26,
            [14] = 13, [15] = 7, [16] = 40, [17] = 9, [18] = 11
        },
        Ct = new Dictionary<uint, uint>
        {
            [2] = 61, [3] = 2, [4] = 63, [5] = 1, [6] = 64,
            [8] = 27, [9] = 23, [10] = 26, [11] = 33, [12] = 34,
            [14] = 10, [15] = 60, [16] = 40, [17] = 9, [18] = 38
        }
    };
}

// One-click equip result: slots requested, slots confirmed on read-back, and notes for the unconfirmed slots.
internal sealed record CsLoadoutApplyResult(int Requested, int Confirmed, IReadOnlyList<string> Failures)
{
    public bool IsSuccess => Requested > 0 && Confirmed == Requested && Failures.Count == 0;
}
