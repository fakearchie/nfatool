using NfaLoader.Localization;

namespace NfaLoader.Models;

internal enum CsWeaponCategory
{
    Pistol,  // Pistol, loadout slot 2-7 (secondary0-5)
    Mid,     // Mid-tier: SMG/shotgun/machine gun, loadout slot 8-13 (smg0-5)
    Rifle,   // Rifle/sniper, loadout slot 14-19 (rifle0-5)
    Taser    // Zeus x27, loadout slot 34 (equipment2)
    // Knives are left out by design (no effect on quick sign-in).
}

// CS2 buy menu groups (the loadout page lays out its columns by these). Starting pistol = secondary0(slot2), other pistols = secondary1-5(slot3-7).
internal enum CsLoadoutGroup
{
    StarterPistol,  // Starting pistol: slot 2. T only has Glock; CT can pick P2000 / USP-S.
    OtherPistol,    // Other pistols: slot 3-7.
    Mid,            // Mid-tier: slot 8-13.
    Rifle,          // Rifle/sniper: slot 14-19.
    Zeus            // Zeus x27: slot 34.
}

// Static definition of one equippable weapon. Def=CS2 item definition index; T/Ct=which side can use the weapon.
// IconFile maps to Assets/weapons/<IconFile>.svg, the CS2 in-game weapon icons.
// Data checked both ways against a real account's SO cache and Valve's items_game.txt.
internal sealed record CsWeapon(
    uint Def,
    string ClassName,
    string DisplayName,
    CsWeaponCategory Category,
    bool T,
    bool Ct,
    string IconFile,
    bool Starter = false)  // Starter=starting pistol (flexible_loadout_slot=secondary0): Glock / P2000 / USP-S.
{
    public string IconUri => $"ms-appx:///Assets/weapons/{IconFile}.svg";

    // Display name goes through i18n (key Weapon_<def>); DisplayName is only a readable note/fallback inside the catalog.
    public string LocalizedName => Loc.T($"Weapon_{Def}");

    public bool UsableBy(bool counterTerrorist) => counterTerrorist ? Ct : T;
}

internal static class CsWeaponCatalog
{
    // loadout numeric slot ranges (confirmed by testing: 5 positions per category, named slots 0-4; slots 7/13/19 do not exist).
    // Pistols secondary0-4=2-6, mid-tier smg0-4=8-12, rifles rifle0-4=14-18.
    public const uint StarterPistolSlot = 2;   // secondary0 (starting pistol)
    public const uint TaserSlot = 34;
    public static readonly uint[] OtherPistolSlots = [3, 4, 5, 6];      // secondary1-4
    public static readonly uint[] PistolSlots = [2, 3, 4, 5, 6];
    public static readonly uint[] MidSlots = [8, 9, 10, 11, 12];
    public static readonly uint[] RifleSlots = [14, 15, 16, 17, 18];

    public static readonly IReadOnlyList<CsWeapon> All =
    [
        // Knives are left out by design (no effect on quick sign-in), so they are not in the catalog.

        // ---- Pistols secondary / slot 2-7 ----
        new(1, "weapon_deagle", "Desert Eagle", CsWeaponCategory.Pistol, T: true, Ct: true, "deagle"),
        new(2, "weapon_elite", "Dual Berettas", CsWeaponCategory.Pistol, T: true, Ct: true, "elite"),
        new(3, "weapon_fiveseven", "Five-SeveN", CsWeaponCategory.Pistol, T: false, Ct: true, "fiveseven"),
        new(4, "weapon_glock", "Glock-18", CsWeaponCategory.Pistol, T: true, Ct: false, "glock", Starter: true),
        new(30, "weapon_tec9", "Tec-9", CsWeaponCategory.Pistol, T: true, Ct: false, "tec9"),
        new(32, "weapon_hkp2000", "P2000", CsWeaponCategory.Pistol, T: false, Ct: true, "hkp2000", Starter: true),
        new(36, "weapon_p250", "P250", CsWeaponCategory.Pistol, T: true, Ct: true, "p250"),
        new(61, "weapon_usp_silencer", "USP-S", CsWeaponCategory.Pistol, T: false, Ct: true, "usp_silencer", Starter: true),
        new(63, "weapon_cz75a", "CZ75-Auto", CsWeaponCategory.Pistol, T: true, Ct: true, "cz75a"),
        new(64, "weapon_revolver", "R8 Revolver", CsWeaponCategory.Pistol, T: true, Ct: true, "revolver"),

        // ---- Mid-tier: SMG / shotgun / machine gun, slot 8-13 ----
        new(17, "weapon_mac10", "MAC-10", CsWeaponCategory.Mid, T: true, Ct: false, "mac10"),
        new(19, "weapon_p90", "P90", CsWeaponCategory.Mid, T: true, Ct: true, "p90"),
        new(23, "weapon_mp5sd", "MP5-SD", CsWeaponCategory.Mid, T: true, Ct: true, "mp5sd"),
        new(24, "weapon_ump45", "UMP-45", CsWeaponCategory.Mid, T: true, Ct: true, "ump45"),
        new(25, "weapon_xm1014", "XM1014", CsWeaponCategory.Mid, T: true, Ct: true, "xm1014"),
        new(26, "weapon_bizon", "PP-Bizon", CsWeaponCategory.Mid, T: true, Ct: true, "bizon"),
        new(27, "weapon_mag7", "MAG-7", CsWeaponCategory.Mid, T: false, Ct: true, "mag7"),
        new(28, "weapon_negev", "Negev", CsWeaponCategory.Mid, T: true, Ct: true, "negev"),
        new(29, "weapon_sawedoff", "Sawed-Off", CsWeaponCategory.Mid, T: true, Ct: false, "sawedoff"),
        new(33, "weapon_mp7", "MP7", CsWeaponCategory.Mid, T: true, Ct: true, "mp7"),
        new(34, "weapon_mp9", "MP9", CsWeaponCategory.Mid, T: false, Ct: true, "mp9"),
        new(35, "weapon_nova", "Nova", CsWeaponCategory.Mid, T: true, Ct: true, "nova"),
        new(14, "weapon_m249", "M249", CsWeaponCategory.Mid, T: true, Ct: true, "m249"),

        // ---- Rifles / snipers, slot 14-19 ----
        new(7, "weapon_ak47", "AK-47", CsWeaponCategory.Rifle, T: true, Ct: false, "ak47"),
        new(8, "weapon_aug", "AUG", CsWeaponCategory.Rifle, T: false, Ct: true, "aug"),
        new(9, "weapon_awp", "AWP", CsWeaponCategory.Rifle, T: true, Ct: true, "awp"),
        new(10, "weapon_famas", "FAMAS", CsWeaponCategory.Rifle, T: false, Ct: true, "famas"),
        new(11, "weapon_g3sg1", "G3SG1", CsWeaponCategory.Rifle, T: true, Ct: false, "g3sg1"),
        new(13, "weapon_galilar", "Galil AR", CsWeaponCategory.Rifle, T: true, Ct: false, "galilar"),
        new(16, "weapon_m4a1", "M4A4", CsWeaponCategory.Rifle, T: false, Ct: true, "m4a1"),
        new(38, "weapon_scar20", "SCAR-20", CsWeaponCategory.Rifle, T: false, Ct: true, "scar20"),
        new(39, "weapon_sg556", "SG 553", CsWeaponCategory.Rifle, T: true, Ct: false, "sg556"),
        new(40, "weapon_ssg08", "SSG 08", CsWeaponCategory.Rifle, T: true, Ct: true, "ssg08"),
        new(60, "weapon_m4a1_silencer", "M4A1-S", CsWeaponCategory.Rifle, T: false, Ct: true, "m4a1_silencer"),

        // Zeus x27 is left out by design (everyone equips it), so it is not in the catalog.
    ];

    private static readonly Dictionary<uint, CsWeapon> ByDefMap = All.ToDictionary(w => w.Def);

    public static CsWeapon? ByDef(uint def) => ByDefMap.GetValueOrDefault(def);

    public static IReadOnlyList<uint> SlotsFor(CsWeaponCategory category) => category switch
    {
        CsWeaponCategory.Pistol => PistolSlots,
        CsWeaponCategory.Mid => MidSlots,
        CsWeaponCategory.Rifle => RifleSlots,
        CsWeaponCategory.Taser => [TaserSlot],
        _ => []
    };

    public static CsWeaponCategory CategoryForSlot(uint slot) => slot switch
    {
        >= 8 and <= 12 => CsWeaponCategory.Mid,
        >= 14 and <= 18 => CsWeaponCategory.Rifle,
        TaserSlot => CsWeaponCategory.Taser,
        _ => CsWeaponCategory.Pistol
    };

    // The loadout page shows categories in this order.
    public static readonly CsWeaponCategory[] EditorCategories =
        [CsWeaponCategory.Pistol, CsWeaponCategory.Mid, CsWeaponCategory.Rifle, CsWeaponCategory.Taser];

    // Weapons a side can pick in a category (for the candidate list on the loadout page).
    public static IEnumerable<CsWeapon> ForTeamCategory(bool counterTerrorist, CsWeaponCategory category) =>
        All.Where(w => w.Category == category && w.UsableBy(counterTerrorist));

    // ---- Buy menu groups (for the drag-and-drop loadout page) ----

    public static readonly CsLoadoutGroup[] EditorGroups =
        [CsLoadoutGroup.StarterPistol, CsLoadoutGroup.OtherPistol, CsLoadoutGroup.Mid, CsLoadoutGroup.Rifle];

    public static CsLoadoutGroup GroupOf(CsWeapon weapon) => weapon.Category switch
    {
        CsWeaponCategory.Pistol => weapon.Starter ? CsLoadoutGroup.StarterPistol : CsLoadoutGroup.OtherPistol,
        CsWeaponCategory.Mid => CsLoadoutGroup.Mid,
        CsWeaponCategory.Rifle => CsLoadoutGroup.Rifle,
        CsWeaponCategory.Taser => CsLoadoutGroup.Zeus,
        _ => CsLoadoutGroup.OtherPistol
    };

    public static IReadOnlyList<uint> SlotsForGroup(CsLoadoutGroup group) => group switch
    {
        CsLoadoutGroup.StarterPistol => [StarterPistolSlot],
        CsLoadoutGroup.OtherPistol => OtherPistolSlots,
        CsLoadoutGroup.Mid => MidSlots,
        CsLoadoutGroup.Rifle => RifleSlots,
        CsLoadoutGroup.Zeus => [TaserSlot],
        _ => []
    };

    public static CsLoadoutGroup GroupForSlot(uint slot) => slot switch
    {
        StarterPistolSlot => CsLoadoutGroup.StarterPistol,
        >= 3 and <= 6 => CsLoadoutGroup.OtherPistol,
        >= 8 and <= 12 => CsLoadoutGroup.Mid,
        >= 14 and <= 18 => CsLoadoutGroup.Rifle,
        TaserSlot => CsLoadoutGroup.Zeus,
        _ => CsLoadoutGroup.OtherPistol
    };

    public static string GroupName(CsLoadoutGroup group) => group switch
    {
        CsLoadoutGroup.StarterPistol => "Starting Pistol",
        CsLoadoutGroup.OtherPistol => "Other Pistols",
        CsLoadoutGroup.Mid => "Mid-Tier",
        CsLoadoutGroup.Rifle => "Rifles / Snipers",
        CsLoadoutGroup.Zeus => "Zeus x27",
        _ => ""
    };

    // Weapons a side can pick in a buy menu group (candidate list on the loadout page).
    public static IEnumerable<CsWeapon> ForTeamGroup(bool counterTerrorist, CsLoadoutGroup group) =>
        All.Where(w => GroupOf(w) == group && w.UsableBy(counterTerrorist));
}
