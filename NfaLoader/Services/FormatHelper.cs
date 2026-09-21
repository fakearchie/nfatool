using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal static class FormatHelper
{
    /// <summary>Fixes a common case error in the token header from pasting (EyAi → eyAi).</summary>
    public static string NormalizeToken(string token)
    {
        token = token.Replace(
            "EyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0",
            "eyAidHlwIjogIkpXVCIsICJhbGciOiAiRWREU0EiIH0",
            StringComparison.Ordinal);

        // A whole account line pasted or delivered as "steamid----token----key:value...": keep only the token.
        if (token.Contains("----", StringComparison.Ordinal))
        {
            token = token
                .Split("----", StringSplitOptions.TrimEntries)
                .FirstOrDefault(LooksLikeToken) ?? token;
        }

        return token;
    }

    /// <summary>A JWT shape: starts with the Steam token header prefix and has three dot-separated parts.</summary>
    public static bool LooksLikeToken(string value) =>
        value.StartsWith("eyAi", StringComparison.Ordinal) && value.Count(c => c == '.') == 2;

    public static string FormatRemaining(TimeSpan remaining)
    {
        return Loc.Tf("Format_Remaining_Format", Math.Floor(remaining.TotalDays), remaining.Hours, remaining.Minutes);
    }

    public static string FormatDateTime(DateTimeOffset value)
    {
        return value == default
            ? Loc.T("Format_DateTime_Unknown")
            : value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>Player level needed before Premier matchmaking unlocks.</summary>
    public const int MinimumPremierLevel = 10;

    public static string FormatDuration(uint seconds)
    {
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var parts = new List<string>();

        if (days > 0)
        {
            parts.Add(Loc.Tf("Format_Duration_Days_Format", days));
        }

        if (hours > 0)
        {
            parts.Add(Loc.Tf("Format_Duration_Hours_Format", hours));
        }

        if (minutes > 0)
        {
            parts.Add(Loc.Tf("Format_Duration_Minutes_Format", minutes));
        }

        return parts.Count > 0 ? string.Join("", parts) : Loc.Tf("Format_Duration_Seconds_Format", seconds);
    }

    // Known GC cooldown reason codes map to readable text; unknown codes stay "Reason N";
    // a null reason (GC sent no code) returns an empty string, so FormatCooldownText drops the reason in brackets,
    // which keeps the old history look of "no code, no reason" (instead of a misleading "Reason 0").
    public static string DescribePenaltyReason(uint? reason) => reason switch
    {
        null => "",
        7 => Loc.T("Format_Penalty_Abandon"),
        22 => "vaclive",
        _ => Loc.Tf("Format_Penalty_Reason_Format", reason.Value)
    };

    /// <param name="seconds">Seconds left on the cooldown; null means GC did not send it (unknown).</param>
    /// <param name="unknownText">Text shown when seconds is null (a parameter because the two callers word it differently).</param>
    public static string FormatCooldownText(uint? seconds, uint? reason, string unknownText)
    {
        if (seconds is null)
        {
            return unknownText;
        }

        // seconds > int.MaxValue means a "negative seconds left" value was read as unsigned into a huge number (the cooldown has actually expired).
        // Catch it here so bad values saved earlier in history don't still show as "49707 days...".
        if (seconds == 0 || seconds > int.MaxValue)
        {
            return Loc.T("Format_Cooldown_None");
        }

        var duration = FormatDuration(seconds.Value);
        var description = DescribePenaltyReason(reason);
        return description.Length > 0 ? Loc.Tf("Format_Cooldown_WithReason_Format", duration, description) : duration;
    }

    /// <summary>Countdown text down to the second (days/hours/minutes/seconds) for the live cooldown timer; same meaning as FormatCooldownText.</summary>
    public static string FormatCooldownCountdownText(uint? seconds, uint? reason, string unknownText)
    {
        if (seconds is null)
        {
            return unknownText;
        }

        if (seconds == 0 || seconds > int.MaxValue)
        {
            return Loc.T("Format_Cooldown_None");
        }

        var countdown = FormatCountdown(seconds.Value);
        var description = DescribePenaltyReason(reason);
        return description.Length > 0 ? Loc.Tf("Format_Cooldown_WithReason_Format", countdown, description) : countdown;
    }

    /// <summary>Renders seconds as a countdown string (leading zero units are dropped, seconds always show).</summary>
    public static string FormatCountdown(uint seconds)
    {
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var secs = seconds % 60;

        if (days > 0)
        {
            return Loc.Tf("Format_Countdown_DHMS_Format", days, hours, minutes, secs);
        }

        if (hours > 0)
        {
            return Loc.Tf("Format_Countdown_HMS_Format", hours, minutes, secs);
        }

        return minutes > 0
            ? Loc.Tf("Format_Countdown_MS_Format", minutes, secs)
            : Loc.Tf("Format_Countdown_S_Format", secs);
    }

    /// <param name="vacBanned">GC VAC flag: null unknown / 0 none / anything else flagged.</param>
    /// <param name="unknownText">Text shown when vacBanned is null.</param>
    public static string FormatGcVacText(int? vacBanned, string unknownText) => vacBanned switch
    {
        null => unknownText,
        0 => Loc.T("Format_GcVac_None"),
        _ => Loc.T("Format_GcVac_Flagged")
    };

    public static string FormatCooldownStatusText(
        uint? seconds,
        uint? reason,
        int? vacBanned,
        string cooldownUnknownText,
        string vacUnknownText) =>
        Loc.Tf(
            "Format_CooldownStatus_Format",
            FormatCooldownText(seconds, reason, cooldownUnknownText),
            FormatGcVacText(vacBanned, vacUnknownText));

    /// <param name="level">CS player level; null returns <paramref name="unknownText"/>.</param>
    public static string FormatPlayerLevelText(int? level, string unknownText)
    {
        if (!level.HasValue)
        {
            return unknownText;
        }

        var status = level.Value >= MinimumPremierLevel
            ? Loc.T("Format_PlayerLevel_Eligible")
            : Loc.Tf("Format_PlayerLevel_Below_Format", MinimumPremierLevel);

        return Loc.Tf("Format_PlayerLevel_Format", level.Value, status);
    }

    public static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return Loc.T("Format_FileSize_Unknown");
        }

        return bytes.Value >= 1024 * 1024
            ? $"{bytes.Value / 1024d / 1024d:F2} MB"
            : $"{bytes.Value / 1024d:F0} KB";
    }

    public static Brush GetStatusBrush(InfoBarSeverity severity)
    {
        var resourceKey = severity switch
        {
            InfoBarSeverity.Success => "SystemFillColorSuccessBrush",
            InfoBarSeverity.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush"
        };

        // The theme is set on the window's root element (MainWindow.ApplyTheme), so a direct Application.Resources lookup always returns the
        // light variant from startup and status text turns black on a dark background; so look it up in the merged theme dictionaries by the root's ActualTheme.
        if (TryFindThemeBrush(resourceKey) is { } themedBrush)
        {
            return themedBrush;
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out var resource) &&
            resource is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>Finds a brush in the XamlControlsResources theme dictionaries for the current actual theme; returns null if not found.</summary>
    private static Brush? TryFindThemeBrush(string key)
    {
        var dark = App.ActualTheme == ElementTheme.Dark;
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            // WinUI theme dictionary keys are Default(=Light)/Light/Dark, so light tries both keys.
            var themeKeys = dark ? new[] { "Dark" } : new[] { "Light", "Default" };
            foreach (var themeKey in themeKeys)
            {
                if (merged.ThemeDictionaries.TryGetValue(themeKey, out var dictObj) &&
                    dictObj is ResourceDictionary themeDict &&
                    themeDict.TryGetValue(key, out var value) &&
                    value is Brush brush)
                {
                    return brush;
                }
            }
        }

        return null;
    }
}
