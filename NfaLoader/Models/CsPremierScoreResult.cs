using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Models;

// The three cooldown fields (PenaltySeconds/PenaltyReason/VacBanned) come from the GC's MatchmakingGC2ClientHello(9110);
// null means 9110 was not received (cooldown state unknown), which is kept strictly apart from "confirmed no cooldown" (0).
public sealed record CsPremierScoreResult(
    string SteamId,
    uint AccountId,
    CsRankingInfo? PremierRanking,
    IReadOnlyList<CsRankingInfo> Rankings,
    uint? PenaltySeconds,
    uint? PenaltyReason,
    int? VacBanned,
    int? PlayerLevel,
    bool InMatch)
{
    public bool HasPremierScore => PremierRanking is not null && PremierRanking.RankId > 0;

    public bool HasCooldownData => PenaltySeconds.HasValue;

    // Capped at int.MaxValue: anything above is a "negative seconds remaining" value read as a huge unsigned number (the cooldown has actually expired), so it does not count as a cooldown.
    public bool HasCooldown => PenaltySeconds is > 0 and <= int.MaxValue;

    public bool IsGcVacBanned => VacBanned is not (null or 0);

    /// <summary>For writing to history: unknown stays null and must not collapse into "not flagged".</summary>
    public bool? GcVacBannedOrUnknown => VacBanned is null ? null : VacBanned != 0;

    public string DisplayText => HasPremierScore
        ? Loc.Tf("Account_Score_WithWins_Format", PremierRanking!.RankId, PremierRanking.Wins)
        : Loc.T("Cs_Premier_NoScore");

    public string CooldownText => FormatHelper.FormatCooldownText(PenaltySeconds, PenaltyReason, Loc.T("Cs_Unknown_GcNoResponse"));

    public string GcVacText => FormatHelper.FormatGcVacText(VacBanned, Loc.T("Cs_Unknown"));

    public string CooldownStatusText =>
        FormatHelper.FormatCooldownStatusText(PenaltySeconds, PenaltyReason, VacBanned, Loc.T("Cs_Unknown_GcNoResponse"), Loc.T("Cs_Unknown"));

    public string PlayerLevelText => FormatHelper.FormatPlayerLevelText(PlayerLevel, Loc.T("Cs_PlayerLevel_NotRead"));

    public string StatusText
    {
        get
        {
            var restrictions = new List<string>();
            if (IsGcVacBanned)
            {
                restrictions.Add(Loc.T("Cs_Premier_Restriction_GcVac"));
            }

            if (HasCooldown)
            {
                var kind = PenaltySeconds > 365U * 86400U
                    ? Loc.T("Cs_Premier_Cooldown_LongBan")
                    : Loc.T("Cs_Premier_Cooldown_Normal");
                restrictions.Add(Loc.Tf("Cs_Premier_Restriction_Format", kind, CooldownText));
            }

            if (restrictions.Count > 0)
            {
                return string.Join(Loc.T("Cs_Premier_Restriction_Separator"), restrictions);
            }

            return HasCooldownData
                ? Loc.T("Cs_Premier_NoRestrictions")
                : Loc.T("Cs_Premier_CooldownUnknown");
        }
    }
}

public sealed record CsRankingInfo(
    uint RankTypeId,
    uint RankId,
    uint Wins,
    uint? MapId);
