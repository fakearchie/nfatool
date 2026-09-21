using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NfaLoader.Pages;

public sealed partial class HistoryPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<SteamAccountHistoryItem> _viewItems = [];
    private readonly ObservableCollection<AccountImportEntry> _importEntries = [];

    /// <summary>
    /// Snapshot of the full list loaded from disk (that is, AppState.HistoryAccounts). Search and filtering run only on this in-memory list,
    /// so a keystroke no longer rereads the disk; the snapshot refreshes only on HistoryChanged and when the page is entered.
    /// </summary>
    private IReadOnlyList<SteamAccountHistoryItem> _allItems = [];

    /// <summary>Search box debounce timer: filters once, about 300ms after typing stops, instead of a full rebuild on every keystroke.</summary>
    private readonly DispatcherQueueTimer _searchDebounceTimer;

    /// <summary>Cooldown countdown timer: every second, tells accounts on cooldown to refresh their countdown binding; stops itself when no account is on cooldown.</summary>
    private readonly DispatcherQueueTimer _cooldownTimer;

    /// <summary>Accounts still on cooldown at the last tick: used to send one extra refresh on the tick the remainder hits zero, so the card lands on its final "no cooldown" state.</summary>
    private readonly HashSet<SteamAccountHistoryItem> _cooldownLiveLastTick = [];

    /// <summary>Whether the account selected in the detail panel was on cooldown at the last tick (the detail cooldown row is set imperatively, so it also needs one extra refresh on the zero tick).</summary>
    private bool _detailCooldownLiveLastTick;

    /// <summary>The account the detail panel's note box is bound to (the save on focus loss uses it, so a changed selection does not write to the wrong account).</summary>
    private SteamAccountHistoryItem? _noteAccount;

    /// <summary>Tag sentinel for the "Ungrouped" filter item (distinct from null = all, and from a real group ID).</summary>
    private const string UngroupedSentinel = "__ungrouped__";

    /// <summary>Currently loaded group definitions (sorted by Order, then name), from settings.json.</summary>
    private List<AccountGroup> _groups = [];

    /// <summary>Current group filter: null = all / <see cref="UngroupedSentinel"/> = ungrouped / anything else = group ID.</summary>
    private string? _groupFilter;

    /// <summary>Suppresses the SelectionChanged callback while the filter dropdown is rebuilt, to avoid a reentrant rebuild.</summary>
    private bool _suppressGroupFilterChange;

    /// <summary>Whether the page is active (navigated to and not left yet); used to defer rebuilds while hidden.</summary>
    private bool _isActive;

    /// <summary>
    /// A HistoryChanged received while hidden only updates the snapshot and records the SteamID to select (null keeps the current selection),
    /// without rebuilding the whole list; the next OnNavigatedTo rebuilds once through ReloadHistory.
    /// </summary>
    private string? _pendingSelectSteamId;

    /// <summary>
    /// Reentrancy latch for dialog flows: a XamlRoot can show only one ContentDialog at a time, and a second ShowAsync throws.
    /// The import flow is suspended between the clipboard read await and ShowAsync, so any Import, Delete or Clear click in that window must be blocked.
    /// </summary>
    private bool _isDialogFlowActive;

    /// <summary>
    /// Batch selection set (account keys). Separate from the ListView single selection (the detail focus): ticking a card's top-left checkbox adds it here,
    /// which drives the card's dark border and check mark and the bottom batch action bar. Stored by key so ticks survive list rebuilds (which swap in new instances).
    /// </summary>
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

    public HistoryPage()
    {
        InitializeComponent();
        HistoryAccountList.ItemsSource = _viewItems;
        ImportDialogList.ItemsSource = _importEntries;

        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += (_, _) => RebuildView(GetSelectedSteamId());

        _cooldownTimer = DispatcherQueue.CreateTimer();
        _cooldownTimer.Interval = TimeSpan.FromSeconds(1);
        _cooldownTimer.IsRepeating = true;
        _cooldownTimer.Tick += (_, _) => OnCooldownTick();

        // The replacement countdown is in minutes, so a slow timer is enough.
        var warrantyTimer = DispatcherQueue.CreateTimer();
        warrantyTimer.Interval = TimeSpan.FromSeconds(30);
        warrantyTimer.IsRepeating = true;
        warrantyTimer.Tick += (_, _) => UpdateNfaWarrantyText(HistoryAccountList.SelectedItem as SteamAccountHistoryItem);
        warrantyTimer.Start();

        AppState.HistoryChanged += OnHistoryChanged;
        AppState.NfaApiKeyChanged += () => DispatcherQueue.TryEnqueue(UpdateNfaKeyWarning);
        UpdateNfaKeyWarning();
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        Loc.LanguageChanged += OnLanguageChanged;

        // Take the selection request queued before the page was created (on first construction _viewItems is empty, so GetSelectedSteamId is always null).
        var pending = AppState.PendingHistorySelection;
        AppState.PendingHistorySelection = null;
        _allItems = AppState.HistoryAccounts;
        LoadGroups();
        RebuildView(pending);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML binding entry point: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Static x:Bind text is recomputed with Strings; imperative text (detail, batch bar, summary, control state) switches language by rerunning its method.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            UpdateSummaryTexts();
            RebuildGroupFilterCombo();
            UpdateBatchBar();
            UpdateDetail();
            UpdateControlsEnabled();
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;

        // Group definitions live in settings.json (no change event), so reload them on entry to pick up edits made elsewhere.
        LoadGroups();

        // A selection request queued while hidden wins over the current selection; ReloadHistory refreshes the snapshot and triggers a rebuild.
        var select = _pendingSelectSteamId ?? GetSelectedSteamId();
        _pendingSelectSteamId = null;
        AppState.ReloadHistory(select);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
        // Stop the debounce and countdown timers so they do not keep refreshing or rebuilding an off-screen page.
        _searchDebounceTimer.Stop();
        _cooldownTimer.Stop();

        // If we navigate away while hovering, PointerExited may not fire; clear the leftover hover state so no empty check circle is left when we come back.
        foreach (var account in _allItems)
        {
            account.IsPointerOver = false;
        }
    }

    private void CancelHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.ShowStatus(Loc.T("History_Status_Canceling"), InfoBarSeverity.Informational);
        AppState.CancelBusyOperation();
    }

    private void OnHistoryChanged(string? selectSteamId)
    {
        // On page entry, OnNavigatedTo sets _isActive to true before its ReloadHistory reaches here, so the rebuild runs normally;
        // while the page is hidden (a background refresh from another page), only update the in-memory snapshot and record the selection request; the next OnNavigatedTo rebuilds.
        _allItems = AppState.HistoryAccounts;
        if (!_isActive)
        {
            _pendingSelectSteamId = selectSteamId ?? _pendingSelectSteamId;
            return;
        }

        RebuildView(selectSteamId ?? GetSelectedSteamId());
    }

    private void RebuildView(string? selectSteamId)
    {
        // Filter only the in-memory snapshot, without rereading the disk. Filter by group first, then by search text.
        var source = _allItems;
        var filter = HistorySearchBox.Text.Trim();
        var filtered = source
            .Where(MatchesGroupFilter)
            .Where(account => string.IsNullOrEmpty(filter) || Matches(account, filter))
            .ToList();

        // The batch tick set survives rebuilds by account key: drop accounts that no longer exist, then apply the tick state to the (possibly new) instances.
        var liveKeys = source
            .Select(AccountHistoryService.GetAccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.IntersectWith(liveKeys);
        foreach (var account in source)
        {
            account.IsSelected = _checkedKeys.Contains(AccountHistoryService.GetAccountKey(account));
        }

        // Remember the key of the single-selected account (the detail focus) and restore it after the rebuild: a delayed refresh such as a background profile sync must not lose the account being viewed.
        var activeKey = !string.IsNullOrWhiteSpace(selectSteamId)
            ? $"id:{selectSteamId}"
            : HistoryAccountList.SelectedItem is SteamAccountHistoryItem current
                ? AccountHistoryService.GetAccountKey(current)
                : null;

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var active = activeKey is null
            ? null
            : _viewItems.FirstOrDefault(account =>
                string.Equals(AccountHistoryService.GetAccountKey(account), activeKey, StringComparison.OrdinalIgnoreCase));
        // With no selection request (or the old selection filtered out or deleted), leave nothing selected: do not fall back to the first item,
        // or the detail panel shows the first account's avatar and profile before the user has clicked any account.
        HistoryAccountList.SelectedItem = active;

        HistoryEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummaryTexts();

        UpdateBatchBar();
        UpdateDetail();
        UpdateControlsEnabled();
        UpdateCooldownTimer();
    }

    /// <summary>Recomputes the header summary and empty-state text from the current snapshot (a language switch also reuses this to refresh the shown text).</summary>
    private void UpdateSummaryTexts()
    {
        var hasAny = _allItems.Count > 0;
        HistoryEmptyText.Text = hasAny ? Loc.T("History_Empty_NoMatch") : Loc.T("History_Empty_Title");
        HistoryEmptyHintText.Text = hasAny
            ? Loc.T("History_Empty_NoMatch_Hint")
            : Loc.T("History_Empty_Hint");
        HistorySummaryText.Text = hasAny
            ? Loc.Tf("History_Subtitle_Count_Format", _allItems.Count)
            : Loc.T("History_Subtitle");
    }

    private static bool Matches(SteamAccountHistoryItem account, string filter)
    {
        return Contains(account.AccountName, filter) ||
            Contains(account.PersonaName, filter) ||
            Contains(account.SteamId, filter) ||
            Contains(account.Note, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrEmpty(value) &&
            value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // Debounce: continuous typing only restarts the timer; the filter and rebuild run about 300ms after typing stops.
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
    }

    // Whether the "Refresh" button's online profile sync is running (stops repeated clicks from stacking fetch rounds; only the UI thread touches it, so no locking).
    private bool _profileRefreshInFlight;

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        // Reread the disk first so the button still responds instantly; then refetch every account's name and avatar in the background.
        // This button used to only reread the disk, so once a profile was saved nothing could update it, and a renamed account or new avatar stayed stale forever.
        AppState.ReloadHistory(GetSelectedSteamId());

        // While a sync is running, do not falsely report "Refreshed": show the in-progress status again (which also restores it if another message replaced it).
        if (_profileRefreshInFlight)
        {
            AppState.ShowStatus(Loc.T("History_Status_ProfileSyncing"), InfoBarSeverity.Informational);
            return;
        }

        var steamIds = AppState.HistoryAccounts
            .Select(item => item.SteamId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (steamIds.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_Refreshed"), InfoBarSeverity.Success);
            return;
        }

        _profileRefreshInFlight = true;
        AppState.ShowStatus(Loc.T("History_Status_ProfileSyncing"), InfoBarSeverity.Informational);
        try
        {
            var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync(steamIds);
            if (refreshed > 0)
            {
                AppState.ReloadHistory(GetSelectedSteamId());
                AppState.ShowStatus(
                    Loc.Tf("History_Status_ProfileSyncDone_Format", refreshed), InfoBarSeverity.Success);
            }
            else
            {
                AppState.ShowStatus(Loc.T("History_Status_ProfileSyncNone"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to refresh history account profiles.", ex);
            AppState.ShowStatus(
                Loc.Tf("History_Status_ProfileSyncFail_Format", ex.Message), InfoBarSeverity.Warning);
        }
        finally
        {
            _profileRefreshInFlight = false;
        }
    }

    private void HistoryAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private void ExportAccountsToClipboard(IReadOnlyList<SteamAccountHistoryItem> accounts)
    {
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToExport"), InfoBarSeverity.Error);
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            accounts.Select(account => $"{account.AccountName}----{account.EyaToken}"));

        var package = new DataPackage();
        package.SetText(text);

        try
        {
            // Tokens are sensitive credentials: keep them out of Win+V clipboard history and cloud clipboard roaming.
            var options = new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            };
            if (!Clipboard.SetContentWithOptions(package, options))
            {
                // SetContentWithOptions can return false on some system configurations; fall back to a plain write so export still works.
                Clipboard.SetContent(package);
            }
        }
        catch (COMException)
        {
            AppState.ShowStatus(Loc.T("History_Status_ClipboardWriteFail"), InfoBarSeverity.Error);
            return;
        }

        try
        {
            // Without Flush the content is rendered lazily by this process, so the clipboard empties when the app exits; a failed Flush does not affect pasting now.
            Clipboard.Flush();
        }
        catch (COMException)
        {
        }

        AppState.ShowStatus(
            accounts.Count == 1
                ? Loc.Tf("History_Status_Exported_One_Format", accounts[0].AccountTitle)
                : Loc.Tf("History_Status_Exported_Many_Format", accounts.Count),
            InfoBarSeverity.Success);
    }

    private async void ImportHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        List<AccountImportEntry>? selected;
        _isDialogFlowActive = true;
        try
        {
            selected = await PickImportEntriesAsync();
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        if (selected is null || selected.Count == 0)
        {
            return;
        }

        AppState.SetBusy(true);
        try
        {
            var (added, updated) = AppState.AccountHistoryService.ImportAccounts(selected);
            AppState.ReloadHistory(selected[0].SteamId);
            AppState.ShowStatus(
                Loc.Tf("History_Status_ImportDone_Format", added, updated),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_ImportFail_Format", ex.Message), InfoBarSeverity.Error);
            return;
        }
        finally
        {
            AppState.SetBusy(false);
        }

        // Fill in names and avatars in the background, then refresh the list (does not hold the global busy state; a failure does not undo the import, but the user should know).
        try
        {
            var refreshed = await AppState.AccountHistoryService.RefreshProfilesAsync(
                selected.Select(entry => entry.SteamId).ToList());
            if (refreshed > 0)
            {
                AppState.ReloadHistory(GetSelectedSteamId());
                AppState.ShowStatus(Loc.Tf("History_Status_ProfileSyncDone_Format", refreshed), InfoBarSeverity.Success);
            }
            else
            {
                // Every fetch failed (offline, rate limited, etc.): do not leave "Import complete" up as if nothing went wrong; show a message the user can retry from.
                AppState.ShowStatus(Loc.T("History_Status_ProfileSyncNone"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to fill in account profiles after import.", ex);
            AppState.ShowStatus(
                Loc.Tf("History_Status_ProfileSyncFail_Format", ex.Message), InfoBarSeverity.Warning);
        }
    }

    /// <summary>Read clipboard → parse → show the selection dialog; returns the entries the user confirmed, or null on cancel or when nothing can be imported.</summary>
    private async Task<List<AccountImportEntry>?> PickImportEntriesAsync()
    {
        string clipboardText;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                AppState.ShowStatus(Loc.T("History_Status_ClipboardNoText"), InfoBarSeverity.Error);
                return null;
            }

            clipboardText = await content.GetTextAsync();
        }
        catch (COMException)
        {
            AppState.ShowStatus(Loc.T("History_Status_ClipboardReadFail"), InfoBarSeverity.Error);
            return null;
        }

        var (entries, invalidCount) = ParseImportText(clipboardText);
        if (entries.Count == 0)
        {
            AppState.ShowStatus(
                invalidCount > 0
                    ? Loc.Tf("History_Status_ImportNoneRecognized_Format", invalidCount)
                    : Loc.T("History_Status_ImportNone"),
                InfoBarSeverity.Error);
            return null;
        }

        _importEntries.Clear();
        foreach (var entry in entries)
        {
            _importEntries.Add(entry);
        }

        ImportDialogSummaryText.Text = invalidCount > 0
            ? Loc.Tf("History_ImportDialog_Summary_WithInvalid_Format", entries.Count, invalidCount)
            : Loc.Tf("History_ImportDialog_Summary_Format", entries.Count);
        ImportDialogList.SelectAll();

        if (await ImportDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return ImportDialogList.SelectedItems.OfType<AccountImportEntry>().ToList();
    }

    private void ImportDialogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ImportDialog.IsPrimaryButtonEnabled = ImportDialogList.SelectedItems.Count > 0;
    }

    private async Task DeleteAccountsWithConfirmAsync(IReadOnlyList<SteamAccountHistoryItem> accounts)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToDelete"), InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join(", ", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? Loc.Tf("History_Delete_Confirm_Many_Format", nameText, accounts.Count)
            : Loc.Tf("History_Delete_Confirm_Few_Format", nameText, accounts.Count);

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_Delete_Dialog_Title"),
            Content = summary,
            PrimaryButtonText = Loc.T("Common_Delete"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // Remove these keys from the batch selection before deleting, so they do not stay ticked after the rebuild.
            foreach (var account in accounts)
            {
                _checkedKeys.Remove(AccountHistoryService.GetAccountKey(account));
            }

            var removed = AppState.AccountHistoryService.DeleteAccounts(accounts);
            AppState.ReloadHistory();
            AppState.ShowStatus(Loc.Tf("History_Status_Deleted_Format", removed), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_DeleteFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    // ---------- Card hover / top-left checkbox / single-card actions ----------

    private static SteamAccountHistoryItem? CardItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as SteamAccountHistoryItem;

    private void HistoryCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = true;
        }
    }

    private void HistoryCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = false;
        }
    }

    private void HistoryCardCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        var key = AccountHistoryService.GetAccountKey(account);
        if (account.IsSelected)
        {
            account.IsSelected = false;
            _checkedKeys.Remove(key);
        }
        else
        {
            account.IsSelected = true;
            _checkedKeys.Add(key);
        }

        UpdateBatchBar();
    }

    private async void CardQuickLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        if (AppState.LoginPage is not { } loginPage)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginPageNotReady"), InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(account.AccountName))
        {
            AppState.ShowStatus(Loc.T("History_Status_MissingAccountName"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus(Loc.Tf("History_Status_LoggingIn_Format", account.AccountTitle), InfoBarSeverity.Informational);
        var progress = new Progress<string>(message => AppState.ShowStatus(message, InfoBarSeverity.Informational));
        SteamAccountHistoryItem? refusedNfaAccount = null;

        try
        {
            var result = await loginPage.QuickLoginAsync(
                account.AccountName, account.EyaToken, progress, cancellationToken);
            AppState.ReloadHistory(result.SteamId);
            AppState.ShowStatus(
                Loc.Tf("History_Status_LoginStarted_Format", account.AccountTitle, result.SteamId),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginCanceled"), InfoBarSeverity.Informational);
        }
        catch (SteamTokenRefusedException ex)
        {
            AppLog.Warn($"Quick login stopped, Steam refused the login token: {ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
            if (NfaReplaceFlow.CanOffer(account))
            {
                refusedNfaAccount = account;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Quick login failed.", ex);
            AppState.ShowStatus(Loc.Tf("History_Status_LoginFail_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
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

    private void CardExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            ExportAccountsToClipboard(new[] { account });
        }
    }

    private async void CardDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            await DeleteAccountsWithConfirmAsync(new[] { account });
        }
    }

    // ---------- Bottom batch action bar (appears once any card is ticked; acts on all ticked accounts) ----------

    // Acts only on the currently visible (filtered) list, so a search filter never batch deletes or exports ticked items the user cannot see.
    // Ticked items hidden by the filter stay in _checkedKeys and reappear, and count again, once the search is cleared.
    private List<SteamAccountHistoryItem> GetCheckedAccounts() =>
        _viewItems
            .Where(account => _checkedKeys.Contains(AccountHistoryService.GetAccountKey(account)))
            .ToList();

    private void UpdateBatchBar()
    {
        // Count the same (visible) set as GetCheckedAccounts, so "N selected" matches what the batch actions will act on.
        var count = GetCheckedAccounts().Count;
        BatchActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = Loc.Tf("Common_Selected_Format", count);
    }

    private void ClearCheckedSelection()
    {
        _checkedKeys.Clear();
        foreach (var account in _allItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
    }

    private void BatchClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCheckedSelection();
    }

    private void BatchExportButton_Click(object sender, RoutedEventArgs e)
    {
        ExportAccountsToClipboard(GetCheckedAccounts());
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteAccountsWithConfirmAsync(GetCheckedAccounts());
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var total = AppState.HistoryAccounts.Count;
        if (total == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToClear"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_ClearAll_Dialog_Title"),
            Content = Loc.Tf("History_ClearAll_Dialog_Content_Format", total),
            PrimaryButtonText = Loc.T("Common_Clear"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var cleared = AppState.AccountHistoryService.ClearAll();
            AppState.ReloadHistory();
            AppState.ShowStatus(Loc.Tf("History_Status_Cleared_Format", cleared), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_ClearFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private async void ClearInvalidAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var accounts = AppState.HistoryAccounts.ToList();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToTest"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_ClearInvalid_Dialog_Title"),
            Content = Loc.Tf("History_ClearInvalid_Dialog_Content_Format", accounts.Count) +
                Environment.NewLine + Environment.NewLine +
                Loc.T("History_ClearInvalid_Dialog_Content_Note"),
            PrimaryButtonText = Loc.T("History_ClearInvalid_Dialog_Primary"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        // Testing reuses the global busy and cancel mechanism (the Cancel button can stop it); a network error or cancel deletes no accounts,
        // and only a complete test run deletes, in one go, the accounts Steam refused (including malformed or expired tokens).
        var cancellationToken = AppState.BeginBusyOperation();
        var invalid = new List<SteamAccountHistoryItem>();
        var tested = 0;
        string? networkError = null;
        var canceled = false;

        try
        {
            foreach (var account in accounts)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                AppState.ShowStatus(
                    Loc.Tf("History_Status_Testing_Format", tested + 1, accounts.Count, account.AccountTitle),
                    InfoBarSeverity.Informational);

                SteamTokenOnlineValidationResult result;
                try
                {
                    result = await AppState.TokenOnlineValidationService.ValidateAsync(
                        account.EyaToken, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    // Network error (CM unreachable, timeout, etc.): stop now and delete no accounts.
                    networkError = ex.Message;
                    break;
                }

                tested++;
                if (!result.IsValid)
                {
                    invalid.Add(account);
                }
            }

            // Whether the run finished, hit a network error or was canceled, accounts Steam has confirmed as refused are still deleted;
            // stopping only skips the remaining accounts (untested accounts are always kept).
            var removed = invalid.Count > 0
                ? AppState.AccountHistoryService.DeleteAccounts(invalid)
                : 0;
            if (removed > 0)
            {
                AppState.ReloadHistory();
            }

            if (networkError is not null)
            {
                AppLog.Warn($"Network error while batch testing history accounts, stopped: {networkError}");
                AppState.ShowStatus(
                    removed > 0
                        ? Loc.Tf("History_Status_TestNetworkErr_WithRemoved_Format", tested, networkError, removed)
                        : Loc.Tf("History_Status_TestNetworkErr_Format", tested, networkError),
                    InfoBarSeverity.Error);
                return;
            }

            if (canceled)
            {
                AppState.ShowStatus(
                    removed > 0
                        ? Loc.Tf("History_Status_TestCanceled_WithRemoved_Format", tested, removed)
                        : Loc.Tf("History_Status_TestCanceled_Format", tested),
                    InfoBarSeverity.Informational);
                return;
            }

            AppState.ShowStatus(
                removed > 0
                    ? Loc.Tf("History_Status_TestDone_WithRemoved_Format", tested, removed)
                    : Loc.Tf("History_Status_TestDone_AllValid_Format", tested),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_ClearInvalidFail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private static (List<AccountImportEntry> Entries, int InvalidCount) ParseImportText(string text)
    {
        var entries = new List<AccountImportEntry>();
        var seenSteamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidCount = 0;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryParseCredentials(line, out var accountName, out var token, out var info))
            {
                invalidCount++;
                continue;
            }

            var steamId = info.SteamId!;

            // If the same account appears on several lines, take the first.
            if (!seenSteamIds.Add(steamId))
            {
                continue;
            }

            entries.Add(new AccountImportEntry
            {
                AccountName = accountName,
                EyaToken = token,
                SteamId = steamId,
                TokenExpiresAt = info.ExpiresAt,
                TokenIsValid = info.IsValid,
                TokenStatus = info.Status,
                AlreadyExists = AppState.HistoryAccounts.Any(account =>
                    string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
            });
        }

        return (entries, invalidCount);
    }

    /// <summary>
    /// Parses one line of credentials. Tries each candidate split in turn ("----" columns, whitespace columns) and accepts the first whose token yields a SteamID.
    /// Single-column candidates come before joined ones, so a multi-column line such as "account----password----token" yields a clean token
    /// instead of storing the whole "password----token" string as the token.
    /// </summary>
    private static bool TryParseCredentials(
        string line,
        out string accountName,
        out string token,
        out JwtTokenInfo info)
    {
        foreach (var (name, candidateToken) in EnumerateCredentialCandidates(line))
        {
            if (name.Length == 0 || candidateToken.Length == 0)
            {
                continue;
            }

            var normalized = FormatHelper.NormalizeToken(candidateToken);
            JwtTokenInfo candidateInfo;
            try
            {
                candidateInfo = AppState.JwtTokenService.Inspect(normalized);
            }
            catch (Exception)
            {
                // Clipboard content is untrusted: treat any malformed fake JWT as unrecognized, and never let parsing crash the app.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidateInfo.SteamId))
            {
                accountName = name;
                token = normalized;
                info = candidateInfo;
                return true;
            }
        }

        accountName = "";
        token = "";
        info = new JwtTokenInfo(null, null, false, Loc.T("Jwt_Status_Unrecognized"), null);
        return false;
    }

    private static IEnumerable<(string AccountName, string Token)> EnumerateCredentialCandidates(string line)
    {
        // Multi-column formats such as "accountname----token" or "accountname----password----token":
        // the account name is the first column, and each remaining column is tried as the token.
        var columns = line.Split("----", StringSplitOptions.None);
        if (columns.Length >= 2)
        {
            var name = columns[0].Trim();
            for (var i = 1; i < columns.Length; i++)
            {
                yield return (name, columns[i].Trim());
            }

            // Edge case: the token itself contains "----" (base64url includes '-'), so also try everything after the first column as one string.
            if (columns.Length > 2)
            {
                yield return (name, string.Join("----", columns[1..]).Trim());
            }
        }

        // Also accept the space or tab separated "accountname token [note...]" format: each column is tried as the token.
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length >= 2)
        {
            for (var i = 1; i < fields.Length; i++)
            {
                yield return (fields[0].Trim(), fields[i].Trim());
            }
        }
    }

    private async void OneClickHistoryQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        if (AppState.LoginPage is not { } loginPage)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginPageNotReady"), InfoBarSeverity.Error);
            return;
        }

        // Use the same cancelable busy mechanism as the login page: a one-click query can take over a hundred seconds, so the Cancel button must be able to stop it.
        var cancellationToken = AppState.BeginBusyOperation();
        AppState.ShowStatus(Loc.Tf("History_Status_Querying_Format", account.AccountTitle), InfoBarSeverity.Informational);

        try
        {
            var score = await loginPage.QueryAndSaveCsStatusAsync(
                account.AccountName, account.EyaToken, cancellationToken);
            AppState.ShowStatus(
                Loc.Tf("History_Status_QueryDone_Format", account.AccountTitle, score.DisplayText, score.PlayerLevelText, score.CooldownText, score.GcVacText),
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AppState.ShowStatus(Loc.T("History_Status_QueryCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    private void UseHistoryAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        MainWindow.Instance?.LoadAccountIntoLogin(account);
    }

    private void UpdateNfaKeyWarning()
    {
        NfaKeyWarningBar.IsOpen = !AppState.HasNfaApiKey;
        NfaKeyWarningBar.Visibility = AppState.HasNfaApiKey ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NfaOpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance?.ShowSettings();
    }

    private async void ReplaceAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            AppState.ShowStatus(Loc.T("History_Status_SelectAccount"), InfoBarSeverity.Error);
            return;
        }

        if (!AppState.HasNfaApiKey)
        {
            UpdateNfaKeyWarning();
            AppState.ShowStatus(Loc.T("Nfa_Warning_NoKey_Message"), InfoBarSeverity.Warning);
            return;
        }

        await NfaReplaceFlow.RunAsync(XamlRoot, account);
    }

    private void UpdateNfaWarrantyText(SteamAccountHistoryItem? account)
    {
        var status = account is null ? null : NfaWarranty.GetStatus(account, DateTimeOffset.Now);
        LoginPage.ShowNfaStatus(status, HistoryDetailNfaRow, HistoryNfaHeadline, HistoryNfaDetail);
    }

    private void ShowDetailName(string text, bool placeholder)
    {
        HistoryDetailPersonaText.Text = text;
        // Dimmed with opacity rather than a brush picked now, so it stays right when the theme changes.
        HistoryDetailPersonaText.Opacity = placeholder ? 0.7 : 1;
    }

    /// <summary>Whether Steam accepted the token when it was last checked, and when it expires.</summary>
    private void ShowDetailToken(SteamAccountHistoryItem? account)
    {
        var expired = account?.TokenExpiresAt is { } expiry && expiry <= DateTimeOffset.Now;
        var (text, severity) = expired
            ? (Loc.T("Login_Availability_Expired"), InfoBarSeverity.Error)
            : account?.JwtAvailable switch
            {
                true => (Loc.T("Login_Availability_Valid"), InfoBarSeverity.Success),
                false => (Loc.T("Login_Availability_Invalid"), InfoBarSeverity.Error),
                _ => (Loc.T("Login_Availability_NotVerified"), InfoBarSeverity.Informational),
            };

        HistoryDetailAccountStatusText.Text = text;
        if (severity == InfoBarSeverity.Informational)
        {
            HistoryDetailAccountStatusText.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            HistoryDetailAccountStatusText.Foreground = FormatHelper.GetStatusBrush(severity);
        }

        HistoryDetailTokenExpiresText.Text = account?.TokenExpiresAt is { } at
            ? Loc.Tf("Login_Card_Expires_Format", at.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
            : "";
        HistoryDetailTokenExpiresText.Visibility = HistoryDetailTokenExpiresText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(
            HistoryDetailAccountStatusText,
            account?.JwtValidatedAt is { } validated ? Loc.Tf("History_Detail_Checked_Format", FormatHelper.FormatDateTime(validated)) : null);
    }

    /// <summary>The live cooldown countdown and the VAC flag, in red when they restrict the account.</summary>
    private void ShowDetailRestrictions(SteamAccountHistoryItem? account)
    {
        if (account is null)
        {
            SetFlagged(HistoryDetailCooldownText, Loc.T("History_Detail_Pending"), false);
            SetFlagged(HistoryDetailVacText, Loc.T("History_Detail_Pending"), false);
            return;
        }

        SetFlagged(HistoryDetailCooldownText, account.RemainingCooldownText, account.HasLiveCooldown);
        SetFlagged(HistoryDetailVacText, account.GcVacText, account.GcVacBanned == true);
    }

    private static void SetFlagged(TextBlock text, string value, bool flagged)
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

    private string? GetSelectedSteamId()
    {
        return HistoryAccountList.SelectedItem is SteamAccountHistoryItem account
            ? account.SteamId
            : null;
    }

    private void UpdateDetail()
    {
        if (HistoryDetailPersonaText is null)
        {
            return;
        }

        // Before overwriting the note box, save a note that is being edited but has not lost focus, so a background ReloadHistory or rebuild does not wipe the change.
        // Record, before the flush, whether the note box is being edited (has focus) and its bound account key: after a background rebuild the selected account is often a new instance read from the disk snapshot,
        // whose Note still holds the value from before the edit, so refilling unconditionally would wipe the text the user is typing (and the cursor).
        // We are past the AccountNameText null guard at the top (the visual tree is loaded), and the note box is created in the same pass, so it is never null here.
        var noteBoxHadFocus = HistoryDetailNoteBox.FocusState != FocusState.Unfocused;
        var editingKey = _noteAccount is null
            ? null
            : AccountHistoryService.GetAccountKey(_noteAccount);
        var inProgressNoteText = HistoryDetailNoteBox.Text;

        FlushPendingNote();

        if (HistoryAccountList.SelectedItem is not SteamAccountHistoryItem account)
        {
            HistoryDetailAvatar.ProfilePicture = null;
            // Only an empty string shows the default silhouette: PersonPicture takes initials from DisplayName, so English text would show letters such as "NS".
            HistoryDetailAvatar.DisplayName = string.Empty;
            ShowDetailName(Loc.T("History_Detail_NoAccountSelected"), placeholder: true);
            HistoryDetailSteamIdText.Text = Loc.T("History_Detail_PickHint");
            HistoryDetailAccountNameText.Visibility = Visibility.Collapsed;
            HistoryDetailStats.ShowPlaceholder(Loc.T("History_Detail_Pending"));
            ShowDetailToken(null);
            HistoryDetailLastLoginText.Text = Loc.T("History_Detail_NoRecord");
            ShowDetailRestrictions(null);
            UpdateNfaWarrantyText(null);
            HistoryDetailNoteBox.Text = string.Empty;
            _noteAccount = null;
            return;
        }

        // A Steam ID would show as the initial "7", so the silhouette stays until the avatar loads.
        HistoryDetailAvatar.DisplayName = string.Empty;
        HistoryDetailAvatar.ProfilePicture = account.AvatarImage;
        if (string.IsNullOrWhiteSpace(account.PersonaName))
        {
            ShowDetailName(Loc.T("Login_Card_NameNotSynced"), placeholder: true);
        }
        else
        {
            ShowDetailName(account.PersonaName, placeholder: false);
        }

        HistoryDetailSteamIdText.Text = account.SteamIdDisplay;
        var showLogin = !string.IsNullOrWhiteSpace(account.AccountName) &&
            !string.Equals(account.AccountName, account.SteamId, StringComparison.OrdinalIgnoreCase);
        HistoryDetailAccountNameText.Text = showLogin ? account.AccountName : "";
        HistoryDetailAccountNameText.Visibility = showLogin ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailStats.Show(account);
        ShowDetailToken(account);
        HistoryDetailLastLoginText.Text = account.LastLoginText;
        ShowDetailRestrictions(account);
        UpdateNfaWarrantyText(account);

        // If it is still the same account and the note box is being edited, keep the text the user is typing (already flushed to disk above) instead of refilling it from the old snapshot.
        var sameAccountBeingEdited = noteBoxHadFocus && editingKey is not null &&
            string.Equals(editingKey, AccountHistoryService.GetAccountKey(account), StringComparison.OrdinalIgnoreCase);
        if (sameAccountBeingEdited)
        {
            // Make the new instance's Note match the text on screen, so the next focus loss does not see "box != instance" and save again for nothing.
            account.Note = string.IsNullOrWhiteSpace(inProgressNoteText) ? null : inProgressNoteText.Trim();
        }
        else
        {
            HistoryDetailNoteBox.Text = account.Note ?? string.Empty;
        }

        _noteAccount = account;
    }

    // ---------- Cooldown countdown (refreshes cards and the detail panel on cooldown every second; stops once all have expired, to save power) ----------

    private void OnCooldownTick()
    {
        var anyLive = false;
        var stillLive = new HashSet<SteamAccountHistoryItem>();
        foreach (var account in _viewItems)
        {
            if (account.HasLiveCooldown)
            {
                account.NotifyCooldownTick();
                stillLive.Add(account);
                anyLive = true;
            }
            else if (_cooldownLiveLastTick.Contains(account))
            {
                // On cooldown last tick, at zero this tick: HasLiveCooldown turning false makes the normal branch skip it,
                // so send one more notification here to move the card from "1 second" to its final "no cooldown" state (or it stays stuck on the last non-zero value).
                account.NotifyCooldownTick();
            }
        }

        _cooldownLiveLastTick.Clear();
        foreach (var account in stillLive)
        {
            _cooldownLiveLastTick.Add(account);
        }

        // The detail panel's cooldown row is set imperatively (bindings do not update it), so refresh it when the selected account is on cooldown or reached zero this tick.
        if (HistoryAccountList.SelectedItem is SteamAccountHistoryItem selected)
        {
            if (selected.HasLiveCooldown)
            {
                ShowDetailRestrictions(selected);
                _detailCooldownLiveLastTick = true;
                anyLive = true;
            }
            else if (_detailCooldownLiveLastTick)
            {
                ShowDetailRestrictions(selected);
                _detailCooldownLiveLastTick = false;
            }
        }

        if (!anyLive)
        {
            _cooldownTimer.Stop();
        }
    }

    private void UpdateCooldownTimer()
    {
        var anyLive = _isActive && _viewItems.Any(account => account.HasLiveCooldown);
        if (anyLive)
        {
            if (!_cooldownTimer.IsRunning)
            {
                _cooldownTimer.Start();
            }
        }
        else
        {
            _cooldownTimer.Stop();
        }
    }

    private void HistoryDetailNoteBox_LostFocus(object sender, RoutedEventArgs e)
    {
        FlushPendingNote();
    }

    // Saves unsaved note box changes for _noteAccount to disk (called on focus loss and before every overwrite of the note box).
    // Idempotent: returns at once when nothing changed, with no status spam; updates the in-memory instance in place (the card's note marker refreshes right away via INPC) without rebuilding the list.
    private void FlushPendingNote()
    {
        var account = _noteAccount;
        if (account is null || HistoryDetailNoteBox is null)
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(HistoryDetailNoteBox.Text)
            ? null
            : HistoryDetailNoteBox.Text.Trim();
        if (string.Equals(normalized, account.Note, StringComparison.Ordinal))
        {
            return;
        }

        account.Note = normalized;
        try
        {
            AppState.AccountHistoryService.SetNote(account, normalized);
            AppState.ShowStatus(Loc.T("History_Status_NoteSaved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("History_Status_NoteSaveFail_Format", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---------- Batch import by account + password (trades the account and password for an EYA token and saves it; with a shared_secret 2FA is automatic, otherwise enter each code) ----------

    private sealed record WhiteAccountEntry(
        string AccountName, string Password, string? SharedSecret, string? Email, string? EmailPassword);

    private async void BatchImportWhiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        var pasteBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 420,
            Height = 180,
            IsSpellCheckEnabled = false,
            PlaceholderText = Loc.T("History_WhiteImport_Placeholder")
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("History_WhiteImport_Hint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
        });
        content.Children.Add(pasteBox);

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_WhiteImport_Title"),
            Content = content,
            PrimaryButtonText = Loc.T("Common_Import"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        string pasteText;
        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            pasteText = pasteBox.Text;
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        var entries = ParseWhiteAccounts(pasteText);
        if (entries.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_WhiteImport_NoneParsed"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var added = 0;
        var failed = 0;
        var canceled = false;
        string? lastSteamId = null;

        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var entry = entries[i];
                AppState.ShowStatus(
                    Loc.Tf("History_WhiteImport_Progress_Format", i + 1, entries.Count, entry.AccountName),
                    InfoBarSeverity.Informational);

                var progress = new Progress<string>(message =>
                    AppState.ShowStatus(
                        Loc.Tf("Common_LabelValue_Format", entry.AccountName, message),
                        InfoBarSeverity.Informational));

                // With a shared_secret the mobile authenticator code is generated automatically (unattended); for email verification a dialog asks the user to check that inbox and enter the code.
                async Task<string?> GuardProvider(SteamGuardPrompt prompt, CancellationToken token)
                {
                    if (prompt.Type == SteamGuardType.DeviceCode && !string.IsNullOrWhiteSpace(entry.SharedSecret))
                    {
                        var code = SteamTotp.GenerateAuthCode(entry.SharedSecret);
                        if (!string.IsNullOrEmpty(code))
                        {
                            return code;
                        }
                    }

                    var emailHint = prompt.Type == SteamGuardType.EmailCode ? entry.Email : null;
                    return await PromptGuardCodeAsync(prompt, token, emailHint);
                }

                try
                {
                    var result = await AppState.CredentialsAuthService.GetRefreshTokenAsync(
                        entry.AccountName, entry.Password, GuardProvider, progress, cancellationToken);

                    var token = FormatHelper.NormalizeToken(result.RefreshToken);
                    var info = AppState.JwtTokenService.Inspect(token);
                    await AppState.AccountHistoryService.SaveLoginAsync(
                        result.AccountName, result.SteamId, token, info.ExpiresAt);
                    lastSteamId = result.SteamId;
                    added++;
                }
                catch (OperationCanceledException)
                {
                    // Tell apart "the user clicked the global Cancel" and "only this account's code dialog was canceled or confirmed empty":
                    // only the first stops the whole batch; the second counts this account as failed and continues with the rest.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }

                    failed++;
                    AppLog.Warn($"Batch password import: skipped {entry.AccountName} (code step canceled).");
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"Batch password import failed: {entry.AccountName}, {ex.Message}");
                }
            }

            AppState.ReloadHistory(lastSteamId);
            AppState.ShowStatus(
                canceled
                    ? Loc.Tf("History_WhiteImport_Canceled_Format", added, failed)
                    : Loc.Tf("History_WhiteImport_Done_Format", added, failed),
                canceled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        finally
        {
            // Drop references to plaintext passwords and shared_secret early (strings cannot be zeroed, but this shortens how long they stay reachable on the heap).
            entries.Clear();
            AppState.EndBusyOperation();
        }
    }

    // Parses batch password import text: each line is "account----password" or "account----password----shared_secret"; whitespace separators also work.
    private static List<WhiteAccountEntry> ParseWhiteAccounts(string text)
    {
        var list = new List<WhiteAccountEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Contains("----", StringComparison.Ordinal)
                ? line.Split("----", StringSplitOptions.None)
                : line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var accountName = parts[0].Trim();
            var password = parts[1].Trim();
            if (accountName.Length == 0 || password.Length == 0)
            {
                continue;
            }

            // Remaining columns: one containing @ is an email (the next column is its password), any other column is the shared_secret. Accepts
            // "account----password", "account----password----shared_secret" and "account----password----email----emailpassword".
            string? sharedSecret = null, email = null, emailPassword = null;
            for (var i = 2; i < parts.Length; i++)
            {
                var field = parts[i].Trim();
                if (field.Length == 0)
                {
                    continue;
                }

                if (email is null && field.Contains('@'))
                {
                    email = field;
                    if (i + 1 < parts.Length)
                    {
                        var pwd = parts[i + 1].Trim();
                        emailPassword = pwd.Length == 0 ? null : pwd;
                        i++;
                    }
                }
                else
                {
                    sharedSecret ??= field;
                }
            }

            // If the same account appears on several lines, take the first.
            if (!seen.Add(accountName))
            {
                continue;
            }

            list.Add(new WhiteAccountEntry(accountName, password, sharedSecret, email, emailPassword));
        }

        return list;
    }

    // When sign-in needs a code, asks for the email or mobile code in a dialog (accounts without a shared_secret); returns null on cancel.
    // emailHint: for email verification, shows the account's linked email so the user knows which inbox to check.
    private async Task<string?> PromptGuardCodeAsync(
        SteamGuardPrompt prompt, CancellationToken cancellationToken, string? emailHint = null)
    {
        var isMobile = prompt.Type == SteamGuardType.DeviceCode;

        var codeBox = new TextBox
        {
            PlaceholderText = Loc.T("Creds_Guard_CodePlaceholder"),
            IsSpellCheckEnabled = false,
            MaxLength = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.AssociatedMessage)
                ? Loc.T(isMobile ? "Creds_Guard_MobileMessage" : "Creds_Guard_EmailMessage")
                : prompt.AssociatedMessage,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(emailHint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Tf("Creds_Guard_EmailAt_Format", emailHint),
                TextWrapping = TextWrapping.Wrap,
                Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
            });
        }

        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            Title = Loc.T(isMobile ? "Creds_Guard_MobileTitle" : "Creds_Guard_EmailTitle"),
            Content = panel,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? codeBox.Text.Trim() : null;
    }

    // ---------- Batch one-click query (queries every ticked account in turn, reusing the global busy and cancel mechanism) ----------

    private async void BatchQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.LoginPage is not { } loginPage)
        {
            AppState.ShowStatus(Loc.T("History_Status_LoginPageNotReady"), InfoBarSeverity.Error);
            return;
        }

        var accounts = GetCheckedAccounts();
        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("History_Status_NoneToQuery"), InfoBarSeverity.Error);
            return;
        }

        var cancellationToken = AppState.BeginBusyOperation();
        var succeeded = 0;
        var failed = 0;
        var canceled = false;

        try
        {
            for (var i = 0; i < accounts.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var account = accounts[i];
                AppState.ShowStatus(
                    Loc.Tf("History_Status_BatchQuerying_Format", i + 1, accounts.Count, account.AccountTitle),
                    InfoBarSeverity.Informational);

                try
                {
                    await loginPage.QueryAndSaveCsStatusAsync(account.AccountName, account.EyaToken, cancellationToken);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn($"Batch account query failed: {account.AccountTitle}, {ex.Message}");
                }
            }

            AppState.ShowStatus(
                canceled
                    ? Loc.Tf("History_Status_BatchQueryCanceled_Format", succeeded, failed)
                    : Loc.Tf("History_Status_BatchQueryDone_Format", succeeded, failed),
                canceled ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        finally
        {
            AppState.EndBusyOperation();
        }
    }

    // ---------- Account groups (definitions in settings.json, membership in each account's GroupIds) ----------

    private void LoadGroups()
    {
        _groups = AppState.SettingsService.Load().Groups
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RebuildGroupFilterCombo();
    }

    private void RebuildGroupFilterCombo()
    {
        if (GroupFilterCombo is null)
        {
            return;
        }

        _suppressGroupFilterChange = true;
        GroupFilterCombo.Items.Clear();
        GroupFilterCombo.Items.Add(new ComboBoxItem { Content = Loc.T("History_Group_Filter_All"), Tag = null });
        foreach (var group in _groups)
        {
            GroupFilterCombo.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Id });
        }

        GroupFilterCombo.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("History_Group_Filter_Ungrouped"),
            Tag = UngroupedSentinel
        });

        // Restore the last filter; if its group was deleted, fall back to "All".
        var target = GroupFilterCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _groupFilter, StringComparison.Ordinal));
        if (target is null)
        {
            _groupFilter = null;
            target = (ComboBoxItem)GroupFilterCombo.Items[0];
        }

        GroupFilterCombo.SelectedItem = target;
        _suppressGroupFilterChange = false;
    }

    private void GroupFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGroupFilterChange)
        {
            return;
        }

        _groupFilter = (GroupFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        RebuildView(GetSelectedSteamId());
    }

    private bool MatchesGroupFilter(SteamAccountHistoryItem account)
    {
        if (_groupFilter is null)
        {
            return true;
        }

        if (_groupFilter == UngroupedSentinel)
        {
            return account.GroupIds is not { Count: > 0 };
        }

        return account.GroupIds is { Count: > 0 } && account.GroupIds.Contains(_groupFilter);
    }

    private void BatchGroupFlyout_Opening(object sender, object e)
    {
        BatchGroupFlyout.Items.Clear();
        var accounts = GetCheckedAccounts();

        foreach (var group in _groups)
        {
            var allMembers = accounts.Count > 0 && accounts.All(account => account.GroupIds.Contains(group.Id));
            var toggle = new ToggleMenuFlyoutItem { Text = group.Name, IsChecked = allMembers };
            var groupId = group.Id;
            var add = !allMembers;
            toggle.Click += (_, _) => ApplyBatchGroup(groupId, add);
            BatchGroupFlyout.Items.Add(toggle);
        }

        if (_groups.Count > 0)
        {
            BatchGroupFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        var newItem = new MenuFlyoutItem { Text = Loc.T("History_Group_NewAndAdd") };
        newItem.Click += async (_, _) => await CreateGroupAndAddSelectedAsync();
        BatchGroupFlyout.Items.Add(newItem);

        var manageItem = new MenuFlyoutItem { Text = Loc.T("History_Group_Manage") };
        manageItem.Click += async (_, _) => await ManageGroupsAsync();
        BatchGroupFlyout.Items.Add(manageItem);
    }

    private void ApplyBatchGroup(string groupId, bool add)
    {
        var accounts = GetCheckedAccounts();
        if (accounts.Count == 0)
        {
            return;
        }

        try
        {
            var changed = AppState.AccountHistoryService.SetGroupMembership(accounts, groupId, add);
            AppState.ReloadHistory(GetSelectedSteamId());
            var groupName = _groups.FirstOrDefault(group => group.Id == groupId)?.Name ?? string.Empty;
            AppState.ShowStatus(
                add
                    ? Loc.Tf("History_Status_GroupAdded_Format", changed, groupName)
                    : Loc.Tf("History_Status_GroupRemoved_Format", changed, groupName),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            // SetGroupMembership throws when accounts.json is locked or corrupt (like delete, clear and import),
            // and an uncaught throw in an event handler kills the process; like the other change paths, show it in the status bar instead.
            AppLog.Warn($"Batch group change failed: {ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task CreateGroupAndAddSelectedAsync()
    {
        var name = await PromptGroupNameAsync(Loc.T("History_Group_NewDialog_Title"), string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var group = CreateGroup(name);
            var accounts = GetCheckedAccounts();
            if (accounts.Count > 0)
            {
                AppState.AccountHistoryService.SetGroupMembership(accounts, group.Id, true);
            }

            AppState.ReloadHistory(GetSelectedSteamId());
            AppState.ShowStatus(Loc.Tf("History_Status_GroupCreated_Format", group.Name), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to create group and add accounts: {ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static AccountGroup CreateGroup(string name)
    {
        var settings = AppState.SettingsService.Load();
        var group = new AccountGroup { Name = name.Trim(), Order = settings.Groups.Count };
        settings.Groups.Add(group);
        AppState.SettingsService.Save(settings);
        return group;
    }

    private async void ManageGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        await ManageGroupsAsync();
    }

    private async Task<string?> PromptGroupNameAsync(string title, string initial)
    {
        if (_isDialogFlowActive)
        {
            return null;
        }

        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = Loc.T("History_Group_Name_Placeholder"),
            AcceptsReturn = false
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = Loc.T("Common_Confirm"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private async Task ManageGroupsAsync()
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        LoadGroups();

        var newNameBox = new TextBox
        {
            PlaceholderText = Loc.T("History_Group_Name_Placeholder"),
            AcceptsReturn = false
        };
        var addButton = new Button { Content = Loc.T("History_Group_Add") };
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(newNameBox);
        addRow.Children.Add(addButton);

        var listPanel = new StackPanel { Spacing = 6 };
        var root = new StackPanel { Spacing = 12, MinWidth = 380 };
        root.Children.Add(addRow);
        root.Children.Add(listPanel);

        void RebuildRows()
        {
            listPanel.Children.Clear();
            LoadGroups();

            if (_groups.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("History_Group_Manage_Empty"),
                    Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
                });
                return;
            }

            foreach (var group in _groups)
            {
                var count = AppState.HistoryAccounts.Count(account =>
                    account.GroupIds is { Count: > 0 } && account.GroupIds.Contains(group.Id));
                var groupId = group.Id;

                var nameBox = new TextBox { Text = group.Name, VerticalAlignment = VerticalAlignment.Center };
                nameBox.LostFocus += (_, _) => RenameGroup(groupId, nameBox.Text);

                var countText = new TextBlock
                {
                    Text = Loc.Tf("History_Group_Manage_Count_Format", count),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational)
                };

                var deleteButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
                deleteButton.Click += (_, _) =>
                {
                    DeleteGroup(groupId);
                    RebuildRows();
                };

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(countText, 1);
                Grid.SetColumn(deleteButton, 2);
                row.Children.Add(nameBox);
                row.Children.Add(countText);
                row.Children.Add(deleteButton);
                listPanel.Children.Add(row);
            }
        }

        addButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newNameBox.Text))
            {
                return;
            }

            CreateGroup(newNameBox.Text);
            newNameBox.Text = string.Empty;
            RebuildRows();
        };

        RebuildRows();

        var dialog = new ContentDialog
        {
            Title = Loc.T("History_Group_Manage"),
            Content = new ScrollViewer { Content = root, MaxHeight = 420 },
            CloseButtonText = Loc.T("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        _isDialogFlowActive = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogFlowActive = false;
        }

        // Groups may have been renamed or deleted in the dialog: refresh the filter dropdown and the list.
        LoadGroups();
        RebuildView(GetSelectedSteamId());
    }

    private static void RenameGroup(string id, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        var group = settings.Groups.FirstOrDefault(item => item.Id == id);
        if (group is null || string.Equals(group.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        group.Name = newName;
        AppState.SettingsService.Save(settings);
    }

    private void DeleteGroup(string id)
    {
        try
        {
            var settings = AppState.SettingsService.Load();
            settings.Groups.RemoveAll(item => item.Id == id);
            AppState.SettingsService.Save(settings);
            AppState.AccountHistoryService.RemoveGroupFromAllAccounts(id);
            AppState.ReloadHistory(GetSelectedSteamId());
        }
        catch (Exception ex)
        {
            // RemoveGroupFromAllAccounts throws when accounts.json cannot be read; uncaught in the Manage groups dialog, it would crash the whole app.
            AppLog.Warn($"Failed to delete group: {ex.Message}");
            AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        var hasActive = HistoryAccountList.SelectedItem is SteamAccountHistoryItem;
        HistoryAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        RefreshHistoryButton.IsEnabled = !isBusy;
        HistorySearchBox.IsEnabled = !isBusy;
        ImportHistoryButton.IsEnabled = !isBusy;
        BatchImportWhiteButton.IsEnabled = !isBusy;
        ClearHistoryButton.IsEnabled = !isBusy && AppState.HistoryAccounts.Count > 0;
        ClearInvalidAccountsButton.IsEnabled = !isBusy && AppState.HistoryAccounts.Count > 0;
        OneClickHistoryQueryButton.IsEnabled = !isBusy && hasActive;
        UseHistoryAccountButton.IsEnabled = !isBusy && hasActive;
        ReplaceAccountButton.IsEnabled = !isBusy && hasActive;
        BatchClearButton.IsEnabled = !isBusy;
        BatchQueryButton.IsEnabled = !isBusy;
        BatchGroupButton.IsEnabled = !isBusy;
        BatchExportButton.IsEnabled = !isBusy;
        BatchDeleteButton.IsEnabled = !isBusy;
        GroupFilterCombo.IsEnabled = !isBusy;
        ManageGroupsButton.IsEnabled = !isBusy;

        // The Cancel button shows only while busy and stays enabled, so the user can stop a one-click query started from this page.
        CancelHistoryQueryButton.Visibility = isBusy && AppState.CanCancelBusyOperation ? Visibility.Visible : Visibility.Collapsed;
    }
}
