using System.ComponentModel;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;
using Windows.System;

namespace NfaLoader.Pages;

public sealed partial class LoginPage : Page, INotifyPropertyChanged
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    // The account bought on this page, used when signing in from Buy mode.
    private static readonly TimeSpan PendingPurchaseMaxAge = TimeSpan.FromHours(1);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _warrantyTimer;
    private IReadOnlyList<NfaStockItem> _nfaStock = [];
    private decimal? _nfaBalance;
    private string? _inFlightPurchaseKey;

    // Whether saving the account to history failed: set by SaveLoginHistoryAsync, and the sign-in message uses it to pick Warning or Success severity
    // (after localization the history suffix text no longer starts with a fixed Chinese prefix, so this flag replaces the old string prefix check).
    private bool _lastLoginHistorySaveFailed;

    public LoginPage()
    {
        InitializeComponent();

        AppState.LoginPage = this;
        AppState.BusyChanged += OnBusyChanged;
        AppState.NfaApiKeyChanged += OnNfaApiKeyChanged;
        AppState.HistoryChanged += selectSteamId => _dispatcherQueue.TryEnqueue(() => RebuildBoughtAccounts(selectSteamId));
        RebuildBoughtAccounts(null);

        _warrantyTimer = _dispatcherQueue.CreateTimer();
        _warrantyTimer.Interval = TimeSpan.FromSeconds(30);
        _warrantyTimer.IsRepeating = true;
        _warrantyTimer.Tick += (_, _) =>
        {
            UpdateAccountInfoNfa();
            UpdatePendingPurchaseBar();
        };
        _warrantyTimer.Start();

        // An interrupted purchase is shown, never re-sent on its own. Finishing it is the user's choice.
        UpdatePendingPurchaseBar();
        Loc.LanguageChanged += OnLanguageChanged;

        // SelectorBarItem.IsSelected is unreliable while XAML is being parsed, so set the initial mode explicitly.
        ModeSelector.SelectedItem = ManualModeItem;
        ApplyModeVisibility();

        UpdateAccountInfoFromCurrentInputs();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML binding entry point: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private bool IsAutoMode => ModeSelector.SelectedItem == AutoModeItem;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Static x:Bind text is recomputed through Strings; for the imperative text in the account info panel on the right (user name/SteamID/expiry/availability/score/level/cooldown),
            // rerunning the two methods below switches placeholders already on screen, such as "Not filled/Not resolved/Not verified", to the new language.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            ApplyModeVisibility();
            UpdateAccountInfoFromCurrentInputs();
            OnBusyChanged(AppState.IsBusy);
        });
    }

    private static void ShowStatus(string message, InfoBarSeverity severity)
    {
        AppState.ShowStatus(message, severity);
    }

    private void OnBusyChanged(bool isBusy)
    {
        var enabled = !isBusy;
        ModeSelector.IsEnabled = enabled;
        EyaTokenBox.IsEnabled = enabled;
        AccountInfoReplaceButton.IsEnabled = enabled;
        NfaTypeBox.IsEnabled = enabled;
        NfaRefreshStockButton.IsEnabled = enabled;
        BoughtAccountBox.IsEnabled = enabled;
        BuyButton.IsEnabled = enabled && AppState.HasNfaApiKey && AppState.GetPendingPurchase() is null;
        NfaFinishPurchaseButton.IsEnabled = enabled;
        NfaDismissPurchaseButton.IsEnabled = enabled;
        PersonalizeButton.IsEnabled = enabled;
        ClearWorkshopButton.IsEnabled = enabled;
        ApplyLoadoutButton.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
        OneClickQueryButton.IsEnabled = enabled;

        // The cancel button does the opposite: it only shows while busy and stays enabled, so the user can stop a long task.
        CancelButton.Visibility = isBusy && AppState.CanCancelBusyOperation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus(Loc.T("Login_Status_Cancelling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    /// <summary>Loads an account from the History page into the Login page (keeps the old behaviour: switch to manual mode and fill in the credentials).</summary>
    public void LoadHistoryAccount(SteamAccountHistoryItem account)
    {
        ModeSelector.SelectedItem = ManualModeItem;
        _knownManualAccount = (account.AccountName, FormatHelper.NormalizeToken(account.EyaToken));
        EyaTokenBox.Text = account.EyaToken;
        UpdateAccountInfo(account.AccountName, account.EyaToken);
        ApplyAccountInfoProfile(account);

        ShowStatus(
            Loc.Tf("Login_Status_HistoryLoaded_Format", account.AccountName, FormatHelper.FormatDateTime(account.LastLoginAt)),
            InfoBarSeverity.Informational);
    }

    // One-click loadout: use the preset saved on the "Loadout" page + the current account's token to write the weapons to both teams. Replaces the old "Equip R8".
    private async void ApplyLoadoutButton_Click(object sender, RoutedEventArgs e)
    {
        var preset = AppState.SettingsService.Load().Loadout;
        if (preset.T.Count == 0 && preset.Ct.Count == 0)
        {
            ShowStatus(Loc.T("Login_Loadout_EmptyPreset"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_ApplyLoadout");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);

            var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
            var steamId = tokenInfo.SteamId
                ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdLoadout"));

            ShowStatus(Loc.T("Login_Status_ApplyingLoadout"), InfoBarSeverity.Informational);
            var result = await AppState.LoadoutService.ApplyPresetAsync(
                preset,
                eyaToken,
                steamId,
                cancellationToken);

            if (result.IsSuccess)
            {
                ShowStatus(Loc.Tf("Login_Status_LoadoutApplied_Format", result.Confirmed), InfoBarSeverity.Success);
            }
            else
            {
                var detail = string.Join(", ", result.Failures.Take(6));
                if (result.Failures.Count > 6)
                {
                    detail += "...";
                }

                ShowStatus(
                    Loc.Tf("Login_Status_LoadoutPartial_Format", result.Confirmed, result.Requested, detail),
                    InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoadoutCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // One-click personalize: set the current account's profile to the nickname + bio (real name / summary) + avatar saved on the "Personalize" page;
    // when "Clear name history" is checked, clear the name history at the end.
    private async void PersonalizeButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppState.SettingsService.Load();
        var personaName = settings.PersonaName;
        var avatarPath = AppState.SettingsService.PersonalizationAvatarPath;
        var hasName = !string.IsNullOrWhiteSpace(personaName);
        var hasRealName = !string.IsNullOrWhiteSpace(settings.ProfileRealName);
        var hasSummary = !string.IsNullOrWhiteSpace(settings.ProfileSummary);
        var hasAvatar = File.Exists(avatarPath);
        var clearAliases = settings.ClearAliasHistoryOnPersonalize;

        if (!hasName && !hasRealName && !hasSummary && !hasAvatar && !clearAliases)
        {
            ShowStatus(Loc.T("Login_Personalize_Empty"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_Personalize");
            UpdateAccountInfo(accountName, eyaToken);

            var result = await AppState.ProfileService.ApplyAsync(
                eyaToken,
                new SteamProfileApplyRequest(
                    hasName ? personaName : null,
                    hasRealName ? settings.ProfileRealName : null,
                    hasSummary ? settings.ProfileSummary : null,
                    hasAvatar ? avatarPath : null,
                    clearAliases),
                progress,
                cancellationToken);

            if (result.IsFullSuccess)
            {
                ShowStatus(Loc.T("Login_Status_Personalized"), InfoBarSeverity.Success);
            }

            // For items that applied, write the "known new values" straight back to the local record and refresh the UI, without fetching again:
            // the community profile endpoint has an edge cache, so fetching right after a rename can still return the old value and "roll back" the record.
            if (result.NameApplied || result.AvatarApplied)
            {
                var steamId = AppState.JwtTokenService.Inspect(eyaToken).SteamId;
                if (!string.IsNullOrWhiteSpace(steamId))
                {
                    await Task.Run(() => AppState.AccountHistoryService.UpdateProfileLocally(
                        steamId,
                        result.NameApplied ? personaName : null,
                        result.AvatarApplied ? avatarPath : null));
                    AppState.ReloadHistory();
                    if (string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyStoredAccountInfoProfile(steamId);
                    }
                }
            }

            if (!result.IsFullSuccess)
            {
                var errors = new List<string>();
                if (result.ProfileRequested && !result.ProfileApplied)
                {
                    errors.Add(result.ProfileError ?? Loc.T("Profile_Error_Unknown"));
                }

                if (result.AvatarRequested && !result.AvatarApplied)
                {
                    errors.Add(result.AvatarError ?? Loc.T("Profile_Error_Unknown"));
                }

                if (result.AliasClearRequested && !result.AliasesCleared)
                {
                    errors.Add(result.AliasClearError ?? Loc.T("Profile_Error_Unknown"));
                }

                ShowStatus(
                    Loc.Tf("Login_Status_PersonalizePartial_Format", string.Join("; ", errors)),
                    InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_PersonalizeCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void ClearWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_ClearingWorkshop"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            EnsureTokenValidForAction(eyaToken, "Login_Action_ClearWorkshop");
            UpdateAccountInfo(accountName, eyaToken);
            await UpdateAccountProfileAsync(accountName, eyaToken);

            // No EnsureTokenAcceptedBySteamAsync up front any more: ClearSubscriptionsAsync already does a full handshake inside,
            // and a refused token is handled below through SteamCmException.IsTokenFailure, which avoids a pointless second handshake.
            var count = await AppState.WorkshopService.ClearSubscriptionsAsync(
                eyaToken,
                progress,
                cancellationToken);

            ShowStatus(
                Loc.Tf("Login_Status_WorkshopCleared_Format", count),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_ClearWorkshopCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            // Token refused by Steam: mark availability red, the same as when the up-front check fails, then report the error.
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            ShowStatus($"{ex.Message}{Loc.T("Login_Error_CannotClearWorkshopSuffix")}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        // The first run detects and saves the Steam path; if it is no longer valid, detect again; only if both fail, ask the user to pick it. If the user cancels, stop signing in.
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_Processing"), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            ShowStatus(message, InfoBarSeverity.Informational));

        string? attemptedToken = null;
        SteamAccountHistoryItem? refusedNfaAccount = null;

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            attemptedToken = eyaToken;
            EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
            UpdateAccountInfo(accountName, eyaToken);
            // Pass the persona/avatar fetched here straight to SaveLoginAsync, so saving to history does not fetch them a second time.
            // triggerBackgroundRefresh:false: sign-in takes tens of seconds, and new values written by a fallback refresh now would be overwritten when sign-in ends
            // by this snapshot (old values) that SaveLoginAsync writes back, so the fallback refresh runs after SaveLoginHistoryAsync saves instead.
            var profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
            await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
            var result = await Task.Run(
                () => AppState.LoginService.Login(accountName, eyaToken, progress),
                cancellationToken);
            var historyStatus = await SaveLoginHistoryAsync(result, eyaToken, profile);
            ShowStatus(
                Loc.Tf("Login_Status_LoginStarted_Format", result.SteamId, FormatHelper.FormatRemaining(result.Remaining), historyStatus),
                _lastLoginHistorySaveFailed
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_LoginCancelled"), InfoBarSeverity.Informational);
        }
        catch (SteamTokenRefusedException ex)
        {
            AppLog.Warn($"Sign-in stopped, Steam refused the login token: {ex.Message}");
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            refusedNfaAccount = FindNfaAccountForToken(attemptedToken);
        }
        catch (Exception ex)
        {
            AppLog.Error("Sign-in failed.", ex);
            ShowStatus(Loc.Tf("Login_Error_LoginFailed_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }

        // After the busy state ends, because replacing sets it again.
        if (refusedNfaAccount is not null)
        {
            await NfaReplaceFlow.OfferAsync(XamlRoot, refusedNfaAccount);
        }
    }

    /// <summary>
    /// Lets the History page's "Quick login" sign in with a given account directly, using the same flow as "Sign in to Steam"
    /// (structure check -> online validation, skipped on network failure -> write config and start Steam -> write history).
    /// The caller handles busy state and showing status and errors; exceptions are thrown as is.
    /// </summary>
    public async Task<LoginResult> QuickLoginAsync(
        string accountName,
        string eyaToken,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        // Same as "Sign in to Steam": first make sure the Steam path is resolved (detect on first run/save/detect again when invalid/dialog).
        // If the user cancels the picker, that counts as cancelling sign-in, and the caller shows a neutral message for the OperationCanceledException.
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            throw new OperationCanceledException();
        }

        eyaToken = FormatHelper.NormalizeToken(eyaToken);
        EnsureTokenValidForAction(eyaToken, "Login_Action_Login");
        UpdateAccountInfo(accountName, eyaToken);
        // Pass the persona/avatar fetched here straight to SaveLoginAsync, so saving to history does not fetch them again.
        // triggerBackgroundRefresh:false for the same reason as "Sign in to Steam": the fallback refresh moves to after the save, so an old snapshot cannot overwrite new values.
        var profile = await UpdateAccountProfileAsync(accountName, eyaToken, triggerBackgroundRefresh: false);
        await EnsureTokenAcceptedBySteamAsync(eyaToken, "Login_Action_Login", cancellationToken);
        var result = await Task.Run(
            () => AppState.LoginService.Login(accountName, eyaToken, progress),
            cancellationToken);
        await SaveLoginHistoryAsync(result, eyaToken, profile);
        return result;
    }

    private async void BuyButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = AppState.GetNfaApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            UpdateNfaKeyWarning();
            ShowStatus(Loc.T("Nfa_Warning_NoKey_Message"), InfoBarSeverity.Warning);
            return;
        }

        // An unconfirmed purchase is settled from its notice first, so a second order is never placed on top of it.
        if (AppState.GetPendingPurchase() is not null)
        {
            UpdatePendingPurchaseBar();
            return;
        }

        var item = SelectedStockItem();
        if (item is null)
        {
            ShowStatus(Loc.T("Nfa_Buy_Error_NoType"), InfoBarSeverity.Warning);
            return;
        }

        if (item.Available <= 0)
        {
            ShowStatus(Loc.T("Nfa_Buy_Error_OutOfStock"), InfoBarSeverity.Warning);
            return;
        }

        if (await ConfirmPurchaseAsync(item) != ContentDialogResult.Primary || AppState.IsBusy)
        {
            return;
        }

        // The price shown came from the last stock load, which can be old. nfa.pub charges its current price, so the
        // purchase only goes ahead at the price the user just agreed to.
        NfaStockItem? current;
        try
        {
            _nfaStock = await AppState.NfaClient.GetCs2StockAsync(apiKey);
            RenderNfaStock();
            current = _nfaStock.FirstOrDefault(stock => stock.ProductId == item.ProductId);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Tf("Nfa_Buy_Error_StockFailed_Format", ex.Message), InfoBarSeverity.Error);
            return;
        }

        if (AppState.IsBusy || AppState.GetPendingPurchase() is not null)
        {
            UpdatePendingPurchaseBar();
            return;
        }

        if (current is null || current.Available <= 0)
        {
            ShowStatus(Loc.T("Nfa_Buy_Error_OutOfStock"), InfoBarSeverity.Warning);
            return;
        }

        if (current.PriceEur != item.PriceEur)
        {
            ShowStatus(
                Loc.Tf("Nfa_Buy_Error_PriceChanged_Format", current.Name, NfaPubClient.FormatEur(current.PriceEur)),
                InfoBarSeverity.Warning);
            return;
        }

        // Saved to disk before the request goes out, so a purchase that is interrupted, even by closing the app, can
        // be finished later: replaying the same key makes nfa.pub return that order instead of charging again.
        var purchase = AppState.StartPendingPurchase(item.ProductId, item.Type, item.Name, apiKey);
        if (purchase is null)
        {
            ShowStatus(Loc.T("Nfa_Buy_Error_CannotSave"), InfoBarSeverity.Error);
            return;
        }

        AppLog.Info($"Buying {item.ProductId} as type {item.Type} at EUR {NfaPubClient.FormatEur(item.PriceEur)}, " +
            $"attempt key {purchase.Key[..8]}");
        await RunPurchaseAsync(purchase.ApiKey!, purchase, recovering: false);
    }

    /// <summary>
    /// The purchase summary: the product with its CS2 art, the price, and the balance before and after. Buy is the
    /// accent button but not the default one, so Enter alone never spends money.
    /// </summary>
    private async Task<ContentDialogResult> ConfirmPurchaseAsync(NfaStockItem item)
    {
        var price = NfaPubClient.FormatEur(item.PriceEur);
        var panel = new StackPanel { Spacing = 16, Width = Controls.NfaDialogParts.Width };

        var product = new Grid { ColumnSpacing = 16 };
        product.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        product.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (Controls.NfaDialogParts.ProductArt(item) is { } art)
        {
            product.Children.Add(art);
        }

        var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        name.Children.Add(new TextBlock
        {
            Text = Loc.T("Nfa_Buy_Confirm_Subtitle"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Controls.NfaDialogParts.Resource("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(name, 1);
        product.Children.Add(name);
        panel.Children.Add(product);

        var summary = new StackPanel { Spacing = 10 };
        summary.Children.Add(Controls.NfaDialogParts.Row(Loc.T("Nfa_Buy_Confirm_Price"), $"\u20ac{price}"));
        var canAfford = true;
        if (_nfaBalance is { } balance)
        {
            var after = balance - item.PriceEur;
            canAfford = after >= 0;
            summary.Children.Add(Controls.NfaDialogParts.Row(Loc.T("Nfa_Buy_Confirm_Balance"), $"\u20ac{NfaPubClient.FormatEur(balance)}"));
            summary.Children.Add(Controls.NfaDialogParts.Divider());
            summary.Children.Add(Controls.NfaDialogParts.Row(
                Loc.T("Nfa_Buy_Confirm_After"),
                canAfford ? $"\u20ac{NfaPubClient.FormatEur(after)}" : Loc.T("Nfa_Buy_Confirm_NotEnough"),
                canAfford ? null : FormatHelper.GetStatusBrush(InfoBarSeverity.Error),
                strong: true));
        }

        panel.Children.Add(new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            Background = Controls.NfaDialogParts.Resource("SubtleFillColorSecondaryBrush"),
            Child = summary,
        });

        if (!canAfford)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("Nfa_Buy_Confirm_TopUp"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Controls.NfaDialogParts.Resource("TextFillColorSecondaryBrush"),
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Nfa_Buy_Confirm_Title"),
            Content = panel,
            PrimaryButtonText = Loc.Tf("Nfa_Buy_Confirm_Primary_Format", price),
            IsPrimaryButtonEnabled = canAfford,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.None,
        };
        return await dialog.ShowAsync();
    }

    // How long nfa.pub keeps idempotency keys is not documented. Replaying one it has forgotten would place a new
    // order, so an attempt older than this can only be dismissed, after the user checks their orders.
    private static bool CanFinish(NfaPendingPurchase pending) =>
        !string.IsNullOrWhiteSpace(pending.ApiKey) && DateTimeOffset.Now - pending.StartedAt <= PendingPurchaseMaxAge;

    private void UpdatePendingPurchaseBar()
    {
        var pending = AppState.GetPendingPurchase();
        // The attempt being sent right now already has the status bar, so its notice waits until it ends unsettled.
        NfaPendingBar.IsOpen = pending is not null && pending.Key != _inFlightPurchaseKey;
        // A closed InfoBar still takes a slot in the panel's spacing, which left a gap above the account type.
        NfaPendingBar.Visibility = NfaPendingBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        BuyButton.IsEnabled = pending is null && AppState.HasNfaApiKey && !AppState.IsBusy;
        if (pending is null)
        {
            return;
        }

        var started = FormatHelper.FormatDateTime(pending.StartedAt);
        var finishable = CanFinish(pending);
        NfaPendingBar.Message = finishable
            ? Loc.Tf("Nfa_Pending_Message_Format", pending.Name, started)
            : Loc.Tf("Nfa_Pending_Expired_Message_Format", pending.Name, started);
        NfaFinishPurchaseButton.Visibility = finishable ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NfaFinishPurchaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || AppState.GetPendingPurchase() is not { } pending || !CanFinish(pending))
        {
            UpdatePendingPurchaseBar();
            return;
        }

        AppLog.Info($"Finishing unconfirmed purchase of {pending.ProductId}, attempt key {pending.Key[..8]}");
        await RunPurchaseAsync(pending.ApiKey!, pending, recovering: true);
    }

    private async void NfaDismissPurchaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.GetPendingPurchase() is not { } pending)
        {
            UpdatePendingPurchaseBar();
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Nfa_Pending_Dismiss_Title"),
            Content = new TextBlock { Text = Loc.T("Nfa_Pending_Dismiss_Message"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("Nfa_Pending_Dismiss"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        AppLog.Warn($"User dismissed unconfirmed purchase of {pending.ProductId} from {pending.StartedAt:u}, attempt key {pending.Key[..8]}");
        AppState.ClearPendingPurchase();
        UpdatePendingPurchaseBar();
    }

    private async Task RunPurchaseAsync(string apiKey, NfaPendingPurchase pending, bool recovering)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        _inFlightPurchaseKey = pending.Key;
        UpdatePendingPurchaseBar();
        ShowStatus(Loc.T(recovering ? "Nfa_Buy_Settling" : "Nfa_Buy_Buying"), InfoBarSeverity.Informational);

        try
        {
            var purchase = await AppState.NfaClient.BuyCs2Async(apiKey, pending.Type, pending.Key, cancellationToken);
            try
            {
                // The attempt started before nfa.pub delivered, so its start time can only make the window look shorter,
                // never longer. A replay does not overwrite a replacement count that is already known.
                AppState.AccountHistoryService.SaveNfaAccount(
                    purchase.SteamId,
                    purchase.Token,
                    purchase.OrderId,
                    pending.StartedAt,
                    recovering ? null : NfaWarranty.ReplacementsPerOrder);
            }
            catch (Exception ex)
            {
                // Paid for and delivered, so it must not be lost. The notice stays, and Finish saves it again later.
                AppLog.Error($"Purchase {purchase.SteamId} on order {purchase.OrderId} arrived but could not be saved", ex);
                await NfaAccountRescue.PreserveAsync(
                    XamlRoot, "Buy", purchase.SteamId, purchase.Token, $"purchase on order {purchase.OrderId}");
                return;
            }

            // Cleared only once the account is safely in History.
            AppState.ClearPendingPurchase();

            // Show the bought account where Sign in will use it, even when it was finished from another mode.
            if (!IsAutoMode)
            {
                ModeSelector.SelectedItem = AutoModeItem;
            }

            AppState.ReloadHistory(purchase.SteamId);
            UpdateAccountInfo(purchase.SteamId, purchase.Token);
            ShowStatus(
                recovering
                    ? Loc.Tf("Nfa_Buy_Recovered_Format", pending.Name, purchase.SteamId)
                    : Loc.Tf("Nfa_Buy_Done_Format", pending.Name, NfaPubClient.FormatEur(purchase.ChargedEur)),
                InfoBarSeverity.Success);
        }
        // The key the attempt was sent with no longer works, so no replay can ever confirm it.
        catch (NfaApiException ex) when (recovering && ex.Code == "E1001")
        {
            AppLog.Warn($"Dropped unconfirmed purchase of {pending.ProductId}: its API key was refused, attempt key {pending.Key[..8]}");
            AppState.ClearPendingPurchase();
            ShowStatus(Loc.T("Nfa_Buy_PendingKeyRefused"), InfoBarSeverity.Warning);
        }
        // A refusal of a first attempt means nothing was charged. A refusal of a replay only describes the replay:
        // a rate limit, a closed shop or a drained balance says nothing about whether the original order went through,
        // so its key is kept. Only sourcing and delivery failures show the replay was handled as a new order that
        // charged nothing.
        catch (NfaApiException ex) when (recovering
            ? ex.Code is "E1501" or "E1502" or "E1503"
            : IsDefinitiveRefusal(ex.Code))
        {
            AppLog.Warn($"Purchase of {pending.ProductId} refused: {ex.Code}");
            AppState.ClearPendingPurchase();
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLog.Info($"Stopped waiting for purchase of {pending.ProductId}, attempt key {pending.Key[..8]} kept");
            ShowStatus(Loc.T("Nfa_Buy_Interrupted"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Purchase of {pending.ProductId} not confirmed, attempt key {pending.Key[..8]} kept", ex);
            ShowStatus(Loc.Tf("Nfa_Buy_Unsettled_Format", ex.Message), InfoBarSeverity.Warning);
        }
        finally
        {
            _inFlightPurchaseKey = null;
            AppState.EndBusyOperation();
            UpdatePendingPurchaseBar();
        }

        _ = RefreshNfaStockAsync();
    }

    // Answers that mean nothing was charged and no order exists. Anything else (no connection, a timeout, a dropped
    // connection, an unreadable response, a server error, E1000, E1302 "still running") may have gone through, so its
    // key is kept.
    private static bool IsDefinitiveRefusal(string code) => code is
        "E1001" or "E1002" or "E1003" or "E1004" or
        "E1101" or "E1102" or "E1103" or "E1104" or
        "E1201" or "E1202" or "E1301" or "E1401" or
        "E1501" or "E1502" or "E1503";

    /// <summary>Shows the replacement row and button on the Account Info card when its account came from nfa.pub.</summary>
    private void UpdateAccountInfoNfa()
    {
        var account = AccountInfoNfaAccount();
        var status = account is null ? null : NfaWarranty.GetStatus(account, DateTimeOffset.Now);
        ShowNfaStatus(status, AccountInfoNfaRow, AccountInfoNfaHeadline, AccountInfoNfaDetail);

        // Replace sits beside Refresh status only while it can be used. Once the window has closed or the order is
        // used up it is left out here; History still has it for the rare case nfa.pub extends a window.
        var canRequest = status?.CanRequest == true;
        AccountInfoReplaceButton.Visibility = canRequest ? Visibility.Visible : Visibility.Collapsed;
        AccountInfoActionGrid.ColumnSpacing = canRequest ? 8 : 0;
    }

    /// <summary>Fills a Replacement row: the headline in the success color while the window is open, the detail below it.</summary>
    internal static void ShowNfaStatus(NfaWarrantyStatus? status, FrameworkElement row, TextBlock headline, TextBlock detail)
    {
        row.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is null)
        {
            return;
        }

        headline.Text = status.Headline;
        if (status.State == NfaWarrantyState.Open)
        {
            headline.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
        }
        else
        {
            headline.ClearValue(TextBlock.ForegroundProperty);
        }

        detail.Text = status.Detail ?? "";
        detail.Visibility = string.IsNullOrEmpty(status.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private SteamAccountHistoryItem? AccountInfoNfaAccount() =>
        _accountInfoPanelSteamId is { } steamId &&
        AppState.FindHistoryAccount(steamId) is { } account &&
        !string.IsNullOrWhiteSpace(account.NfaOrder)
            ? account
            : null;

    private async void AccountInfoReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountInfoNfaAccount() is not { } account)
        {
            UpdateAccountInfoNfa();
            return;
        }

        if (!AppState.HasNfaApiKey)
        {
            UpdateNfaKeyWarning();
            ShowStatus(Loc.T("Nfa_Warning_NoKey_Message"), InfoBarSeverity.Warning);
            return;
        }

        await NfaReplaceFlow.RunAsync(XamlRoot, account);
        UpdateAccountInfoNfa();
    }

    private static SteamAccountHistoryItem? FindNfaAccountForToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || AppState.JwtTokenService.Inspect(token).SteamId is not { } steamId)
        {
            return null;
        }

        return AppState.FindHistoryAccount(steamId) is { } account && NfaReplaceFlow.CanOffer(account) ? account : null;
    }

    private SteamAccountHistoryItem? SelectedBoughtAccount() =>
        (BoughtAccountBox.SelectedItem as ComboBoxItem)?.Tag is string steamId
            ? AppState.FindHistoryAccount(steamId)
            : null;

    /// <summary>
    /// Every account in History that came from nfa.pub, newest first, so a purchase can still be picked after a
    /// restart. Keeps the current pick unless a specific account (such as one just bought) should be selected.
    /// </summary>
    private void RebuildBoughtAccounts(string? selectSteamId)
    {
        var current = SelectedBoughtAccount()?.SteamId;
        var listed = BoughtAccountBox.Items
            .OfType<ComboBoxItem>()
            .Select(item => item.Tag as string)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        BoughtAccountBox.Items.Clear();

        ComboBoxItem? requested = null;
        ComboBoxItem? kept = null;
        foreach (var account in AppState.HistoryAccounts.Where(a => !string.IsNullOrWhiteSpace(a.NfaOrder)))
        {
            var entry = new ComboBoxItem
            {
                Tag = account.SteamId,
                Content = string.IsNullOrWhiteSpace(account.PersonaName)
                    ? account.SteamId
                    : $"{account.PersonaName}  ·  {account.SteamId}",
            };
            BoughtAccountBox.Items.Add(entry);

            // Only a newly added account (just bought or received as a replacement) takes over the pick. Other
            // reloads name whatever History has selected, and must not change the account Sign in will use.
            if (string.Equals(account.SteamId, selectSteamId, StringComparison.OrdinalIgnoreCase) && !listed.Contains(account.SteamId))
            {
                requested = entry;
            }

            if (string.Equals(account.SteamId, current, StringComparison.OrdinalIgnoreCase))
            {
                kept = entry;
            }
        }

        // A reload for some other account (a sign-in, a profile refresh) must not move the pick Sign in will use.
        BoughtAccountBox.SelectedItem = requested ?? kept ?? BoughtAccountBox.Items.FirstOrDefault();

        // A replacement or a purchase changes what the card's account has left, even when the card shows another mode.
        UpdateAccountInfoNfa();
    }

    private void BoughtAccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsAutoMode)
        {
            UpdateAccountInfoFromCurrentInputs();
        }
    }

    private async void NfaRefreshStockButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNfaStockAsync();
    }

    private void NfaOpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance?.ShowSettings();
    }

    private void OnNfaApiKeyChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // The balance was read with the previous key.
            _nfaBalance = null;
            NfaBalanceText.Text = "";
            UpdateNfaKeyWarning();
            if (IsAutoMode)
            {
                _ = RefreshNfaStockAsync();
            }
        });
    }

    private void UpdateNfaKeyWarning()
    {
        var hasKey = AppState.HasNfaApiKey;
        NfaKeyWarningBar.IsOpen = !hasKey;
        NfaKeyWarningBar.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        BuyButton.IsEnabled = hasKey && !AppState.IsBusy && AppState.GetPendingPurchase() is null;
        if (!hasKey)
        {
            NfaBalanceText.Text = "";
        }
    }

    private NfaStockItem? SelectedStockItem() =>
        (NfaTypeBox.SelectedItem as ComboBoxItem)?.Tag is string type
            ? _nfaStock.FirstOrDefault(item => item.ProductId == type)
            : null;

    private async Task RefreshNfaStockAsync()
    {
        UpdateNfaKeyWarning();
        NfaRefreshStockButton.IsEnabled = false;
        var apiKey = AppState.GetNfaApiKey();

        try
        {
            _nfaStock = await AppState.NfaClient.GetCs2StockAsync(apiKey);
            RenderNfaStock();
        }
        catch (Exception ex)
        {
            NfaTypeBox.PlaceholderText = Loc.Tf("Nfa_Buy_Error_StockFailed_Format", ex.Message);
        }
        finally
        {
            NfaRefreshStockButton.IsEnabled = true;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        try
        {
            var balance = await AppState.NfaClient.GetBalanceAsync(apiKey);
            if (AppState.GetNfaApiKey() != apiKey)
            {
                // The key changed while this was loading; the refresh for the new key fills it in.
                return;
            }

            _nfaBalance = balance;
            NfaBalanceText.Text = Loc.Tf("Nfa_Buy_Balance_Format", NfaPubClient.FormatEur(balance));
        }
        catch (NfaApiException ex)
        {
            _nfaBalance = null;
            NfaBalanceText.Text = ex.Message;
        }
    }

    private void RenderNfaStock()
    {
        var selectedType = SelectedStockItem()?.ProductId;
        NfaTypeBox.Items.Clear();

        ComboBoxItem? toSelect = null;
        foreach (var item in _nfaStock)
        {
            var name = item.Name;
            var price = NfaPubClient.FormatEur(item.PriceEur);
            var entry = new ComboBoxItem
            {
                Tag = item.ProductId,
                IsEnabled = item.Available > 0,
                Content = item.Available > 0
                    ? Loc.Tf("Nfa_Buy_Type_Format", name, price, item.Available)
                    : Loc.Tf("Nfa_Buy_Type_OutOfStock_Format", name, price),
            };
            NfaTypeBox.Items.Add(entry);

            if (item.ProductId == selectedType || (toSelect is null && item.Available > 0))
            {
                toSelect = entry;
            }
        }

        NfaTypeBox.SelectedItem = toSelect;
    }

    private async void OneClickQueryButton_Click(object sender, RoutedEventArgs e)
    {
        var cancellationToken = AppState.BeginBusyOperation();
        ShowStatus(Loc.T("Login_Status_QueryingAccount"), InfoBarSeverity.Informational);

        try
        {
            var (accountName, eyaToken) = await GetCredentialsAsync(cancellationToken);
            var score = await QueryAndSaveCsStatusAsync(accountName, eyaToken, cancellationToken);
            ShowStatus(
                Loc.Tf("Login_Status_QueryDone_Format", score.DisplayText, score.PlayerLevelText, score.CooldownText, score.GcVacText),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowStatus(Loc.T("Login_Status_QueryCancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ManualPanel is null || AutoPanel is null || ActionButtonGrid is null)
        {
            return;
        }

        ApplyModeVisibility();
        UpdateAccountInfoFromCurrentInputs();

        if (IsAutoMode && _nfaStock.Count == 0)
        {
            _ = RefreshNfaStockAsync();
        }

    }

    private void ApplyModeVisibility()
    {
        var auto = IsAutoMode;
        ManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        AutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ManualCredentialBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsAutoMode)
        {
            UpdateAccountInfoFromCurrentInputs();
        }
    }

    // The account name that came with a token loaded from History. It is used only while the token box still holds
    // that token. A pasted token uses the History name for its Steam ID, or the Steam ID.
    private (string Name, string Token)? _knownManualAccount;

    private string? ResolveManualAccountName(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (_knownManualAccount is { } known && known.Token == token && !string.IsNullOrWhiteSpace(known.Name))
        {
            return known.Name;
        }

        var steamId = AppState.JwtTokenService.Inspect(token).SteamId;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        // An account History already knows by its login name keeps that name, so signing in does not rename it.
        return AppState.FindHistoryAccount(steamId)?.AccountName is { Length: > 0 } stored ? stored : steamId;
    }

    private async Task<(string AccountName, string EyaToken)> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (IsAutoMode)
        {
            await Task.CompletedTask;
            return SelectedBoughtAccount() is { } bought
                ? (bought.AccountName, FormatHelper.NormalizeToken(bought.EyaToken))
                : throw new InvalidOperationException(Loc.T("Nfa_Buy_Error_NothingBought"));
        }

        var eyaToken = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new InvalidOperationException(Loc.T("Login_Error_EyaTokenRequired"));
        }

        var accountName = ResolveManualAccountName(eyaToken)
            ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenNoSteamId"));

        return (accountName, eyaToken);
    }

    /// <summary>
    /// Queries and saves the CS account status in one click. The History page calls this too,
    /// same as the old version: the query updates the account info panel on the right of the Login page as it runs.
    /// </summary>
    public async Task<CsPremierScoreResult> QueryAndSaveCsStatusAsync(
        string accountName,
        string eyaToken,
        CancellationToken cancellationToken = default)
    {
        eyaToken = FormatHelper.NormalizeToken(eyaToken);
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (!tokenInfo.IsValid)
        {
            throw new InvalidOperationException($"{tokenInfo.Status}{Loc.T("Login_Error_CannotOneClickQuerySuffix")}");
        }

        var steamId = tokenInfo.SteamId
            ?? throw new InvalidOperationException(Loc.T("Login_Error_TokenMissingSteamIdQuery"));

        UpdateAccountInfo(accountName, eyaToken);
        // Keep the fetched profile: save it to disk with the CS status. If it were dropped, the nickname/avatar of accounts that were only queried would stay "Not synced" forever,
        // and ApplyStoredAccountInfoProfile below would use the profile-less record to wipe the correct nickname and avatar that briefly showed on the panel.
        var prefetchedProfile = await UpdateAccountProfileAsync(accountName, eyaToken);
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_VerifyingAndQuerying");
        ResetAvailabilityForeground();
        ShowCsStatusPlaceholder(Loc.T("Login_Value_Querying"));

        CsPremierScoreResult score;
        SteamTokenOnlineValidationResult online;
        try
        {
            score = await AppState.PremierScoreService.QueryAsync(eyaToken, steamId, cancellationToken);
            online = new SteamTokenOnlineValidationResult(true, Loc.T("Token_Result_Accepted"));
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Valid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Success);
        }
        catch (SteamCmException ex) when (ex.IsTokenFailure)
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            RestoreStoredCsStatus(steamId);
            throw new InvalidOperationException($"{ex.Message}{Loc.T("Login_Error_CannotOneClickQuerySuffix")}", ex);
        }
        catch
        {
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
            ResetAvailabilityForeground();
            RestoreStoredCsStatus(steamId);
            throw;
        }

        AppState.AccountHistoryService.SaveCsAccountStatus(
            accountName,
            steamId,
            eyaToken,
            tokenInfo.ExpiresAt,
            score,
            online,
            NullIfBlank(prefetchedProfile?.PersonaName),
            NullIfBlank(prefetchedProfile?.AvatarUrl),
            NullIfBlank(prefetchedProfile?.AvatarPath));

        AppState.ReloadHistory(steamId);
        ApplyStoredAccountInfoProfile(steamId);
        ShowCsStatus(score);
        return score;
    }

    private async Task<string> SaveLoginHistoryAsync(
        LoginResult result,
        string eyaToken,
        SteamAccountHistoryItem? prefetchedProfile)
    {
        try
        {
            // UpdateAccountProfileAsync already fetched the persona/avatar once before sign-in, so pass that in as prefetched here
            // and let SaveLoginAsync skip fetching again (most noticeable on a new account's first sign-in).
            await AppState.AccountHistoryService.SaveLoginAsync(
                result.AccountName,
                result.SteamId,
                eyaToken,
                result.ExpiresAt,
                NullIfBlank(prefetchedProfile?.PersonaName),
                NullIfBlank(prefetchedProfile?.AvatarUrl),
                NullIfBlank(prefetchedProfile?.AvatarPath));
            AppState.ReloadHistory(result.SteamId);
            ApplyStoredAccountInfoProfile(result.SteamId);
            // The fallback refresh goes after the save: the snapshot prefetched during sign-in is already tens of seconds old, and if we refreshed before sign-in,
            // the new values would be overwritten by the old snapshot SaveLoginAsync writes back above (the sign-in path would never settle).
            StartBackgroundProfileRefresh(result.SteamId);
            _lastLoginHistorySaveFailed = false;
            return Loc.T("Login_Status_HistorySaved_Suffix");
        }
        catch (Exception ex)
        {
            _lastLoginHistorySaveFailed = true;
            return Loc.Tf("Login_Status_HistorySaveFailed_Suffix_Format", ex.Message);
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The SteamID the account info panel on the right is showing now (kept up to date by UpdateAccountInfo when it parses the token, UI thread only).</summary>
    private string? _accountInfoPanelSteamId;

    /// <summary>
    /// Refetches one account's nickname/avatar in the background and saves it to disk, reloading the history list on the UI thread when something changed;
    /// updates the panel only if it still shows that account, and only the nickname/avatar, never score/level/cooldown,
    /// so a late callback cannot overwrite the "Fetching..." placeholder or put the previous account's profile into the current panel.
    /// Failures are only logged (best-effort, never interrupts the main flow).
    /// </summary>
    private void StartBackgroundProfileRefresh(string steamId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync([steamId]);
                if (refreshed > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AppState.ReloadHistory();
                        if (!string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var account = AppState.FindHistoryAccount(steamId);
                        if (account is null)
                        {
                            return;
                        }

                        AccountInfoAvatar.ProfilePicture = account.AvatarImage;
                        ShowPersona(account.PersonaName);
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Background refresh of account profile failed ({steamId}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Syncs the avatar/nickname in the account info panel on the right, and returns the profile known at this point for the caller to prefetch (so a later SaveLoginAsync does not fetch it again).
    /// Returns the history record when a complete one exists; otherwise the preview fetched from the network; null if neither is available.
    /// <paramref name="triggerBackgroundRefresh"/>=false is for the sign-in path: sign-in runs its own fallback refresh after saving history,
    /// and triggering one here would only let the old snapshot written back when sign-in ends overwrite the new values.
    /// </summary>
    private async Task<SteamAccountHistoryItem?> UpdateAccountProfileAsync(
        string accountName, string eyaToken, bool triggerBackgroundRefresh = true)
    {
        var tokenInfo = AppState.JwtTokenService.Inspect(eyaToken);
        if (string.IsNullOrWhiteSpace(tokenInfo.SteamId))
        {
            return null;
        }

        var storedAccount = AppState.FindHistoryAccount(tokenInfo.SteamId);
        if (storedAccount is not null)
        {
            ApplyAccountInfoProfile(storedAccount);
            if (!string.IsNullOrWhiteSpace(storedAccount.PersonaName) &&
                (!string.IsNullOrWhiteSpace(storedAccount.AvatarPath) ||
                    !string.IsNullOrWhiteSpace(storedAccount.AvatarUrl)))
            {
                // A complete record is returned right away for display, but still refetch once in the background as a fallback:
                // otherwise the profile is frozen once saved, and after the account changes its name/avatar on Steam this would show the old values forever.
                if (triggerBackgroundRefresh)
                {
                    StartBackgroundProfileRefresh(tokenInfo.SteamId);
                }

                return storedAccount;
            }
        }

        try
        {
            var profile = await AppState.AccountHistoryService.GetProfilePreviewAsync(
                accountName,
                tokenInfo.SteamId,
                eyaToken,
                tokenInfo.ExpiresAt);
            if (profile is not null)
            {
                // The preview carries no CS status, so only the header takes it.
                if (profile.AvatarImage is not null)
                {
                    AccountInfoAvatar.ProfilePicture = profile.AvatarImage;
                }

                if (!string.IsNullOrWhiteSpace(profile.PersonaName))
                {
                    ShowPersona(profile.PersonaName);
                }

                return profile;
            }
        }
        catch
        {
            // Profile sync is decorative; token validation and account actions should continue.
        }

        return storedAccount;
    }

    private void ApplyStoredAccountInfoProfile(string steamId)
    {
        var account = AppState.FindHistoryAccount(steamId);
        if (account is not null)
        {
            ApplyAccountInfoProfile(account);
        }
    }

    private void ApplyAccountInfoProfile(SteamAccountHistoryItem? account)
    {
        if (account is null)
        {
            AccountInfoAvatar.ProfilePicture = null;
            ShowPersona(null);
            ShowCsStatusPlaceholder(Loc.T("Login_Value_NotQueried"));
            return;
        }

        AccountInfoAvatar.ProfilePicture = account.AvatarImage;
        ShowPersona(account.PersonaName);
        ShowCsStatus(account);
    }

    // PersonPicture takes initials from DisplayName, and a Steam ID would show as "7". Left empty, it shows the
    // silhouette until the avatar loads.
    private void ShowPersona(string? personaName)
    {
        AccountInfoAvatar.DisplayName = string.Empty;
        if (string.IsNullOrWhiteSpace(personaName))
        {
            AccountInfoPersonaText.Text = Loc.T(_accountInfoPanelSteamId is null ? "Login_Card_NoAccount" : "Login_Card_NameNotSynced");
            // Dimmed with opacity rather than a brush picked now, so it stays right when the theme changes.
            AccountInfoPersonaText.Opacity = 0.7;
            return;
        }

        AccountInfoPersonaText.Text = personaName;
        AccountInfoPersonaText.Opacity = 1;
    }

    private void ShowCsStatusPlaceholder(string text)
    {
        AccountInfoStats.ShowPlaceholder(text);
        ShowFlagged(AccountInfoCooldownText, text, false);
        ShowFlagged(AccountInfoVacText, text, false);
    }

    private void RestoreStoredCsStatus(string steamId)
    {
        if (!string.Equals(_accountInfoPanelSteamId, steamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (AppState.FindHistoryAccount(steamId) is { } stored)
        {
            ShowCsStatus(stored);
        }
        else
        {
            ShowCsStatusPlaceholder(Loc.T("Login_Value_NotQueried"));
        }
    }

    private void ShowCsStatus(CsPremierScoreResult score)
    {
        AccountInfoStats.Show(score);
        ShowFlagged(AccountInfoCooldownText, score.CooldownText, score.HasCooldown);
        ShowFlagged(AccountInfoVacText, score.GcVacText, score.IsGcVacBanned);
    }

    private void ShowCsStatus(SteamAccountHistoryItem account)
    {
        if (account.CsStatusUpdatedAt is null && account.PremierScoreUpdatedAt is null && string.IsNullOrWhiteSpace(account.CompetitiveScore))
        {
            ShowCsStatusPlaceholder(Loc.T("Login_Value_NotQueried"));
            return;
        }

        AccountInfoStats.Show(account);

        // A saved cooldown counts down from when it was saved, so an expired one is not shown in red.
        ShowFlagged(
            AccountInfoCooldownText,
            FormatHelper.FormatCooldownText(account.RemainingCooldownSeconds, account.CooldownReason, Loc.T("Account_Pending")),
            account.RemainingCooldownSeconds is > 0 and <= int.MaxValue);
        ShowFlagged(AccountInfoVacText, account.GcVacText, account.GcVacBanned == true);
    }

    private static void ShowFlagged(TextBlock text, string value, bool flagged)
    {
        text.Text = value;
        if (flagged)
        {
            text.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
        }
        else
        {
            text.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    // actionName is a stable localization key (Login_Action_*), not display text: it is compared against specific actions,
    // and Loc.T(actionName) gets the action name in the current language for the error, so UI text never drives control flow (it would not match across languages).
    private void EnsureTokenValidForAction(string eyaToken, string actionName)
    {
        var info = AppState.JwtTokenService.Inspect(eyaToken);
        if (info.IsValid)
        {
            return;
        }

        // Decide "expired" from the structured expiry time, not by comparing localized status text.
        var isExpired = info.ExpiresAt is { } expiry && expiry <= DateTimeOffset.Now;
        if (isExpired && actionName == "Login_Action_ClearWorkshop")
        {
            throw new InvalidOperationException(Loc.T("Login_Error_TokenExpiredCannotClearWorkshop"));
        }

        throw new InvalidOperationException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", info.Status, Loc.T(actionName)));
    }

    private void UpdateAccountInfoFromCurrentInputs()
    {
        if (AccountInfoUserText is null)
        {
            return;
        }

        if (IsAutoMode)
        {
            if (SelectedBoughtAccount() is { } bought)
            {
                UpdateAccountInfo(bought.AccountName, bought.EyaToken);
            }
            else
            {
                UpdateAccountInfo(null, null);
            }

            return;
        }

        var token = FormatHelper.NormalizeToken(EyaTokenBox.Text.Trim());
        UpdateAccountInfo(ResolveManualAccountName(token), token);
    }

    /// <summary>
    /// Restores the default foreground color for availability: placeholders such as not verified/verifying keep the same color as fields such as "Not filled/Not resolved",
    /// and only a validation result (valid/invalid) uses the success/failure color.
    /// </summary>
    private void ResetAvailabilityForeground() =>
        AccountInfoAvailabilityText.ClearValue(TextBlock.ForegroundProperty);

    private void UpdateAccountInfo(string? userName, string? token)
    {
        var info = string.IsNullOrWhiteSpace(token) ? null : AppState.JwtTokenService.Inspect(token);
        _accountInfoPanelSteamId = string.IsNullOrWhiteSpace(info?.SteamId) ? null : info.SteamId;
        ApplyAccountInfoProfile(null);

        // The login name only gets its own line when it says something the Steam ID does not.
        var showUser = !string.IsNullOrWhiteSpace(userName) &&
            !string.Equals(userName, _accountInfoPanelSteamId, StringComparison.OrdinalIgnoreCase);
        AccountInfoUserText.Text = showUser ? userName : "";
        AccountInfoUserText.Visibility = showUser ? Visibility.Visible : Visibility.Collapsed;
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerified");
        ResetAvailabilityForeground();

        if (info is null)
        {
            AccountInfoSteamIdText.Text = Loc.T("Login_Card_NoAccount_Hint");
            AccountInfoExpiresText.Text = "";
            AccountInfoExpiresText.Visibility = Visibility.Collapsed;
            UpdateAccountInfoNfa();
            return;
        }

        AccountInfoSteamIdText.Text = _accountInfoPanelSteamId ?? Loc.T("Login_Value_NotResolved");
        AccountInfoExpiresText.Text = info.ExpiresAt.HasValue
            ? Loc.Tf("Login_Card_Expires_Format", info.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
            : "";
        AccountInfoExpiresText.Visibility = info.ExpiresAt.HasValue ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(info.SteamId))
        {
            ApplyStoredAccountInfoProfile(info.SteamId);
        }

        UpdateAccountInfoNfa();
    }

    private async Task EnsureTokenAcceptedBySteamAsync(
        string eyaToken,
        string actionName,
        CancellationToken cancellationToken = default)
    {
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
        ResetAvailabilityForeground();

        SteamTokenOnlineValidationResult online;
        try
        {
            // ValidateAsync only turns "token refused by Steam" into IsValid=false; network or CM unreachable (including
            // TimeoutException) is thrown. Signing in itself only writes VDF + starts Steam and does not need this machine to reach CM,
            // so a network failure should not block sign-in: show a neutral message and carry on.
            online = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not reach Steam to validate the token online, skipped online validation and continuing with {Loc.T(actionName)}: {ex.Message}");
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_NotVerifiedOffline");
            ResetAvailabilityForeground();
            ShowStatus(Loc.Tf("Login_Status_SkippedOnlineValidation_Format", Loc.T(actionName)), InfoBarSeverity.Warning);
            return;
        }

        AccountInfoAvailabilityText.Text = online.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid");
        AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
            online.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);

        if (online.IsValid)
        {
            return;
        }

        // Getting here means Steam really refused the token (the IsTokenFailure path): keep it red and block the next actions.
        throw new SteamTokenRefusedException(
            Loc.Tf("Login_Error_StatusCannotAction_Format", online.Status, Loc.T(actionName)));
    }

    private async Task<SteamTokenOnlineValidationResult> ValidateTokenOnlineAsync(
        string eyaToken,
        CancellationToken cancellationToken = default)
    {
        AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Verifying");
        ResetAvailabilityForeground();

        try
        {
            var result = await AppState.TokenOnlineValidationService.ValidateAsync(eyaToken, cancellationToken);
            AccountInfoAvailabilityText.Text = result.IsValid ? Loc.T("Login_Availability_Valid") : Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(
                result.IsValid ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user cancelled: rethrow to the caller, which handles it as "Cancelled".
            throw;
        }
        catch (Exception ex)
        {
            var result = new SteamTokenOnlineValidationResult(false, Loc.Tf("Login_Status_OnlineValidationFailed_Format", ex.Message));
            AccountInfoAvailabilityText.Text = Loc.T("Login_Availability_Invalid");
            AccountInfoAvailabilityText.Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Error);
            return result;
        }
    }
}
