using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal enum NfaWarrantyState
{
    Open,
    Closed,
    NoneLeft,
    Replaced,
    Unknown,
}

/// <summary>What an account's order has left, as a short headline and an optional detail line.</summary>
internal sealed record NfaWarrantyStatus(NfaWarrantyState State, string Headline, string? Detail)
{
    /// <summary>Whether a replacement can still be asked for from the account's own card.</summary>
    public bool CanRequest => State is NfaWarrantyState.Open or NfaWarrantyState.Unknown;
}

/// <summary>
/// The nfa.pub replacement rules as the loader shows them: 3 replacements per order, within 6 hours of the order
/// being delivered. The server has the final word: it can extend the window while a product is out of stock, so
/// a closed window here is shown as a note, not used to block a request.
/// </summary>
internal static class NfaWarranty
{
    public const int ReplacementsPerOrder = 3;

    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>One line describing what is left on the account's order, or null when it did not come from nfa.pub.</summary>
    public static string? Describe(SteamAccountHistoryItem account, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(account.NfaOrder))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(account.NfaReplacedBy))
        {
            return Loc.Tf("Nfa_Warranty_ReplacedBy_Format", account.NfaReplacedBy);
        }

        // Checked before the count: a refusal for a closed window is also recorded as none left.
        var left = account.NfaOrderDeliveredAt is { } delivered ? delivered + Window - now : (TimeSpan?)null;
        if (left <= TimeSpan.Zero)
        {
            return Loc.T("Nfa_Warranty_Closed");
        }

        if (account.NfaReplacementsLeft is <= 0)
        {
            return Loc.T("Nfa_Warranty_NoneLeft");
        }

        // Accounts bought before delivery times were recorded have no start for the window.
        if (left is not { } open)
        {
            return Loc.T("Nfa_Warranty_Unknown");
        }

        return Loc.Tf(
            "Nfa_Warranty_Open_Format",
            FormatLeft(open),
            account.NfaReplacementsLeft ?? ReplacementsPerOrder);
    }

    /// <summary>The account's replacement status for a details panel, or null when it did not come from nfa.pub.</summary>
    public static NfaWarrantyStatus? GetStatus(SteamAccountHistoryItem account, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(account.NfaOrder))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(account.NfaReplacedBy))
        {
            return new(
                NfaWarrantyState.Replaced,
                Loc.T("Nfa_Status_Replaced"),
                Loc.Tf("Nfa_Status_Replaced_Detail_Format", account.NfaReplacedBy));
        }

        var end = account.NfaOrderDeliveredAt is { } delivered ? delivered + Window : (DateTimeOffset?)null;

        // Checked before the count: a refusal for a closed window is also recorded as none left, and saying all
        // replacements were used would be wrong.
        if (end is { } closedAt && closedAt <= now)
        {
            return new(
                NfaWarrantyState.Closed,
                Loc.T("Nfa_Status_Closed"),
                Loc.Tf("Nfa_Status_Closed_Detail_Format", FormatEnd(closedAt, now)));
        }

        if (account.NfaReplacementsLeft is <= 0)
        {
            return new(NfaWarrantyState.NoneLeft, Loc.T("Nfa_Status_NoneLeft"), null);
        }

        if (end is not { } openUntil)
        {
            return new(NfaWarrantyState.Unknown, Loc.T("Nfa_Status_Unknown"), Loc.T("Nfa_Status_Unknown_Detail"));
        }

        var left = openUntil - now;

        return new(
            NfaWarrantyState.Open,
            Loc.Tf("Nfa_Status_Open_Format", FormatLeft(left)),
            Loc.Tf(
                "Nfa_Status_Open_Detail_Format",
                FormatEnd(openUntil, now),
                account.NfaReplacementsLeft ?? ReplacementsPerOrder,
                ReplacementsPerOrder));
    }

    private static string FormatLeft(TimeSpan left) =>
        left.TotalHours >= 1
            ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : $"{Math.Max(1, left.Minutes)}m";

    // Just the time when it falls on the same day, so the common case stays short.
    private static string FormatEnd(DateTimeOffset end, DateTimeOffset now)
    {
        var local = end.LocalDateTime;
        return local.Date == now.LocalDateTime.Date
            ? local.ToString("HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
    }
}
