using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;

namespace NfaLoader.Pages;

/// <summary>Asking nfa.pub to replace a faulty account. Shared by the History page and the sign-in refusal offer.</summary>
internal static class NfaReplaceFlow
{
    // Swaps sent this session whose answer never arrived. They may have gone through, and nfa.pub swaps a Steam ID only
    // once, so they are not offered again automatically. History's Replace button still works for them. The doubt is
    // also saved on the account; this set covers a save that failed.
    private static readonly HashSet<string> SwapInDoubt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a replacement request is waiting on nfa.pub, so closing the loader now would leave it unfinished.</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>
    /// Whether to offer a replacement when Steam refuses the account: it came from nfa.pub, it has not been swapped,
    /// refused for good or had a swap go unanswered, and its order is not known to be out of replacements or past its window.
    /// </summary>
    public static bool CanOffer(SteamAccountHistoryItem account) =>
        !string.IsNullOrWhiteSpace(account.NfaOrder) &&
        string.IsNullOrWhiteSpace(account.NfaReplacedBy) &&
        string.IsNullOrWhiteSpace(account.NfaReplaceRefusal) &&
        account.NfaReplaceUnconfirmedAt is null &&
        !SwapInDoubt.Contains(account.SteamId) &&
        account.NfaReplacementsLeft is null or > 0;

    /// <summary>
    /// Tells the user about a replacement that was sent but never had its result saved, most likely because the loader
    /// was closed while nfa.pub was still working. Called once at startup. Never throws.
    /// </summary>
    public static async Task ShowUnfinishedAsync(XamlRoot? xamlRoot)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            if (settings.NfaReplacingSteamId is not { Length: > 0 } steamId)
            {
                return;
            }

            var order = settings.NfaReplacingOrder ?? "";
            var startedAt = settings.NfaReplacingStartedAt ?? DateTimeOffset.Now;

            // The answer reached History but the record was not cleared, so there is nothing to collect.
            if (AppState.FindHistoryAccount(steamId) is { } account &&
                (!string.IsNullOrWhiteSpace(account.NfaReplacedBy) || !string.IsNullOrWhiteSpace(account.NfaReplaceRefusal)))
            {
                ClearInFlight();
                return;
            }

            AppLog.Warn($"Replacement of {steamId} on order {order} sent {startedAt:u} was never finished");
            TryRecordOutcome(steamId, startedAt, null);

            var message = Loc.Tf(
                "Nfa_Replace_Unfinished_Message_Format", steamId, order, FormatHelper.FormatDateTime(startedAt));
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = Loc.T("Nfa_Replace_Unfinished_Title"),
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    CloseButtonText = Loc.T("Common_Confirm"),
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // Another dialog was already open, so the status bar has to carry it.
                AppLog.Warn($"Could not show the unfinished replacement notice: {ex.Message}");
                AppState.ShowStatus(message, InfoBarSeverity.Warning);
            }

            // Told once. The account keeps the doubt, so it is still not offered again.
            ClearInFlight();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not check for an unfinished replacement.", ex);
        }
    }

    /// <summary>Offers a replacement after Steam refused the account's token. Never throws.</summary>
    public static async Task OfferAsync(XamlRoot? xamlRoot, SteamAccountHistoryItem account)
    {
        if (xamlRoot is null || !CanOffer(account))
        {
            return;
        }

        try
        {
            var message = Loc.Tf("Nfa_Replace_Offer_Message_Format", account.SteamId);
            if (NfaWarranty.Describe(account, DateTimeOffset.Now) is { } warranty)
            {
                message += "\n\n" + warranty;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Loc.T("Nfa_Replace_Offer_Title"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.T("Nfa_Replace_Offer_Yes"),
                CloseButtonText = Loc.T("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await RunAsync(xamlRoot, account, Loc.T("Nfa_Replace_Offer_Reason"));
            }
        }
        catch (Exception ex)
        {
            // Most likely another dialog was already open. The account stays in History, so Replace is still one click away.
            AppLog.Error("Could not show the replacement offer.", ex);
        }
    }

    /// <summary>
    /// Requests the replacement straight away when the order is known, showing a window that waits on nfa.pub and then
    /// shows the result. Only an account the loader did not buy asks for its order first.
    /// Returns the replacement's Steam ID, or null if nothing was swapped.
    /// </summary>
    public static async Task<string?> RunAsync(XamlRoot xamlRoot, SteamAccountHistoryItem account, string? knownReason = null)
    {
        if (!string.IsNullOrWhiteSpace(account.NfaReplacedBy))
        {
            AppState.ShowStatus(Loc.Tf("Nfa_Replace_AlreadyReplaced_Format", account.NfaReplacedBy), InfoBarSeverity.Warning);
            return null;
        }

        // nfa.pub would give the same answer again, so it is shown without asking.
        if (!string.IsNullOrWhiteSpace(account.NfaReplaceRefusal))
        {
            AppState.ShowStatus(NfaPubClient.DescribeError(account.NfaReplaceRefusal), InfoBarSeverity.Warning);
            return null;
        }

        var apiKey = AppState.GetNfaApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AppState.ShowStatus(Loc.T("Nfa_Warning_NoKey_Message"), InfoBarSeverity.Warning);
            return null;
        }

        var order = account.NfaOrder?.Trim() ?? "";
        var reason = knownReason;

        if (order.Length == 0)
        {
            var form = await AskForDetailsAsync(xamlRoot, account, knownReason);
            if (form is null)
            {
                return null;
            }

            (order, reason) = form.Value;
            if (order.Length == 0)
            {
                AppState.ShowStatus(Loc.T("Nfa_Replace_Error_OrderRequired"), InfoBarSeverity.Warning);
                return null;
            }
        }

        // Starting on top of another task would let each one end the other's busy state and re-enable the page
        // mid-request. Checked after the form, because the user can take any time over it.
        if (AppState.IsBusy)
        {
            AppState.ShowStatus(Loc.T("Nfa_Replace_Busy"), InfoBarSeverity.Warning);
            return null;
        }

        // Saved before the request goes out. A swap cannot be replayed, so if the loader closes before the result is
        // saved, the next start can still tell the user where the new account went.
        var startedAt = DateTimeOffset.Now;
        if (!SaveInFlight(account.SteamId, order, startedAt))
        {
            AppState.ShowStatus(Loc.T("Nfa_Replace_Error_CannotSave"), InfoBarSeverity.Error);
            return null;
        }

        var window = new NfaReplaceDialog(xamlRoot, account.SteamId);
        NfaReplacement replacement;
        AppState.SetBusy(true);
        IsRunning = true;
        window.Show();
        try
        {
            replacement = await AppState.NfaClient.ReplaceAsync(apiKey, order, account.SteamId, reason);
        }
        catch (NfaApiException ex) when (ex.Code is "TIMEOUT" or "NETWORK" or "UNREADABLE" or "UNCONFIRMED" or "E1000")
        {
            // The request reached nfa.pub but no usable answer came back, so the swap may have happened. A swap has no key
            // to replay and a Steam ID is swapped only once, so asking again would be refused: say so instead.
            AppLog.Warn($"Replacement of {account.SteamId} on order {order} unconfirmed: {ex.Code}");
            SwapInDoubt.Add(account.SteamId);
            TryRecordOutcome(account.SteamId, startedAt, null);
            Report(window, InfoBarSeverity.Warning, Loc.T("Nfa_Replace_Unconfirmed_Title"), Loc.T("Nfa_Replace_Unconfirmed"));
            return null;
        }
        catch (NfaApiException ex)
        {
            AppLog.Warn($"Replacement of {account.SteamId} on order {order} refused: {ex.Code}");

            // A refusal swapped nothing, so there is no new account to collect.
            ClearInFlight();

            // A closed window or a used-up order cannot reopen, so it is recorded to stop offering it.
            if (ex.Code is "E1605" or "E1608")
            {
                TryRecordNoneLeft(account, order);
            }
            // These hold for good on this Steam ID only. The rest of the order keeps its cover, so the count is left alone.
            else if (ex.Code is "E1607" or "E1616" or "E1617" or "E1618")
            {
                TryRecordOutcome(account.SteamId, null, ex.Code);
            }

            // A check that finds the account working is good news, not a failure, and a replacement already running on
            // the order only needs a retry.
            if (ex.Code == "E1610")
            {
                Report(window, InfoBarSeverity.Informational, Loc.T("Nfa_Replace_StillWorks_Title"), ex.Message);
            }
            else if (ex.Code == "E1621")
            {
                Report(window, InfoBarSeverity.Informational, Loc.T("Nfa_Replace_OrderBusy_Title"), ex.Message);
            }
            else
            {
                Report(window, InfoBarSeverity.Error, Loc.T("Nfa_Replace_Failed_Title"), ex.Message);
            }

            return null;
        }
        catch (Exception ex)
        {
            // What happened at nfa.pub is unknown, so the in-flight record stays for the next start to report.
            AppLog.Error($"Replacement of {account.SteamId} on order {order} failed", ex);
            Report(window, InfoBarSeverity.Error, Loc.T("Nfa_Replace_Failed_Title"), ex.Message);
            return null;
        }
        finally
        {
            IsRunning = false;
            AppState.SetBusy(false);
        }

        try
        {
            // The window belongs to the order, so the replacement inherits the original delivery time.
            AppState.AccountHistoryService.SaveNfaAccount(
                replacement.SteamId,
                replacement.Token,
                order,
                account.NfaOrderDeliveredAt,
                replacement.Remaining,
                string.IsNullOrWhiteSpace(replacement.ReplacedSteamId) ? account.SteamId : replacement.ReplacedSteamId);
            AppState.ReloadHistory(replacement.SteamId);
        }
        catch (Exception ex)
        {
            // The replacement is already used up at nfa.pub, so it must not be lost with the failed save. Only one
            // dialog can be open, and the rescue may need one to show the account line.
            AppLog.Error($"Replacement {replacement.SteamId} on order {order} arrived but could not be saved", ex);
            SwapInDoubt.Add(account.SteamId);
            TryRecordOutcome(account.SteamId, startedAt, null);
            await window.CloseAsync();
            await NfaAccountRescue.PreserveAsync(
                xamlRoot, "Replace", replacement.SteamId, replacement.Token, $"replacement on order {order}");
            return replacement.SteamId;
        }

        // Cleared only once the new account is safely in History.
        ClearInFlight();

        if (!window.TryShowResult(
                InfoBarSeverity.Success,
                Loc.T("Nfa_Replace_Done_Title"),
                Loc.T("Nfa_Replace_Done_Message"),
                NfaReplaceDialog.NewAccount(replacement.SteamId, replacement.Remaining)))
        {
            AppState.ShowStatus(
                Loc.Tf("Nfa_Replace_Done_Format", replacement.SteamId, replacement.Remaining),
                InfoBarSeverity.Success);
        }

        return replacement.SteamId;
    }

    // In the window when it is still open, otherwise in the status bar.
    private static void Report(NfaReplaceDialog window, InfoBarSeverity severity, string title, string message)
    {
        if (!window.TryShowResult(severity, title, message))
        {
            AppState.ShowStatus(message, severity);
        }
    }

    private static async Task<(string Order, string? Reason)?> AskForDetailsAsync(
        XamlRoot xamlRoot,
        SteamAccountHistoryItem account,
        string? suggestedReason)
    {
        var orderBox = new TextBox
        {
            Header = Loc.T("Nfa_Replace_Field_Order"),
            PlaceholderText = Loc.T("Nfa_Replace_Field_Order_Placeholder"),
            Text = account.NfaOrder ?? "",
            IsSpellCheckEnabled = false,
        };
        var reasonBox = new TextBox
        {
            Header = Loc.T("Nfa_Replace_Field_Reason"),
            PlaceholderText = Loc.T("Nfa_Replace_Field_Reason_Placeholder"),
            Text = suggestedReason ?? "",
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tf("Nfa_Replace_Dialog_Message_Format", account.SteamId),
            TextWrapping = TextWrapping.Wrap,
        });

        if (NfaWarranty.Describe(account, DateTimeOffset.Now) is { } warranty)
        {
            panel.Children.Add(new TextBlock
            {
                Text = warranty,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        panel.Children.Add(orderBox);
        panel.Children.Add(reasonBox);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.T("Nfa_Replace_Dialog_Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Nfa_Replace_Button"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? (orderBox.Text.Trim(), string.IsNullOrWhiteSpace(reasonBox.Text) ? null : reasonBox.Text.Trim())
            : null;
    }

    private static void TryRecordNoneLeft(SteamAccountHistoryItem account, string order)
    {
        try
        {
            AppState.AccountHistoryService.SaveNfaAccount(account.SteamId, account.EyaToken, order, null, 0);
            AppState.ReloadHistory(account.SteamId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not record that order {order} can no longer be replaced: {ex.Message}");
        }
    }

    // Saved on the account itself, so a restart still knows not to offer it.
    private static void TryRecordOutcome(string steamId, DateTimeOffset? unconfirmedAt, string? refusal)
    {
        try
        {
            AppState.AccountHistoryService.SaveNfaReplaceOutcome(steamId, unconfirmedAt, refusal);
            AppState.ReloadHistory(steamId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not record the replacement outcome for {steamId}: {ex.Message}");
        }
    }

    private static bool SaveInFlight(string steamId, string order, DateTimeOffset startedAt)
    {
        var settings = AppState.SettingsService.Load();
        settings.NfaReplacingSteamId = steamId;
        settings.NfaReplacingOrder = order;
        settings.NfaReplacingStartedAt = startedAt;
        return AppState.SettingsService.Save(settings);
    }

    private static void ClearInFlight()
    {
        var settings = AppState.SettingsService.Load();
        settings.NfaReplacingSteamId = null;
        settings.NfaReplacingOrder = null;
        settings.NfaReplacingStartedAt = null;
        if (!AppState.SettingsService.Save(settings))
        {
            AppLog.Warn("Could not clear the replacement record from settings.json.");
        }
    }
}
