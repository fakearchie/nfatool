using System.Globalization;
using Microsoft.Win32;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamConfigService
{
    public IReadOnlyList<CachedSteamLoginAccount> GetLoginAccounts(SteamPaths paths)
    {
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        var loginUsers = VdfDocument.LoadOrEmpty(loginUsersPath);
        var accounts = new List<CachedSteamLoginAccount>();

        var activeAccount = GetActiveLoginAccount(paths, loginUsers);
        if (activeAccount is not null)
        {
            accounts.Add(activeAccount);
        }

        accounts.AddRange(GetLoginUsersAccounts(loginUsers));
        accounts.AddRange(GetConfigAccounts(Path.Combine(paths.ConfigPath, "config.vdf")));

        var normalized = NormalizeLoginAccounts(accounts);
        PopulateConnectCacheTokens(paths, normalized);
        return normalized;
    }

    public void UpdateLoginFiles(
        SteamPaths paths,
        string accountName,
        string steamId,
        string encryptedJwt,
        string accountCrc32)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        // Both files that hold the accounts Steam remembers are read before anything is written. If either cannot be
        // read, the sign-in stops here with every file untouched.
        var loginUsers = LoadRememberedAccounts(loginUsersPath);
        var local = LoadRememberedAccounts(paths.LocalVdfPath);
        AppLog.Info($"Steam remembers {CountUsers(loginUsers)} account(s) before this sign-in.");

        UpdateConfigVdf(configPath, accountName, steamId, loginUsers);
        AppLog.Info($"Wrote config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        UpdateLoginUsersVdf(loginUsersPath, loginUsers, accountName, steamId);
        AppLog.Info($"Wrote loginusers.vdf ({FileLength(loginUsersPath)} bytes, {CountUsers(loginUsers)} account(s)): \"{loginUsersPath}\"");

        UpdateLocalVdf(paths.LocalVdfPath, local, accountCrc32, encryptedJwt);
        AppLog.Info($"Wrote local.vdf ({FileLength(paths.LocalVdfPath)} bytes): \"{paths.LocalVdfPath}\"");
    }

    public void RestoreLoginFiles(SteamPaths paths, CachedSteamLoginAccount account)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        // Read before writing anything, for the same reason as a sign-in.
        var loginUsers = LoadRememberedAccounts(loginUsersPath);
        var local = string.IsNullOrWhiteSpace(account.ConnectCacheToken)
            ? null
            : LoadRememberedAccounts(paths.LocalVdfPath);

        UpdateConfigVdf(configPath, account.AccountName, account.SteamId, loginUsers);
        AppLog.Info($"Restored config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        RestoreLoginUsersVdf(loginUsersPath, loginUsers, account);
        AppLog.Info($"Restored loginusers.vdf ({FileLength(loginUsersPath)} bytes, {CountUsers(loginUsers)} account(s)): \"{loginUsersPath}\"");

        // Write back the account's original ConnectCache token: Steam needs it to sign in automatically without a password; without it the account is only preselected and still needs a manual sign-in.
        if (local is not null)
        {
            UpdateLocalVdf(
                paths.LocalVdfPath,
                local,
                Crc32.ComputeSteamAccountKey(account.AccountName),
                account.ConnectCacheToken!);
            AppLog.Info($"Restored local.vdf ConnectCache ({FileLength(paths.LocalVdfPath)} bytes): \"{paths.LocalVdfPath}\"");
        }
        else
        {
            AppLog.Warn("Cached account has no ConnectCache token, so Steam may need a manual sign-in after the restore.");
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    // Where the last copy of each accounts file that read cleanly is kept. Local, not roaming: local.vdf holds login tokens.
    private static readonly string BackupFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "nfa.pub Loader",
        "steam-backup");

    /// <summary>
    /// Saves loginusers.vdf and local.vdf as the backup while Steam is still running, before it is stopped and possibly
    /// killed mid-write, so a fallback includes every account Steam has added since the loader last wrote. Best effort:
    /// a file that does not read cleanly on the first try is simply not backed up this time.
    /// </summary>
    public void BackupRememberedAccounts(SteamPaths paths)
    {
        foreach (var path in new[] { Path.Combine(paths.ConfigPath, "loginusers.vdf"), paths.LocalVdfPath })
        {
            if (VdfDocument.LoadForUpdate(path, out _, out var text, out _, attempts: 1) == VdfReadState.Loaded)
            {
                SaveBackup(Path.GetFileName(path), text);
            }
        }
    }

    /// <summary>
    /// Reads loginusers.vdf or local.vdf for a sign-in. A clean read is also saved as the backup. A file that is empty or
    /// cannot be read, most likely because Steam was stopped while writing it, falls back to that backup, so the accounts
    /// Steam remembered come back instead of being replaced by the one account being added. With no backup, an
    /// unreadable file stops the sign-in and is left as it is.
    /// </summary>
    private static Dictionary<string, object> LoadRememberedAccounts(string path)
    {
        var name = Path.GetFileName(path);
        var state = VdfDocument.LoadForUpdate(path, out var document, out var text, out var error);
        if (state == VdfReadState.Loaded)
        {
            SaveBackup(name, text);
            return document;
        }

        if (state == VdfReadState.Missing)
        {
            return document;
        }

        var backupPath = Path.Combine(BackupFolder, name);
        if (VdfDocument.LoadForUpdate(backupPath, out var backup, out _, out _) == VdfReadState.Loaded)
        {
            AppLog.Warn($"{name} could not be read ({error ?? "empty"}), so the copy saved at {File.GetLastWriteTime(backupPath):yyyy-MM-dd HH:mm} was used instead.");
            return backup;
        }

        if (state == VdfReadState.Empty)
        {
            // Nothing is left in the file and there is no earlier copy, so there is nothing to lose by starting it fresh.
            AppLog.Warn($"{name} is empty and there is no earlier copy, so it is started fresh.");
            return document;
        }

        AppLog.Error($"{name} could not be read ({error}) and there is no earlier copy. Sign-in stopped without changing it.");
        throw new InvalidOperationException(Loc.Tf("Steam_Error_AccountsFileUnreadable_Format", name));
    }

    private static void SaveBackup(string name, string text)
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var backupPath = Path.Combine(BackupFolder, name);
            var tempPath = backupPath + "." + Path.GetRandomFileName() + ".tmp";
            File.WriteAllText(tempPath, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // The backup only helps recovery, so failing to write it never stops a sign-in.
            AppLog.Warn($"Could not back up {name}: {ex.Message}");
        }
    }

    private static int CountUsers(Dictionary<string, object> loginUsers) =>
        TryGetUsers(loginUsers, out var users) ? users.Values.OfType<Dictionary<string, object>>().Count() : 0;

    private static void UpdateConfigVdf(string path, string accountName, string steamId, Dictionary<string, object> loginUsers)
    {
        // Matches the original binary (sub_140003640): config.vdf is built from scratch and overwritten whole,
        // never read or merged with the old file. The old code used LoadOrEmpty to read the user's existing config.vdf
        // (often 20KB+) and round-tripped it through our hand-written VDF parser and serializer. If the round trip broke
        // any structure, Steam could not read config.vdf on start and reset it, also ignoring the auto sign-in we
        // wrote to loginusers.vdf/local.vdf, and stopped at the sign-in screen. This was the root cause of "the sign-in flow
        // succeeded and Steam started, but it did not sign in automatically", seen only on some machines
        // (depending on whether that machine's config.vdf had content our parser handled badly). The original binary
        // just writes the three-entry minimal template below, which avoids round-trip damage entirely.
        var config = new Dictionary<string, object>(StringComparer.Ordinal);
        var steam = EnsurePath(config, "InstallConfigStore", "Software", "Valve", "Steam");

        steam["AutoUpdateWindowEnabled"] = "0";
        steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();

        // Steam keeps an entry here for every account it remembers. The template used to hold only the account being
        // signed in, which dropped the others from this file on every sign-in, so they are carried over from loginusers.vdf.
        var accounts = EnsureObject(steam, "Accounts");
        if (TryGetUsers(loginUsers, out var users))
        {
            foreach (var (userSteamId, value) in users)
            {
                if (value is Dictionary<string, object> user &&
                    GetString(user, "AccountName") is { Length: > 0 } userAccountName)
                {
                    accounts[userAccountName] = new Dictionary<string, object>
                    {
                        ["SteamID"] = userSteamId
                    };
                }
            }
        }

        accounts[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId
        };

        VdfDocument.Save(path, config);
    }

    private static void UpdateLoginUsersVdf(string path, Dictionary<string, object> loginUsers, string accountName, string steamId)
    {
        var users = EnsureObject(loginUsers, "users");

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        users[steamId] = new Dictionary<string, object>
        {
            ["AccountName"] = accountName,
            ["PersonaName"] = accountName,
            ["RememberPassword"] = "1",
            ["WantsOfflineMode"] = "0",
            ["SkipOfflineModeWarning"] = "0",
            ["AllowAutoLogin"] = "1",
            ["MostRecent"] = "1",
            ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };

        SaveAndBackUp(path, loginUsers);
    }

    private static void RestoreLoginUsersVdf(string path, Dictionary<string, object> loginUsers, CachedSteamLoginAccount account)
    {
        var users = EnsureObject(loginUsers, "users");

        foreach (var user in users.Values.OfType<Dictionary<string, object>>())
        {
            user["MostRecent"] = "0";
        }

        var restoredUser = EnsureObject(users, account.SteamId);
        restoredUser["AccountName"] = account.AccountName;
        // Prefer the nickname already in loginusers.vdf, then the cached Steam nickname, and only then fall back to the account name.
        var existingPersona = GetString(restoredUser, "PersonaName");
        restoredUser["PersonaName"] = !string.IsNullOrWhiteSpace(existingPersona)
            ? existingPersona
            : !string.IsNullOrWhiteSpace(account.PersonaName)
                ? account.PersonaName
                : account.AccountName;
        restoredUser["RememberPassword"] = "1";
        restoredUser["WantsOfflineMode"] = "0";
        restoredUser["SkipOfflineModeWarning"] = "0";
        restoredUser["AllowAutoLogin"] = "1";
        restoredUser["MostRecent"] = "1";
        restoredUser["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

        SaveAndBackUp(path, loginUsers);
    }

    private static void UpdateLocalVdf(string path, Dictionary<string, object> local, string accountCrc32, string encryptedJwt)
    {
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");

        connectCache[accountCrc32] = encryptedJwt;
        SaveAndBackUp(path, local);
    }

    // The backup always matches what the loader last wrote, so a later fallback keeps the account signed in here.
    private static void SaveAndBackUp(string path, Dictionary<string, object> document)
    {
        VdfDocument.Save(path, document);
        SaveBackup(Path.GetFileName(path), VdfDocument.ToText(document));
    }

    private static Dictionary<string, object> EnsurePath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            current = EnsureObject(current, key);
        }

        return current;
    }

    private static Dictionary<string, object> EnsureObject(
        Dictionary<string, object> parent,
        string key)
    {
        if (parent.TryGetValue(key, out var value) && value is Dictionary<string, object> existing)
        {
            return existing;
        }

        var created = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[key] = created;
        return created;
    }

    private static CachedSteamLoginAccount? GetActiveLoginAccount(
        SteamPaths paths,
        Dictionary<string, object> loginUsers)
    {
        var accountId = ReadActiveUserAccountId();
        if (!accountId.HasValue)
        {
            return null;
        }

        var steamId = ToSteam64(accountId.Value);
        var accountName = FindAccountNameBySteamId(loginUsers, steamId) ??
            FindAccountNameBySteamId(Path.Combine(paths.ConfigPath, "config.vdf"), steamId) ??
            ReadSteamRegistryString("AutoLoginUser");

        if (string.IsNullOrWhiteSpace(accountName))
        {
            AppLog.Warn($"Found active Steam user {steamId}, but could not resolve its account name.");
            return null;
        }

        return new CachedSteamLoginAccount
        {
            AccountName = accountName,
            SteamId = steamId,
            CachedAt = DateTimeOffset.Now
        };
    }

    // Before our sign-in overwrites local.vdf, grab each account's ConnectCache token (crc32(account name)+"1") and cache it with the account;
    // writing it back to local.vdf on restore is what lets Steam sign in automatically without a password. If it can't be read (Steam forgot the account or the token rotated) it stays null,
    // and restore falls back to only preselecting the account. If local.vdf fails to parse, GetPath returns null and the whole step is skipped, best-effort.
    private static void PopulateConnectCacheTokens(
        SteamPaths paths,
        IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        var connectCache = GetPath(
            VdfDocument.LoadOrEmpty(paths.LocalVdfPath),
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");
        if (connectCache is null)
        {
            return;
        }

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AccountName))
            {
                continue;
            }

            var token = GetString(connectCache, Crc32.ComputeSteamAccountKey(account.AccountName));
            if (!string.IsNullOrWhiteSpace(token))
            {
                account.ConnectCacheToken = token;
            }
        }
    }

    // internal: SteamAccountNameService reuses this parsing to fill in the CS2 source account's display name offline.
    internal static IEnumerable<CachedSteamLoginAccount> GetLoginUsersAccounts(
        Dictionary<string, object> loginUsers)
    {
        if (!TryGetUsers(loginUsers, out var users))
        {
            yield break;
        }

        foreach (var (steamId, value) in users)
        {
            if (value is not Dictionary<string, object> user)
            {
                continue;
            }

            var accountName = GetString(user, "AccountName");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                PersonaName = GetString(user, "PersonaName"),
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    // internal: like GetLoginUsersAccounts, reused by SteamAccountNameService.
    internal static IEnumerable<CachedSteamLoginAccount> GetConfigAccounts(string configPath)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            yield break;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is not Dictionary<string, object> account)
            {
                continue;
            }

            var steamId = GetString(account, "SteamID");
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(steamId))
            {
                continue;
            }

            yield return new CachedSteamLoginAccount
            {
                AccountName = accountName,
                SteamId = steamId,
                CachedAt = DateTimeOffset.Now
            };
        }
    }

    private static IReadOnlyList<CachedSteamLoginAccount> NormalizeLoginAccounts(
        IEnumerable<CachedSteamLoginAccount> accounts)
    {
        return accounts
            .Where(account =>
                !string.IsNullOrWhiteSpace(account.AccountName) &&
                !string.IsNullOrWhiteSpace(account.SteamId))
            .GroupBy(account => account.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool TryGetUsers(
        Dictionary<string, object> loginUsers,
        out Dictionary<string, object> users)
    {
        if (VdfDocument.GetObject(loginUsers, "users") is { } existingUsers)
        {
            users = existingUsers;
            return true;
        }

        users = [];
        return false;
    }

    private static string? GetString(Dictionary<string, object> values, string key)
    {
        return VdfDocument.GetValue(values, key)?.ToString();
    }

    private static uint? ReadActiveUserAccountId()
    {
        return ReadSteamRegistryUInt32(@"Software\Valve\Steam\ActiveProcess", "ActiveUser") ??
            ReadSteamRegistryUInt32(@"Software\Valve\Steam", "ActiveUser");
    }

    private static string ToSteam64(uint accountId)
    {
        const ulong individualAccountUniverseBase = 76561197960265728UL;
        return (individualAccountUniverseBase + accountId).ToString(CultureInfo.InvariantCulture);
    }

    private static string? FindAccountNameBySteamId(
        Dictionary<string, object> loginUsers,
        string steamId)
    {
        if (!TryGetUsers(loginUsers, out var users) ||
            !users.TryGetValue(steamId, out var value) ||
            value is not Dictionary<string, object> user)
        {
            return null;
        }

        return GetString(user, "AccountName");
    }

    private static string? FindAccountNameBySteamId(string configPath, string steamId)
    {
        var config = VdfDocument.LoadOrEmpty(configPath);
        var steam = GetPath(config, "InstallConfigStore", "Software", "Valve", "Steam");
        if (steam is null || VdfDocument.GetObject(steam, "Accounts") is not { } accounts)
        {
            return null;
        }

        foreach (var (accountName, value) in accounts)
        {
            if (value is Dictionary<string, object> account &&
                string.Equals(GetString(account, "SteamID"), steamId, StringComparison.OrdinalIgnoreCase))
            {
                return accountName;
            }
        }

        return null;
    }

    private static Dictionary<string, object>? GetPath(
        Dictionary<string, object> root,
        params string[] keys)
    {
        var current = root;
        foreach (var key in keys)
        {
            if (VdfDocument.GetObject(current, key) is not { } child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private static uint? ReadSteamRegistryUInt32(string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(keyPath);
            var value = key?.GetValue(valueName);
            return value switch
            {
                int intValue when intValue > 0 => unchecked((uint)intValue),
                uint uintValue when uintValue > 0 => uintValue,
                long longValue when longValue is > 0 and <= uint.MaxValue => (uint)longValue,
                string stringValue when uint.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSteamRegistryString(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = baseKey.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
