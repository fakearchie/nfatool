using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class AccountHistoryService
{
    private const string AppFolderName = "nfa.pub Loader";
    private const string HistoryFolderName = "history";
    private const string HistoryFileName = "accounts.json";
    private const string AvatarFolderName = "avatars";

    // Concurrency cap for RefreshProfilesAsync batch fetches, so importing hundreds of accounts does not open hundreds of connections at once and trigger 429.
    private const int MaxProfileFetchConcurrency = 4;

    private static readonly HttpClient DefaultHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    // Serializes every "read, modify, rewrite whole file" critical section. The async path (SaveLoginAsync) uses await WaitAsync so the UI thread is not blocked;
    // the sync paths (SaveCsAccountStatus/ImportAccounts/DeleteAccounts/ClearAll) use Wait. Their critical sections contain no await,
    // so the holder always finishes synchronously and releases, and Wait on the UI thread cannot deadlock itself.
    // This is a different lock from the concurrency-limiting SemaphoreSlim in RefreshProfilesAsync; do not mix them up.
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    public AccountHistoryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AppFolderPath = Path.Combine(appData, AppFolderName);
        HistoryFolderPath = Path.Combine(AppFolderPath, HistoryFolderName);
        HistoryFilePath = Path.Combine(HistoryFolderPath, HistoryFileName);
        AvatarFolderPath = Path.Combine(AppFolderPath, AvatarFolderName);
    }

    public string AppFolderPath { get; }

    public string HistoryFolderPath { get; }

    public string HistoryFilePath { get; }

    public string AvatarFolderPath { get; }

    public IReadOnlyList<SteamAccountHistoryItem> Load()
    {
        var document = ReadDocument();
        return NormalizeAccounts(document.Accounts);
    }

    public async Task<SteamAccountHistoryItem?> GetProfilePreviewAsync(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt)
    {
        var profile = await TryGetSteamProfileAsync(steamId);
        if (profile is null)
        {
            return null;
        }

        var avatarPath = !string.IsNullOrWhiteSpace(profile.AvatarUrl)
            ? await TryDownloadAvatarAsync(steamId, accountName, profile.AvatarUrl)
            : null;

        return new SteamAccountHistoryItem
        {
            AccountName = accountName,
            SteamId = steamId,
            EyaToken = eyaToken,
            TokenExpiresAt = tokenExpiresAt,
            PersonaName = profile.PersonaName,
            AvatarUrl = profile.AvatarUrl,
            AvatarPath = avatarPath
        };
    }

    public async Task SaveLoginAsync(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt,
        string? prefetchedPersonaName = null,
        string? prefetchedAvatarUrl = null,
        string? prefetchedAvatarPath = null)
    {
        accountName = accountName.Trim();
        steamId = steamId.Trim();
        eyaToken = eyaToken.Trim();

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException(Loc.T("Account_Error_AccountNameEmpty"), nameof(accountName));
        }

        if (string.IsNullOrWhiteSpace(eyaToken))
        {
            throw new ArgumentException(Loc.T("Account_Error_EyaTokenEmpty"), nameof(eyaToken));
        }

        // Passing prefetched data skips the matching network fetch.
        var needPersona = prefetchedPersonaName is null;
        var needAvatar = prefetchedAvatarPath is null;
        // With an avatar URL already in hand (prefetched or fetched later), there is no need to fetch the profile just for the URL;
        // but when needAvatar is set and there is no URL, the profile is still fetched for AvatarUrl, or the avatar never gets filled in (accounts whose download failed before).
        var avatarUrl = string.IsNullOrWhiteSpace(prefetchedAvatarUrl) ? null : prefetchedAvatarUrl;
        var needProfileFetch = needPersona || (needAvatar && avatarUrl is null);
        var skipNetwork = !needProfileFetch && !needAvatar;

        // Critical section 1: read, modify and write the base record, merging any prefetched profile/avatar fields.
        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, steamId, accountName);
            if (item is null)
            {
                item = new SteamAccountHistoryItem();
                document.Accounts.Add(item);
            }

            item.AccountName = accountName;
            item.SteamId = steamId;
            SetToken(item, eyaToken);
            item.TokenExpiresAt = tokenExpiresAt;
            item.LastLoginAt = DateTimeOffset.Now;

            if (!string.IsNullOrWhiteSpace(prefetchedPersonaName))
            {
                item.PersonaName = prefetchedPersonaName;
            }

            // Save a prefetched URL too: AvatarImage falls back to it when the local avatar file is deleted or lost.
            if (avatarUrl is not null)
            {
                item.AvatarUrl = avatarUrl;
            }

            if (!string.IsNullOrWhiteSpace(prefetchedAvatarPath))
            {
                item.AvatarPath = prefetchedAvatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }

        if (skipNetwork)
        {
            return;
        }

        // Network requests run outside the lock so the file lock is not held for seconds; a background RefreshProfilesAsync can still write in the meantime.
        var profile = needProfileFetch ? await TryGetSteamProfileAsync(steamId) : null;
        string? personaName = needPersona ? profile?.PersonaName : prefetchedPersonaName;
        if (avatarUrl is null)
        {
            avatarUrl = profile?.AvatarUrl;
        }

        string? avatarPath = prefetchedAvatarPath;
        if (needAvatar && !string.IsNullOrWhiteSpace(avatarUrl))
        {
            avatarPath = await TryDownloadAvatarAsync(steamId, accountName, avatarUrl);
        }

        var hasPersona = !string.IsNullOrWhiteSpace(personaName);
        var hasAvatarUrl = !string.IsNullOrWhiteSpace(avatarUrl);
        var hasAvatarPath = !string.IsNullOrWhiteSpace(avatarPath);
        if (!hasPersona && !hasAvatarUrl && !hasAvatarPath)
        {
            return;
        }

        // Critical section 2: re-read from disk, find the entry again by account key and merge only profile/avatar fields, so a stale document does not roll back other writes.
        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, steamId, accountName);
            if (item is null)
            {
                return;
            }

            if (hasPersona)
            {
                item.PersonaName = personaName;
            }

            if (hasAvatarUrl)
            {
                item.AvatarUrl = avatarUrl;
            }

            if (hasAvatarPath)
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    // personaName/avatarUrl/avatarPath: data the query flow already fetched along the way. Only non-blank values overwrite (same merge rules as SaveLoginAsync).
    // Leaving them out keeps stored data as is. Otherwise accounts that were only queried and never signed in would never get a nickname or avatar (fetched, thrown away, and fetched again on every query).
    /// <summary>
    /// Saves an account bought from nfa.pub, or received as a replacement, with its order. The delivery time and the
    /// replacements left belong to the whole order, so they are copied onto every account from that order.
    /// </summary>
    // Whether Steam accepted a token says nothing about the next one, so a new token starts unchecked.
    private static void SetToken(SteamAccountHistoryItem item, string token)
    {
        if (!string.Equals(item.EyaToken, token, StringComparison.Ordinal))
        {
            item.JwtAvailable = null;
            item.JwtStatus = null;
            item.JwtValidatedAt = null;
        }

        item.EyaToken = token;
    }

    public void SaveNfaAccount(
        string steamId,
        string token,
        string order,
        DateTimeOffset? orderDeliveredAt,
        int? replacementsLeft,
        string? replacedSteamId = null)
    {
        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, steamId, steamId);
            var isNew = item is null;
            if (item is null)
            {
                item = new SteamAccountHistoryItem { AccountName = steamId };
                document.Accounts.Add(item);
            }

            // Only a new account or a new token moves it to the top; recording order details alone does not.
            if (isNew || !string.Equals(item.EyaToken, token, StringComparison.Ordinal))
            {
                item.LastLoginAt = DateTimeOffset.Now;
            }

            item.SteamId = steamId;
            SetToken(item, token);
            item.TokenExpiresAt = AppState.JwtTokenService.Inspect(token).ExpiresAt;

            if (!string.IsNullOrWhiteSpace(order))
            {
                item.NfaOrder = order;
                foreach (var sibling in document.Accounts.Where(a => string.Equals(a.NfaOrder, order, StringComparison.Ordinal)))
                {
                    if (orderDeliveredAt is { } delivered)
                    {
                        sibling.NfaOrderDeliveredAt ??= delivered;
                    }

                    if (replacementsLeft is { } left)
                    {
                        sibling.NfaReplacementsLeft = left;
                    }
                }
            }

            // nfa.pub swaps a Steam ID only once, so the account this one replaced is marked as finished.
            if (!string.IsNullOrWhiteSpace(replacedSteamId) &&
                FindExisting(document.Accounts, replacedSteamId, replacedSteamId) is { } replaced &&
                !ReferenceEquals(replaced, item))
            {
                replaced.NfaReplacedBy = steamId;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public void SaveCsAccountStatus(
        string accountName,
        string steamId,
        string eyaToken,
        DateTimeOffset? tokenExpiresAt,
        CsPremierScoreResult score,
        SteamTokenOnlineValidationResult jwtValidation,
        string? personaName = null,
        string? avatarUrl = null,
        string? avatarPath = null)
    {
        accountName = accountName.Trim();
        steamId = steamId.Trim();
        eyaToken = eyaToken.Trim();

        if (string.IsNullOrWhiteSpace(accountName) ||
            string.IsNullOrWhiteSpace(steamId) ||
            string.IsNullOrWhiteSpace(eyaToken))
        {
            return;
        }

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, steamId, accountName);
            if (item is null)
            {
                item = new SteamAccountHistoryItem
                {
                    AccountName = accountName,
                    SteamId = steamId,
                    EyaToken = eyaToken,
                    TokenExpiresAt = tokenExpiresAt
                };
                document.Accounts.Add(item);
            }

            item.AccountName = accountName;
            item.SteamId = steamId;
            item.EyaToken = eyaToken;
            item.TokenExpiresAt = tokenExpiresAt;
            item.CompetitiveScore = score.DisplayText;
            item.AccountStatus = score.StatusText;
            item.JwtAvailable = jwtValidation.IsValid;
            item.JwtStatus = jwtValidation.IsValid ? "Valid" : "Invalid";
            item.JwtValidatedAt = DateTimeOffset.Now;
            item.PremierScore = score.PremierRanking is null ? null : checked((int)score.PremierRanking.RankId);
            item.PremierWins = score.PremierRanking is null ? null : checked((int)score.PremierRanking.Wins);
            item.PremierScoreUpdatedAt = DateTimeOffset.Now;
            item.CooldownSeconds = score.PenaltySeconds;
            item.CooldownReason = score.PenaltyReason;
            item.GcVacBanned = score.GcVacBannedOrUnknown;
            item.CsPlayerLevel = score.PlayerLevel;
            item.InCsMatch = score.InMatch;
            item.CsStatusUpdatedAt = DateTimeOffset.Now;

            if (!string.IsNullOrWhiteSpace(personaName))
            {
                item.PersonaName = personaName;
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                item.AvatarUrl = avatarUrl;
            }

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>
    /// After personalization succeeds, writes the known new nickname/avatar straight to the local record instead of fetching again:
    /// the community profile endpoint is edge cached, so fetching right after a rename can still return the old value and "roll back" the record.
    /// When <paramref name="avatarImageSourcePath"/> is set, copies that image into the avatar folder and updates AvatarPath.
    /// </summary>
    public void UpdateProfileLocally(string steamId, string? personaName, string? avatarImageSourcePath)
    {
        steamId = steamId.Trim();
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        string? avatarPath = null;
        if (!string.IsNullOrWhiteSpace(avatarImageSourcePath) && File.Exists(avatarImageSourcePath))
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(AvatarFolderPath);
                avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId, steamId)}.jpg");
                tempPath = avatarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(avatarImageSourcePath, tempPath, overwrite: true);
                File.Move(tempPath, avatarPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Failed to copy the personalization avatar into the avatar folder: {ex.Message}");
                TryDeleteFile(tempPath);
                avatarPath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(personaName) && avatarPath is null)
        {
            return;
        }

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = document.Accounts.FirstOrDefault(account =>
                string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(personaName))
            {
                item.PersonaName = personaName.Trim();
            }

            if (avatarPath is not null)
            {
                item.AvatarPath = avatarPath;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Sets the account note (trimmed; blank clears it). Rewrites the entry on disk in place and leaves other fields unchanged.</summary>
    public void SetNote(SteamAccountHistoryItem account, string? note)
    {
        var normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var item = FindExisting(document.Accounts, account.SteamId, account.AccountName);
            if (item is null)
            {
                return;
            }

            item.Note = normalized;
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Adds the selected accounts to a group, or removes them from it, in bulk. Returns the number of accounts actually changed.</summary>
    public int SetGroupMembership(
        IReadOnlyCollection<SteamAccountHistoryItem> accounts, string groupId, bool isMember)
    {
        if (accounts.Count == 0 || string.IsNullOrWhiteSpace(groupId))
        {
            return 0;
        }

        var keys = accounts.Select(GetAccountKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = 0;

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            foreach (var item in document.Accounts)
            {
                if (!keys.Contains(GetAccountKey(item)))
                {
                    continue;
                }

                item.GroupIds ??= [];
                if (isMember)
                {
                    if (!item.GroupIds.Contains(groupId))
                    {
                        item.GroupIds.Add(groupId);
                        changed++;
                    }
                }
                else if (item.GroupIds.Remove(groupId))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }

            return changed;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>When a group is deleted, removes its ID from every account (cascade cleanup). Returns the number of accounts affected.</summary>
    public int RemoveGroupFromAllAccounts(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return 0;
        }

        var changed = 0;

        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            foreach (var item in document.Accounts)
            {
                if (item.GroupIds is { Count: > 0 } && item.GroupIds.Remove(groupId))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }

            return changed;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Merges imported accounts (deduplicated and overwritten by SteamID/account name). Importing does not update "last sign-in".</summary>
    public (int Added, int Updated) ImportAccounts(IReadOnlyList<AccountImportEntry> entries)
    {
        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            var added = 0;
            var updated = 0;

            foreach (var entry in entries)
            {
                var accountName = entry.AccountName.Trim();
                var steamId = entry.SteamId.Trim();
                var eyaToken = entry.EyaToken.Trim();
                if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(eyaToken))
                {
                    continue;
                }

                var item = FindExisting(document.Accounts, steamId, accountName);
                if (item is null)
                {
                    item = new SteamAccountHistoryItem();
                    document.Accounts.Add(item);
                    added++;
                }
                else
                {
                    updated++;
                }

                item.AccountName = accountName;
                item.SteamId = steamId;
                SetToken(item, eyaToken);
                item.TokenExpiresAt = entry.TokenExpiresAt;
            }

            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
            return (added, updated);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Fills in nicknames and avatars in bulk (network failures are skipped per account). Returns the number of accounts updated.</summary>
    public async Task<int> RefreshProfilesAsync(IReadOnlyCollection<string> steamIds)
    {
        var distinctIds = steamIds
            .Where(steamId => !string.IsNullOrWhiteSpace(steamId))
            .Select(steamId => steamId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctIds.Count == 0)
        {
            return 0;
        }

        // Snapshot each account's current (PersonaName, AvatarUrl) before fetching: a batch fetch can run for minutes, and meanwhile personalization or the post-sign-in fallback refresh
        // may have written newer data. When merging, a field that no longer matches its snapshot (a concurrent write happened) is not overwritten,
        // so data fetched minutes ago does not "roll back" the newer value (optimistic concurrency check).
        var baseline = new Dictionary<string, (string? PersonaName, string? AvatarUrl)>(StringComparer.OrdinalIgnoreCase);
        await _fileGate.WaitAsync();
        try
        {
            foreach (var account in ReadDocument().Accounts)
            {
                baseline[account.SteamId] = (account.PersonaName, account.AvatarUrl);
            }
        }
        finally
        {
            _fileGate.Release();
        }

        // Limit concurrency to 4: this lock only throttles network fetches and is a different lock from the file mutex _fileGate; do not mix them up.
        using var fetchThrottle = new SemaphoreSlim(MaxProfileFetchConcurrency, MaxProfileFetchConcurrency);
        var results = await Task.WhenAll(distinctIds.Select(async steamId =>
        {
            await fetchThrottle.WaitAsync();
            try
            {
                var profile = await TryGetSteamProfileAsync(steamId);
                var avatarPath = !string.IsNullOrWhiteSpace(profile?.AvatarUrl)
                    ? await TryDownloadAvatarAsync(steamId, steamId, profile.AvatarUrl)
                    : null;
                return (SteamId: steamId, Profile: profile, AvatarPath: avatarPath);
            }
            finally
            {
                fetchThrottle.Release();
            }
        }));

        await _fileGate.WaitAsync();
        try
        {
            var document = ReadDocumentForWrite();
            var updatedCount = 0;
            foreach (var (steamId, profile, avatarPath) in results)
            {
                if (profile is null)
                {
                    continue;
                }

                var item = document.Accounts.FirstOrDefault(account =>
                    string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    continue;
                }

                // Skip overwriting when a concurrent write updated the field during the fetch (it no longer matches the snapshot): the concurrent value is always newer than
                // the snapshot taken before this fetch began, so overwriting would only "roll back" the data.
                var snapshot = baseline.TryGetValue(steamId, out var value) ? value : default;
                var wrote = false;
                if (!string.IsNullOrWhiteSpace(profile.PersonaName) && item.PersonaName == snapshot.PersonaName)
                {
                    item.PersonaName = profile.PersonaName;
                    wrote = true;
                }

                if (!string.IsNullOrWhiteSpace(profile.AvatarUrl) && item.AvatarUrl == snapshot.AvatarUrl)
                {
                    var urlChanged = !string.Equals(item.AvatarUrl, profile.AvatarUrl, StringComparison.OrdinalIgnoreCase);
                    item.AvatarUrl = profile.AvatarUrl;
                    if (avatarPath is not null)
                    {
                        item.AvatarPath = avatarPath;
                    }
                    else if (urlChanged)
                    {
                        // The avatar changed but this download failed: clear the local path so the UI falls back to the new remote URL,
                        // otherwise the old local image wins and the new avatar never shows.
                        item.AvatarPath = null;
                    }

                    wrote = true;
                }

                // Count only accounts that actually had data written: a fetch with all fields empty (nothing written) is not "synced", so the count shown is not inflated.
                if (wrote)
                {
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                document.Accounts = NormalizeAccounts(document.Accounts).ToList();
                WriteDocument(document);
            }

            return updatedCount;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Deletes the given accounts (including cached local avatars). Returns the number of entries actually removed.</summary>
    public int DeleteAccounts(IReadOnlyCollection<SteamAccountHistoryItem> accounts)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var keys = accounts.Select(GetAccountKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int removed;
        _fileGate.Wait();
        try
        {
            var document = ReadDocumentForWrite();
            removed = document.Accounts.RemoveAll(account => keys.Contains(GetAccountKey(account)));
            document.Accounts = NormalizeAccounts(document.Accounts).ToList();
            WriteDocument(document);
        }
        finally
        {
            _fileGate.Release();
        }

        foreach (var account in accounts)
        {
            TryDeleteFile(account.AvatarPath);
        }

        return removed;
    }

    /// <summary>Clears all saved accounts and the avatar cache. Returns the account count before clearing.</summary>
    public int ClearAll()
    {
        int count;
        _fileGate.Wait();
        try
        {
            // Clearing is a plain overwrite that does not depend on the old content, so a failed read just counts as 0; no need for ReadDocumentForWrite to abort.
            var document = ReadDocument();
            count = NormalizeAccounts(document.Accounts).Count;
            WriteDocument(new AccountHistoryDocument());
        }
        finally
        {
            _fileGate.Release();
        }

        try
        {
            if (Directory.Exists(AvatarFolderPath))
            {
                foreach (var file in Directory.EnumerateFiles(AvatarFolderPath))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return count;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // The avatar may be held open by a BitmapImage in the UI. If it cannot be deleted, leave it; the record is still removed.
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Read-only callers (Load and other read-only APIs) may fall back to an empty document; read-modify-write paths must use ReadDocumentForWrite instead.
    private AccountHistoryDocument ReadDocument()
    {
        try
        {
            return ReadDocumentForWrite();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to read account history, using an empty document (read-only path).", ex);
            return new AccountHistoryDocument();
        }
    }

    // For "read, modify, overwrite whole file" paths: tells "file does not exist" apart from "file exists but cannot be read or parsed".
    // The latter throws to abort the overwrite, so an empty document never replaces all saved accounts and tokens.
    private AccountHistoryDocument ReadDocumentForWrite()
    {
        if (!File.Exists(HistoryFilePath))
        {
            return new AccountHistoryDocument();
        }

        try
        {
            var json = File.ReadAllText(HistoryFilePath);
            var document = JsonSerializer.Deserialize(json, AccountHistoryJsonContext.Default.AccountHistoryDocument)
                ?? new AccountHistoryDocument();
            document.Accounts ??= [];
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Account history file exists but cannot be read. Save aborted so data is not overwritten.", ex);
            throw new InvalidOperationException(
                Loc.T("Account_Error_HistoryFileUnreadable"),
                ex);
        }
    }

    private static SteamAccountHistoryItem? FindExisting(
        IEnumerable<SteamAccountHistoryItem> accounts,
        string steamId,
        string accountName)
    {
        if (!string.IsNullOrWhiteSpace(steamId))
        {
            var bySteamId = accounts.FirstOrDefault(account =>
                string.Equals(account.SteamId, steamId, StringComparison.OrdinalIgnoreCase));
            if (bySteamId is not null)
            {
                return bySteamId;
            }
        }

        return accounts.FirstOrDefault(account =>
            string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<SteamAccountHistoryItem> NormalizeAccounts(
        IEnumerable<SteamAccountHistoryItem> accounts)
    {
        var normalized = accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.EyaToken))
            .GroupBy(GetAccountKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(account => account.LastLoginAt)
                .First())
            .OrderByDescending(account => account.LastLoginAt)
            .ToList();

        // An explicit "groupIds": null in the JSON overrides the property initializer; consumers (such as the bulk group flyout) that call .GroupIds.Contains directly would throw an NRE.
        foreach (var account in normalized)
        {
            account.GroupIds ??= [];
        }

        return normalized;
    }

    /// <summary>Account dedup key (SteamID first, account name if missing). HistoryPage reuses it so selection keys stay consistent.</summary>
    internal static string GetAccountKey(SteamAccountHistoryItem account)
    {
        return string.IsNullOrWhiteSpace(account.SteamId)
            ? $"name:{account.AccountName}"
            : $"id:{account.SteamId}";
    }

    private static async Task<SteamProfileData?> TryGetSteamProfileAsync(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        try
        {
            // The one-off nc parameter bypasses the edge cache: ?xml=1 is cached by the CDN for up to 1 hour (the cache key includes the query string, so a unique parameter always goes to origin),
            // otherwise a refresh after a rename or avatar change gets the old snapshot and "rolls back" the record.
            var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId.Trim())}" +
                $"?xml=1&nc={DateTimeOffset.UtcNow.Ticks}";
            var xml = await DefaultHttpClient.GetStringAsync(url);
            var document = XDocument.Parse(xml);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            return new SteamProfileData(
                root.Element("steamID")?.Value,
                root.Element("avatarFull")?.Value ?? root.Element("avatarMedium")?.Value);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn($"Failed to fetch Steam profile ({steamId}): {ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            AppLog.Warn($"Steam profile fetch timed out ({steamId}).");
            return null;
        }
        catch (System.Xml.XmlException)
        {
            // Missing or banned accounts return 200 with an HTML error page, not XML.
            AppLog.Warn($"Steam profile response was not XML ({steamId}), skipped.");
            return null;
        }
    }

    private async Task<string?> TryDownloadAvatarAsync(
        string steamId,
        string accountName,
        string avatarUrl)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri))
        {
            return null;
        }

        string? tempPath = null;
        try
        {
            using var response = await DefaultHttpClient.GetAsync(avatarUri);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 || !LooksLikeImage(bytes))
            {
                // Reject 200+HTML "avatars" from captive portals or hijacked networks: once saved, such files always fail to decode in the UI.
                return null;
            }

            Directory.CreateDirectory(AvatarFolderPath);
            var avatarPath = Path.Combine(AvatarFolderPath, $"{GetSafeAvatarKey(steamId, accountName)}.jpg");
            // Atomic write: write a temp file, then replace, so the UI thread never reads a half-written JPEG or hits a file mid-write.
            // The temp name has a random suffix: the post-sign-in fallback refresh and a manual refresh can download the same account at once, and a fixed name would collide and fail silently.
            tempPath = avatarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, avatarPath, overwrite: true);
            return avatarPath;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            TryDeleteFile(tempPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            return null;
        }
    }

    // JPEG(FF D8 FF) / PNG(89 50 4E 47) / GIF(47 49 46): the avatar CDN only returns these three image types.
    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length >= 4 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) ||
            (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
            (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46));

    private static string GetSafeAvatarKey(string steamId, string accountName)
    {
        var value = string.IsNullOrWhiteSpace(steamId) ? accountName : steamId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private void WriteDocument(AccountHistoryDocument document)
    {
        Directory.CreateDirectory(HistoryFolderPath);
        var json = JsonSerializer.Serialize(document, AccountHistoryJsonContext.Default.AccountHistoryDocument);

        // Write a temp file, then replace atomically, so an interrupted process cannot leave accounts.json half-written and have the next save wipe all history.
        // The temp file name has a random suffix so leftover .tmp files from earlier writes do not collide.
        var tempPath = HistoryFilePath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);

            var backupPath = HistoryFilePath + ".bak";
            if (File.Exists(HistoryFilePath))
            {
                // Keep a .bak of the old file before overwriting: File.Replace swaps atomically and leaves the backup.
                File.Replace(tempPath, HistoryFilePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, HistoryFilePath);
            }
        }
        catch
        {
            // If the replace fails (.bak locked, etc.), delete the temp file so failures do not pile up .tmp files in the history folder.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // A failed cleanup only leaves a temp file behind and does not affect the real file; swallow it to keep the original exception.
            }

            throw;
        }
    }

    private sealed record SteamProfileData(string? PersonaName, string? AvatarUrl);
}

internal sealed class AccountHistoryDocument
{
    public int Version { get; set; } = 1;

    public List<SteamAccountHistoryItem> Accounts { get; set; } = [];
}

// JsonSerializerDefaults.Web matches the old reflection-based serializer: camelCase property names and case-insensitive reads,
// so existing accounts.json files stay readable and writable under AOT.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AccountHistoryDocument))]
internal sealed partial class AccountHistoryJsonContext : JsonSerializerContext;
