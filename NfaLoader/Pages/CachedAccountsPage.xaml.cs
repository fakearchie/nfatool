using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;

namespace NfaLoader.Pages;

public sealed partial class CachedAccountsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<CachedSteamLoginAccount> _viewItems = [];
    private IReadOnlyList<CachedSteamLoginAccount> _sourceItems = [];
    private bool _isDialogFlowActive;

    /// <summary>
    /// Batch check set (account CacheKey). Separate from the ListView single selection (detail focus): ticking a card's top left checkbox adds it here,
    /// which drives the card's black frame + tick and the batch action bar at the bottom. Stored by key so checks survive list rebuilds (new instances). Same as the history page.
    /// </summary>
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);

    public CachedAccountsPage()
    {
        InitializeComponent();
        CachedAccountList.ItemsSource = _viewItems;
        AppState.BusyChanged += _ => UpdateControlsEnabled();
        // Reload after the background profile refresh started by a sign-in is saved (the event comes from a background thread, so run on the UI thread);
        // Reload(GetSelectedKey()) restores the selection by key, and batch checks survive the rebuild through _checkedKeys.
        AppState.CachedLoginAccountsRefreshed += () =>
            _dispatcherQueue.TryEnqueue(() => Reload(GetSelectedKey()));
        Loc.LanguageChanged += OnLanguageChanged;
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Entry point for XAML bindings: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Static x:Bind text recomputes with Strings; text set from code (summary/empty state/batch bar/detail panel) switches language by rerunning its method.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            RebuildView(GetSelectedKey());
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Reload(GetSelectedKey());
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Navigating away while hovering may skip PointerExited; clear the leftover hover state so an empty check circle isn't still there on return.
        foreach (var account in _sourceItems)
        {
            account.IsPointerOver = false;
        }
    }

    private void Reload(string? selectKey = null)
    {
        _sourceItems = AppState.LoginService.GetCachedLoginAccounts();
        RebuildView(selectKey);
    }

    private void RebuildView(string? selectKey)
    {
        var source = _sourceItems;
        var filter = CachedSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? source
            : source.Where(account => Matches(account, filter)).ToList();

        // The batch check set survives rebuilds by CacheKey: first drop accounts that no longer exist, then apply the check state to the (possibly new) instances.
        var liveKeys = source.Select(account => account.CacheKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _checkedKeys.IntersectWith(liveKeys);
        foreach (var account in source)
        {
            account.IsSelected = _checkedKeys.Contains(account.CacheKey);
        }

        // Remember the key of the single selected account (detail focus) and restore it after the rebuild: a delayed action like refresh should not lose the account being viewed.
        var activeKey = !string.IsNullOrWhiteSpace(selectKey)
            ? selectKey
            : CachedAccountList.SelectedItem is CachedSteamLoginAccount current ? current.CacheKey : null;

        _viewItems.Clear();
        foreach (var account in filtered)
        {
            _viewItems.Add(account);
        }

        var active = activeKey is null
            ? null
            : _viewItems.FirstOrDefault(account =>
                string.Equals(account.CacheKey, activeKey, StringComparison.OrdinalIgnoreCase));
        // With nothing to select (or the old selection filtered out/deleted) leave it unselected: don't fall back to the first item,
        // otherwise the detail panel shows the first account's avatar and profile before the user has clicked anything.
        CachedAccountList.SelectedItem = active;

        var hasAny = source.Count > 0;
        CachedEmptyPanel.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CachedEmptyText.Text = hasAny ? Loc.T("Cached_Empty_NoMatch") : Loc.T("Cached_Empty_None");
        CachedEmptyHintText.Text = hasAny
            ? Loc.T("Cached_Empty_NoMatch_Hint")
            : Loc.T("Cached_Empty_None_Hint");
        CachedSummaryText.Text = hasAny
            ? Loc.Tf("Cached_Summary_Count_Format", source.Count)
            : Loc.T("Cached_Summary_Empty");

        UpdateBatchBar();
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private static bool Matches(CachedSteamLoginAccount account, string filter)
    {
        return Contains(account.AccountName, filter) ||
            Contains(account.PersonaName, filter) ||
            Contains(account.SteamId, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrEmpty(value) &&
            value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void CachedSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildView(GetSelectedKey());
        }
    }

    private async void RefreshCachedButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetBusy(true);
        AppState.ShowStatus(Loc.T("Cached_Refresh_Progress"), InfoBarSeverity.Informational);

        try
        {
            var refreshed = await AppState.LoginService.RefreshCachedLoginProfilesAsync(_sourceItems);
            Reload(GetSelectedKey());
            if (refreshed > 0)
            {
                AppState.ShowStatus(Loc.Tf("Cached_Refresh_Success_Format", refreshed), InfoBarSeverity.Success);
            }
            else
            {
                // If nothing synced (offline/rate limited/no accounts), don't show a green success bar as if all is fine.
                AppState.ShowStatus(Loc.T("Cached_Refresh_None"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Reload(GetSelectedKey());
            AppState.ShowStatus(Loc.Tf("Cached_Refresh_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void CachedAccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetail();
        UpdateControlsEnabled();
    }

    private async void RestoreSelectedCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            AppState.ShowStatus(Loc.T("Cached_Restore_NoSelection"), InfoBarSeverity.Error);
            return;
        }

        // On first run, detect and save the Steam path; if it stops working, detect again; only if both fail ask the user to pick it. Cancelling aborts the restore.
        if (!await SteamPathCoordinator.EnsureResolvedAsync())
        {
            AppState.ShowStatus(Loc.T("SteamPath_Status_Required"), InfoBarSeverity.Warning);
            return;
        }

        AppState.SetBusy(true);
        AppState.ShowStatus(Loc.Tf("Cached_Restore_Progress_Format", account.AccountTitle), InfoBarSeverity.Informational);

        var progress = new Progress<string>(message =>
            AppState.ShowStatus(message, InfoBarSeverity.Informational));

        try
        {
            var restored = await Task.Run(() => AppState.LoginService.RestoreCachedLogin(account, progress));
            Reload(restored.CacheKey);
            AppState.ShowStatus(
                Loc.Tf("Cached_Restore_Success_Format", restored.AccountName, restored.SteamId),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("Restoring the cached account failed.", ex);
            AppState.ShowStatus(Loc.Tf("Cached_Restore_Fail_Format", ex.Message, AppLog.LogFilePath), InfoBarSeverity.Error);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private async void ClearCachedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (_sourceItems.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Cached_Clear_None"), InfoBarSeverity.Error);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Loc.T("Cached_Clear_Dialog_Title"),
            Content = Loc.Tf("Cached_Clear_Dialog_Content_Format", _sourceItems.Count),
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

            _checkedKeys.Clear();
            var cleared = AppState.LoginService.ClearCachedLoginAccounts();
            Reload();
            AppState.ShowStatus(Loc.Tf("Cached_Clear_Success_Format", cleared), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Cached_Clear_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    // ---------- Card hover / top left check (same as the history page) ----------

    private static CachedSteamLoginAccount? CardItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CachedSteamLoginAccount;

    private void CachedCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = true;
        }
    }

    private void CachedCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            account.IsPointerOver = false;
        }
    }

    private void CachedCardCheck_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is not { } account)
        {
            return;
        }

        var key = account.CacheKey;
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
        UpdateControlsEnabled();
    }

    private async void CachedCardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CardItem(sender) is { } account)
        {
            await DeleteAccountsWithConfirmAsync(new[] { account });
        }
    }

    // ---------- Bottom batch action bar (appears once any card is checked, acts on all checked accounts) ----------

    // Only acts on the currently visible (filtered) list, so a batch delete never hits checked items hidden by the search filter.
    // Checked items hidden by the filter stay in _checkedKeys and reappear and count again once the search is cleared.
    private List<CachedSteamLoginAccount> GetCheckedAccounts() =>
        _viewItems.Where(account => _checkedKeys.Contains(account.CacheKey)).ToList();

    private void UpdateBatchBar()
    {
        var count = GetCheckedAccounts().Count;
        BatchActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = Loc.Tf("Common_Selected_Format", count);
    }

    private void ClearCheckedSelection()
    {
        _checkedKeys.Clear();
        foreach (var account in _sourceItems)
        {
            account.IsSelected = false;
        }

        UpdateBatchBar();
        UpdateControlsEnabled();
    }

    private void BatchClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCheckedSelection();
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteAccountsWithConfirmAsync(GetCheckedAccounts());
    }

    private async Task DeleteAccountsWithConfirmAsync(IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (_isDialogFlowActive)
        {
            return;
        }

        if (accounts.Count == 0)
        {
            AppState.ShowStatus(Loc.T("Cached_Delete_NoSelection"), InfoBarSeverity.Error);
            return;
        }

        var nameText = string.Join(", ", accounts.Take(5).Select(account => account.AccountTitle));
        var summary = accounts.Count > 5
            ? Loc.Tf("Cached_Delete_Dialog_Many_Format", nameText, accounts.Count)
            : Loc.Tf("Cached_Delete_Dialog_Few_Format", nameText, accounts.Count);

        var dialog = new ContentDialog
        {
            Title = Loc.T("Cached_Delete_Dialog_Title"),
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

            // Remove these keys from the batch check set before deleting, so they don't stay checked after the rebuild.
            foreach (var account in accounts)
            {
                _checkedKeys.Remove(account.CacheKey);
            }

            var removed = AppState.LoginService.DeleteCachedLoginAccounts(accounts);
            Reload();
            AppState.ShowStatus(Loc.Tf("Cached_Delete_Success_Format", removed), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppState.ShowStatus(Loc.Tf("Cached_Delete_Fail_Format", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _isDialogFlowActive = false;
        }
    }

    private string? GetSelectedKey()
    {
        return CachedAccountList.SelectedItem is CachedSteamLoginAccount account
            ? account.CacheKey
            : null;
    }

    private void UpdateDetail()
    {
        if (CachedDetailAccountNameText is null)
        {
            return;
        }

        if (CachedAccountList.SelectedItem is not CachedSteamLoginAccount account)
        {
            CachedDetailAvatar.ProfilePicture = null;
            // Only an empty string shows the default person silhouette: PersonPicture takes initials from DisplayName, so English text would show a letter block instead.
            CachedDetailAvatar.DisplayName = string.Empty;
            CachedDetailAccountNameText.Text = Loc.T("Cached_Detail_NoSelection");
            CachedDetailPersonaText.Text = Loc.T("Cached_Detail_PersonaUnsynced");
            CachedDetailSteamIdText.Text = Loc.T("Cached_Detail_SteamIdMissing");
            CachedDetailTimeText.Text = Loc.T("Cached_Detail_TimeNone");
            return;
        }

        CachedDetailAvatar.DisplayName = account.AccountTitle;
        CachedDetailAvatar.ProfilePicture = account.AvatarImage;
        CachedDetailAccountNameText.Text = account.AccountTitle;
        CachedDetailPersonaText.Text = account.PersonaDisplayName;
        CachedDetailSteamIdText.Text = account.SteamIdDisplay;
        CachedDetailTimeText.Text = Loc.Tf("Cached_Detail_Time_Format", account.CachedAtText);
    }

    private void UpdateControlsEnabled()
    {
        var isBusy = AppState.IsBusy;
        CachedAccountList.IsEnabled = !isBusy && _viewItems.Count > 0;
        CachedSearchBox.IsEnabled = !isBusy;
        RefreshCachedButton.IsEnabled = !isBusy;
        ClearCachedButton.IsEnabled = !isBusy && !_isDialogFlowActive && _sourceItems.Count > 0;
        RestoreSelectedCachedButton.IsEnabled = !isBusy && CachedAccountList.SelectedItem is not null;
        BatchClearButton.IsEnabled = !isBusy;
        BatchDeleteButton.IsEnabled = !isBusy && !_isDialogFlowActive;
    }
}
