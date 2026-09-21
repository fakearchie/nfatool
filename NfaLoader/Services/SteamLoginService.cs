using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamLoginService
{
    private readonly JwtTokenService _jwtTokenService = new();
    private readonly SteamCryptoService _steamCryptoService = new();
    private readonly SteamConfigService _steamConfigService = new();
    private readonly SteamProcessService _steamProcessService = new();
    private readonly SteamLoginCacheService _loginCacheService = new();
    private readonly AccountHistoryService _accountHistoryService = new();

    public LoginResult Login(string accountName, string eyaToken, IProgress<string>? progress = null)
    {
        AppLog.Info(
            $"==== Starting sign-in: account=\"{accountName}\"  OS={Environment.OSVersion}  64-bit process={Environment.Is64BitProcess} ====");
        try
        {
            progress?.Report(Loc.T("Steam_Progress_ValidatingToken"));
            var token = _jwtTokenService.Validate(eyaToken);
            AppLog.Info($"EYA token validated: SteamID={token.SteamId} expires={token.ExpiresAt:yyyy-MM-dd HH:mm:ss}");

            progress?.Report(Loc.T("Steam_Progress_LocatingInstall"));
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();
            var cachedAccountCandidates = _steamConfigService.GetLoginAccounts(paths);

            progress?.Report(Loc.T("Steam_Progress_EncryptingToken"));
            var encryptedJwt = _steamCryptoService.EncryptToHex(eyaToken, accountName);
            var accountCrc32 = Crc32.ComputeSteamAccountKey(accountName);
            AppLog.Info($"Token encrypted ({encryptedJwt.Length} hex chars); ConnectCache key={accountCrc32}");

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report(Loc.T("Steam_Progress_WritingConfig"));
            if (cachedAccountCandidates.Count == 0)
            {
                cachedAccountCandidates = _steamConfigService.GetLoginAccounts(paths);
            }

            _loginCacheService.MarkEyaLogin(accountName, token.SteamId);
            CacheLoginAccounts(cachedAccountCandidates, accountName, token.SteamId);
            _steamConfigService.UpdateLoginFiles(
                paths,
                accountName,
                token.SteamId,
                encryptedJwt,
                accountCrc32);

            progress?.Report(Loc.T("Steam_Progress_StartingSteam"));
            _steamProcessService.LaunchSteamWithLogin(paths, accountName);

            TryPushCs2Cloud(paths, token.SteamId, progress);

            AppLog.Info("==== Sign-in finished (Steam launch requested) ====");
            return new LoginResult(accountName, token.SteamId, token.ExpiresAt);
        }
        catch (Exception ex)
        {
            AppLog.Error("Sign-in failed.", ex);
            throw;
        }
    }

    public IReadOnlyList<CachedSteamLoginAccount> GetCachedLoginAccounts()
    {
        return _loginCacheService.LoadAll()
            .Where(account => !IsKnownEyaAccount(account))
            .ToList();
    }

    public CachedSteamLoginAccount RestoreCachedLogin(
        CachedSteamLoginAccount account,
        IProgress<string>? progress = null)
    {
        AppLog.Info("==== Starting restore of cached Steam account ====");

        try
        {
            progress?.Report(Loc.T("Steam_Progress_LocatingInstall"));
            var paths = SteamPathCoordinator.ResolvePathsOrThrow();

            _steamProcessService.EnsureSteamStopped(paths, progress);

            progress?.Report(Loc.T("Steam_Progress_RestoringConfig"));
            _steamConfigService.RestoreLoginFiles(paths, account);

            progress?.Report(Loc.T("Steam_Progress_StartingSteam"));
            _steamProcessService.LaunchSteamWithLogin(paths, account.AccountName);

            TryPushCs2Cloud(paths, account.SteamId, progress);

            AppLog.Info($"==== Requested restore of cached account: {account.AccountName} ({account.SteamId}) ====");
            return account;
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to restore cached Steam account.", ex);
            throw;
        }
    }

    public int DeleteCachedLoginAccounts(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        return _loginCacheService.Delete(accounts);
    }

    public int ClearCachedLoginAccounts()
    {
        return _loginCacheService.ClearAll();
    }

    public Task<int> RefreshCachedLoginProfilesAsync(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        return _loginCacheService.RefreshProfilesAsync(accounts);
    }

    // If "CS2 settings sync" is on, force-push the source account's CS2 config to the target account's Steam Cloud after sign-in.
    // Steam has only just started and is not signed in yet, so ForcePush retries and waits internally; it runs in the background so the sign-in returns right away, and failures are only logged.
    private void TryPushCs2Cloud(SteamPaths paths, string targetSteamId, IProgress<string>? progress)
    {
        try
        {
            // Use the global singletons directly (not captured fields): AppState.LoginService is the first object created in AppState's static initializer,
            // when SettingsService/Cs2CloudService are not assigned yet, so a field initializer would capture null; here they are read at actual sign-in time,
            // long after static initialization has finished. Sharing the singletons still serializes settings.json reads and writes with saves from the settings page (instance-level lock).
            var settings = AppState.SettingsService.Load();
            if (!settings.Cs2SyncOnLogin || string.IsNullOrWhiteSpace(settings.Cs2SyncSourceSteamId))
            {
                return;
            }

            var source = settings.Cs2SyncSourceSteamId;
            _ = Task.Run(() => AppState.Cs2CloudService.PushSourceForLogin(paths, source, targetSteamId, progress));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to schedule the CS2 cloud push, ignoring: {ex.Message}");
        }
    }

    private void CacheLoginAccounts(
        IReadOnlyList<CachedSteamLoginAccount> accounts,
        string nextAccountName,
        string nextSteamId)
    {
        var filtered = accounts
            .Where(account =>
                !string.Equals(account.AccountName, nextAccountName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(account.SteamId, nextSteamId, StringComparison.OrdinalIgnoreCase) &&
                !IsKnownEyaAccount(account))
            .ToList();

        if (filtered.Count == 0)
        {
            return;
        }

        var saved = _loginCacheService.SaveMany(filtered);
        AppLog.Info($"Cached {saved.Count} non-EYA Steam account(s) for restore.");
        StartCachedProfileRefresh(saved);
    }

    private void StartCachedProfileRefresh(IReadOnlyCollection<CachedSteamLoginAccount> accounts)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await _loginCacheService.RefreshProfilesAsync(accounts);
                AppLog.Info($"Updated cached Steam profile data for {updated} account(s).");
                if (updated > 0)
                {
                    // Tell the cached accounts page to reload: otherwise the refresh is saved to disk but an open page still shows the old "not synced" snapshot.
                    AppState.NotifyCachedLoginAccountsRefreshed();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to sync cached account avatars: {ex.Message}");
            }
        });
    }

    private bool IsKnownEyaAccount(CachedSteamLoginAccount account)
    {
        try
        {
            if (_loginCacheService.IsEyaLogin(account))
            {
                return true;
            }

            return _accountHistoryService.Load().Any(historyAccount =>
                string.Equals(historyAccount.SteamId, account.SteamId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(historyAccount.AccountName, account.AccountName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
