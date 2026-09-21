using Microsoft.UI.Xaml.Controls;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>
/// App-wide shared state: service singletons, the history account cache, the global busy state and update check coordination.
/// Pages subscribe to state changes through events (every page is a long-lived NavigationCacheMode=Required instance, so no unsubscribing).
/// </summary>
internal static class AppState
{
    public static SteamLoginService LoginService { get; } = new();
    public static SteamWorkshopService WorkshopService { get; } = new();
    public static NfaPubClient NfaClient { get; } = new();
    public static JwtTokenService JwtTokenService { get; } = new();
    public static SteamTokenOnlineValidationService TokenOnlineValidationService { get; } = new();
    public static AccountHistoryService AccountHistoryService { get; } = new();
    public static CsPremierScoreService PremierScoreService { get; } = new();
    public static CsLoadoutService LoadoutService { get; } = new();
    public static SteamProfileService ProfileService { get; } = new();
    public static SteamCredentialsAuthService CredentialsAuthService { get; } = new();
    public static GitHubUpdateService UpdateService { get; } = new();
    public static UpdateInstallerService UpdateInstallerService { get; } = new();
    public static SettingsService SettingsService { get; } = new();
    public static Cs2CloudService Cs2CloudService { get; } = new();

    // ---------- nfa.pub API key ----------

    /// <summary>The key was saved or removed. Pages that show the missing-key warning listen for it.</summary>
    public static event Action? NfaApiKeyChanged;

    public static string? GetNfaApiKey() =>
        SecretProtector.TryUnprotect(SettingsService.Load().NfaApiKeyProtected);

    public static bool HasNfaApiKey => !string.IsNullOrWhiteSpace(GetNfaApiKey());

    public static NfaPendingPurchase? GetPendingPurchase()
    {
        var settings = SettingsService.Load();
        return settings.NfaPendingKey is { Length: > 0 } key &&
               settings.NfaPendingType is { Length: > 0 } type &&
               settings.NfaPendingStartedAt is { } startedAt
            ? new NfaPendingPurchase(
                settings.NfaPendingProductId ?? type,
                type,
                settings.NfaPendingName ?? type,
                key,
                startedAt,
                SecretProtector.TryUnprotect(settings.NfaPendingApiKeyProtected))
            : null;
    }

    /// <summary>
    /// Records a purchase about to be sent, with a fresh idempotency key. Returns null when the record did not reach
    /// the disk. The replace is atomic, so a failed save leaves no record behind, and a key held only in memory could
    /// not be replayed after a restart: the caller must not send the purchase at all.
    /// </summary>
    public static NfaPendingPurchase? StartPendingPurchase(string productId, string type, string name, string apiKey)
    {
        var pending = new NfaPendingPurchase(productId, type, name, Guid.NewGuid().ToString("N"), DateTimeOffset.Now, apiKey);
        var settings = SettingsService.Load();
        settings.NfaPendingProductId = pending.ProductId;
        settings.NfaPendingType = pending.Type;
        settings.NfaPendingName = pending.Name;
        settings.NfaPendingKey = pending.Key;
        settings.NfaPendingStartedAt = pending.StartedAt;
        settings.NfaPendingApiKeyProtected = SecretProtector.Protect(apiKey);
        return SettingsService.Save(settings) ? pending : null;
    }

    public static void ClearPendingPurchase()
    {
        var settings = SettingsService.Load();
        settings.NfaPendingProductId = null;
        settings.NfaPendingType = null;
        settings.NfaPendingName = null;
        settings.NfaPendingKey = null;
        settings.NfaPendingStartedAt = null;
        settings.NfaPendingApiKeyProtected = null;
        SettingsService.Save(settings);

        if (GetPendingPurchase() is not null)
        {
            AppLog.Warn("A finished purchase could not be cleared from settings.json; the next Buy will replay it instead of buying.");
        }
    }

    /// <summary>Stores the key encrypted, or clears it when <paramref name="apiKey"/> is null or blank.</summary>
    public static void SaveNfaApiKey(string? apiKey)
    {
        var settings = SettingsService.Load();
        settings.NfaApiKeyProtected = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : SecretProtector.Protect(apiKey.Trim());
        SettingsService.Save(settings);
        NfaApiKeyChanged?.Invoke();
    }

    /// <summary>Set by MainWindow, writes messages to the global status bar.</summary>
    public static Action<string, InfoBarSeverity>? StatusReporter { get; set; }

    /// <summary>
    /// The background profile refresh for cached accounts, started by a sign-in, has been saved (SteamLoginService.StartCachedProfileRefresh finished with changes).
    /// May fire on a background thread; subscribers switch back to the UI thread themselves. The cached accounts page subscribes to reload itself, otherwise opening it right after a sign-in
    /// would stay stuck on the old "not synced" snapshot.
    /// </summary>
    public static event Action? CachedLoginAccountsRefreshed;

    public static void NotifyCachedLoginAccountsRefreshed()
    {
        CachedLoginAccountsRefreshed?.Invoke();
    }

    /// <summary>The long-lived login page instance, so the history page can reuse its one-click lookup flow (same linked behaviour as the old version).</summary>
    public static Pages.LoginPage? LoginPage { get; set; }

    public static void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusReporter?.Invoke(message, severity);
    }

    // ---------- Global busy state ----------

    public static bool IsBusy { get; private set; }

    public static event Action<bool>? BusyChanged;

    public static void SetBusy(bool isBusy)
    {
        if (IsBusy == isBusy)
        {
            return;
        }

        IsBusy = isBusy;
        BusyChanged?.Invoke(isBusy);
    }

    // Cancellation source for the current busy operation. Only the UI thread touches it (pages start, cancel and end long flows on the UI thread),
    // so no extra locking is needed.
    private static CancellationTokenSource? _busyCts;

    /// <summary>Starts a cancellable busy operation: sets busy, creates a new CTS and returns its Token for the long task.</summary>
    public static CancellationToken BeginBusyOperation()
    {
        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        SetBusy(true);
        return _busyCts.Token;
    }

    /// <summary>Whether the running busy operation can be cancelled. Operations started with SetBusy alone cannot.</summary>
    public static bool CanCancelBusyOperation => _busyCts is not null;

    /// <summary>Cancels the current busy operation; a no-op when nothing is running (called by the Cancel button).</summary>
    public static void CancelBusyOperation()
    {
        _busyCts?.Cancel();
    }

    /// <summary>Ends the busy operation: clears busy and disposes the CTS. Idempotent, safe to call from finally.</summary>
    public static void EndBusyOperation()
    {
        SetBusy(false);
        _busyCts?.Dispose();
        _busyCts = null;
    }

    // ---------- History account cache ----------

    public static IReadOnlyList<SteamAccountHistoryItem> HistoryAccounts { get; private set; } = [];

    /// <summary>
    /// The SteamID the history page should select. The history page loads lazily, so a selection request sent during sign-in or lookup
    /// has no subscriber before the page is first built; it is kept here for the page to pick up when it is constructed.
    /// </summary>
    public static string? PendingHistorySelection { get; set; }

    /// <summary>History accounts were reloaded; the argument is the SteamID to select (null keeps the current selection).</summary>
    public static event Action<string?>? HistoryChanged;

    public static void ReloadHistory(string? selectSteamId = null)
    {
        try
        {
            HistoryAccounts = AccountHistoryService.Load();
        }
        catch (Exception ex)
        {
            HistoryAccounts = [];
            ShowStatus(Loc.Tf("AppState_HistoryLoadFailed_Format", ex.Message), InfoBarSeverity.Warning);
        }

        if (!string.IsNullOrWhiteSpace(selectSteamId))
        {
            PendingHistorySelection = selectSteamId;
        }

        HistoryChanged?.Invoke(selectSteamId);
    }

    public static SteamAccountHistoryItem? FindHistoryAccount(string? steamId)
    {
        return string.IsNullOrWhiteSpace(steamId)
            ? null
            : HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Update check coordination ----------

    public static GitHubUpdateInfo? LatestUpdate { get; private set; }

    public static bool IsCheckingForUpdates { get; private set; }

    public static string? UpdateCheckError { get; private set; }

    /// <summary>The release manifest returned 404: nothing has been published yet.</summary>
    public static bool UpdateHasNoReleases { get; private set; }

    public static DateTimeOffset? UpdateCheckedAt { get; private set; }

    public static event Action? UpdateStateChanged;

    public static async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStateChanged?.Invoke();

        if (!isAutomatic)
        {
            ShowStatus(Loc.T("AppState_Update_Checking"), InfoBarSeverity.Informational);
        }

        try
        {
            var update = await UpdateService.CheckLatestAsync();
            LatestUpdate = update;
            UpdateCheckError = null;
            UpdateCheckedAt = update.CheckedAt;

            // When the automatic check finds a new version it only lights the red dot on the "About" nav item (UpdateStateChanged → MainWindow.RefreshUpdateBadge),
            // with no permanent banner taking up the bottom of every page; only a manual check reports the result in the status bar.
            if (update.IsUpdateAvailable)
            {
                if (!isAutomatic)
                {
                    ShowStatus(Loc.Tf("AppState_Update_Available_Format", update.LatestTag), InfoBarSeverity.Warning);
                }
            }
            else if (!isAutomatic)
            {
                ShowStatus(Loc.Tf("AppState_Update_UpToDate_Format", update.LatestTag), InfoBarSeverity.Success);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            UpdateHasNoReleases = true;
            UpdateCheckError = Loc.T("About_Update_NoReleases");
            UpdateCheckedAt = DateTimeOffset.Now;

            if (!isAutomatic)
            {
                ShowStatus(UpdateCheckError, InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            UpdateHasNoReleases = false;
            UpdateCheckError = ex.Message;
            UpdateCheckedAt = DateTimeOffset.Now;

            if (!isAutomatic)
            {
                ShowStatus(Loc.Tf("AppState_Update_CheckFailed_Format", ex.Message), InfoBarSeverity.Error);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateStateChanged?.Invoke();
        }
    }

    public static async Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowStatus(Loc.T("AppState_Url_Invalid"), InfoBarSeverity.Error);
            return;
        }

        var opened = await Windows.System.Launcher.LaunchUriAsync(uri);
        ShowStatus(
            opened ? Loc.T("AppState_Url_Opened") : Loc.T("AppState_Url_OpenFailed"),
            opened ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
}
