using System.Globalization;
using Microsoft.Win32;
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

        UpdateConfigVdf(configPath, accountName, steamId);
        AppLog.Info($"Wrote config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        UpdateLoginUsersVdf(loginUsersPath, accountName, steamId);
        AppLog.Info($"Wrote loginusers.vdf ({FileLength(loginUsersPath)} bytes): \"{loginUsersPath}\"");

        UpdateLocalVdf(paths.LocalVdfPath, accountCrc32, encryptedJwt);
        AppLog.Info($"Wrote local.vdf ({FileLength(paths.LocalVdfPath)} bytes): \"{paths.LocalVdfPath}\"");
    }

    public void RestoreLoginFiles(SteamPaths paths, CachedSteamLoginAccount account)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        UpdateConfigVdf(configPath, account.AccountName, account.SteamId);
        AppLog.Info($"Restored config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        RestoreLoginUsersVdf(loginUsersPath, account);
        AppLog.Info($"Restored loginusers.vdf ({FileLength(loginUsersPath)} bytes): \"{loginUsersPath}\"");

        // Write back the account's original ConnectCache token: Steam needs it to sign in automatically without a password; without it the account is only preselected and still needs a manual sign-in.
        if (!string.IsNullOrWhiteSpace(account.ConnectCacheToken))
        {
            UpdateLocalVdf(
                paths.LocalVdfPath,
                Crc32.ComputeSteamAccountKey(account.AccountName),
                account.ConnectCacheToken);
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

    private static void UpdateConfigVdf(string path, string accountName, string steamId)
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

        var accounts = EnsureObject(steam, "Accounts");
        accounts[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId
        };

        VdfDocument.Save(path, config);
    }

    private static void UpdateLoginUsersVdf(string path, string accountName, string steamId)
    {
        var loginUsers = VdfDocument.LoadOrEmpty(path);
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

        VdfDocument.Save(path, loginUsers);
    }

    private static void RestoreLoginUsersVdf(string path, CachedSteamLoginAccount account)
    {
        var loginUsers = VdfDocument.LoadOrEmpty(path);
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

        VdfDocument.Save(path, loginUsers);
    }

    private static void UpdateLocalVdf(string path, string accountCrc32, string encryptedJwt)
    {
        var local = VdfDocument.LoadOrEmpty(path);
        var connectCache = EnsurePath(
            local,
            "MachineUserConfigStore",
            "Software",
            "Valve",
            "Steam",
            "ConnectCache");

        connectCache[accountCrc32] = encryptedJwt;
        VdfDocument.Save(path, local);
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
