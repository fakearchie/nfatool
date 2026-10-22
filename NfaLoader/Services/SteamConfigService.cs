using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamConfigService
{
    // Where Steam keeps the saved login of each account in local.vdf, and the accounts list in config.vdf. Steam reads
    // these keys without regard to case, and some installs spell them "valve" and "steam".
    private static readonly string[] ConnectCachePath = ["MachineUserConfigStore", "Software", "Valve", "Steam", "ConnectCache"];
    private static readonly string[] ConfigSteamPath = ["InstallConfigStore", "Software", "Valve", "Steam"];
    private static readonly string[] ConfigAccountsPath = [.. ConfigSteamPath, "Accounts"];

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

    /// <summary>Joins two reads of the remembered accounts. For an account in both, the first list's entry wins.</summary>
    public static IReadOnlyList<CachedSteamLoginAccount> CombineLoginAccounts(
        IReadOnlyList<CachedSteamLoginAccount> preferred,
        IReadOnlyList<CachedSteamLoginAccount> fallback) =>
        NormalizeLoginAccounts(preferred.Concat(fallback));

    /// <summary>
    /// Writes what Steam needs to sign in to the account by itself. Only the entries for this account are changed in
    /// Steam's files, the way tools that work reliably do it; everything else in them stays exactly as Steam wrote it.
    /// </summary>
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

        // Both files that hold the accounts Steam remembers are read before anything is written, so a problem with
        // either one is known before the first file changes.
        var loginUsers = LoadRememberedAccounts(loginUsersPath, out _);
        var local = LoadRememberedAccounts(paths.LocalVdfPath, out _);
        AppLog.Info($"Steam remembers {CountUsers(loginUsers)} account(s) before this sign-in.");
        RepairConnectCache(local);

        UpdateConfigVdf(configPath, accountName, steamId);
        AppLog.Info($"Wrote config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        MarkSignedIn(loginUsers, accountName, steamId, accountName);
        SaveAndBackUp(loginUsersPath, loginUsers);
        AppLog.Info($"Wrote loginusers.vdf ({FileLength(loginUsersPath)} bytes, {CountUsers(loginUsers)} account(s)): \"{loginUsersPath}\"");

        local.SetValue(ConnectCachePath, accountCrc32, encryptedJwt);
        SaveAndBackUp(paths.LocalVdfPath, local);
        AppLog.Info($"Wrote local.vdf ({FileLength(paths.LocalVdfPath)} bytes), saved login under {local.DescribePath(ConnectCachePath)}/{accountCrc32}: \"{paths.LocalVdfPath}\"");
    }

    /// <summary>
    /// Steam signs in by itself only when the registry allows it: AutoLoginUser names the account and RememberPassword
    /// lets it use the saved login. The loader used to rely on "-login" alone, which only worked on PCs where
    /// RememberPassword was already 1 from something else. Everywhere else Steam opened at its sign-in screen and
    /// did nothing, even though the sign-in had been reported as done.
    /// </summary>
    public void SetAutoLoginUser(string accountName)
    {
        try
        {
            using var steam = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam", writable: true);
            steam.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
            steam.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
            AppLog.Info($"Set Steam to sign in to \"{accountName}\" automatically.");
        }
        catch (Exception ex)
        {
            // Steam may still sign in from "-login" and loginusers.vdf, so this does not stop the sign-in.
            AppLog.Warn($"Could not set Steam's automatic sign-in in the registry: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects a cached account in Steam again. Returns whether Steam has a working saved login for it, which is what
    /// lets Steam sign in without asking for the password.
    /// </summary>
    public bool RestoreLoginFiles(SteamPaths paths, CachedSteamLoginAccount account)
    {
        Directory.CreateDirectory(paths.ConfigPath);

        var configPath = Path.Combine(paths.ConfigPath, "config.vdf");
        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");

        // Read before writing anything, for the same reason as a sign-in.
        var loginUsers = LoadRememberedAccounts(loginUsersPath, out _);
        // A local.vdf that Steam itself cannot read (left half written when Steam was stopped) is always written back
        // from the copy that replaced it, even when nothing else in it changes.
        var local = LoadRememberedAccounts(paths.LocalVdfPath, out var localRecovered);
        var localChanged = RepairConnectCache(local) > 0 || localRecovered;

        UpdateConfigVdf(configPath, account.AccountName, account.SteamId);
        AppLog.Info($"Restored config.vdf ({FileLength(configPath)} bytes): \"{configPath}\"");

        MarkSignedIn(
            loginUsers,
            account.AccountName,
            account.SteamId,
            string.IsNullOrWhiteSpace(account.PersonaName) ? account.AccountName : account.PersonaName);
        SaveAndBackUp(loginUsersPath, loginUsers);
        AppLog.Info($"Restored loginusers.vdf ({FileLength(loginUsersPath)} bytes, {CountUsers(loginUsers)} account(s)): \"{loginUsersPath}\"");

        // The login Steam has saved now is newer than the cached copy whenever the account was used in Steam since it
        // was cached, and Steam replaces a login token each time it renews it. So the cached copy only fills a gap.
        var key = Crc32.ComputeSteamAccountKey(account.AccountName);
        bool savedLogin;
        if (!string.IsNullOrWhiteSpace(local.GetValue([.. ConnectCachePath, key])))
        {
            AppLog.Info("Steam still has a saved login for this account, so it was kept instead of the cached copy.");
            savedLogin = true;
        }
        else if (IsUsableSavedLogin(account))
        {
            local.SetValue(ConnectCachePath, key, account.ConnectCacheToken!);
            localChanged = true;
            AppLog.Info("Wrote the cached saved login back to local.vdf.");
            savedLogin = true;
        }
        else
        {
            AppLog.Warn("There is no working saved login for this account, so Steam will ask for its password.");
            savedLogin = false;
        }

        if (localChanged)
        {
            SaveAndBackUp(paths.LocalVdfPath, local);
            AppLog.Info($"Restored local.vdf ({FileLength(paths.LocalVdfPath)} bytes): \"{paths.LocalVdfPath}\"");
        }

        return savedLogin;
    }

    // A cached login is only written back if Windows can still decrypt it for this account and it has not expired.
    // Anything else would only replace a missing login with one Steam refuses.
    private static bool IsUsableSavedLogin(CachedSteamLoginAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.ConnectCacheToken))
        {
            AppLog.Warn("The cached account has no saved login.");
            return false;
        }

        var token = new SteamCryptoService().TryDecryptFromHex(account.ConnectCacheToken, account.AccountName);
        if (token is null)
        {
            AppLog.Warn("The cached saved login cannot be decrypted for this Windows user and account name.");
            return false;
        }

        var info = new JwtTokenService().Inspect(token);
        if (!info.IsValid)
        {
            AppLog.Warn($"The cached saved login cannot be used: {info.Status}");
            return false;
        }

        return true;
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
    /// Steam remembered come back instead of being replaced by the one account being added. With no backup, every
    /// account and login token that can still be found in the file is kept. A sign-in is never stopped by this.
    /// </summary>
    /// <param name="recovered">True when the file on disk is empty or unreadable, so what is returned is not what Steam has.</param>
    private static VdfText LoadRememberedAccounts(string path, out bool recovered)
    {
        var name = Path.GetFileName(path);
        var state = VdfDocument.LoadForUpdate(path, out _, out var text, out var error);
        recovered = state is VdfReadState.Empty or VdfReadState.Unreadable;
        if (state == VdfReadState.Loaded)
        {
            if (VdfText.TryParse(text, out var document))
            {
                SaveBackup(name, text);
                return document;
            }

            state = VdfReadState.Unreadable;
            error = "the loader cannot edit its layout";
        }

        if (state == VdfReadState.Missing)
        {
            return VdfText.Empty();
        }

        var backupPath = Path.Combine(BackupFolder, name);
        if (VdfDocument.LoadForUpdate(backupPath, out _, out var backupText, out _) == VdfReadState.Loaded &&
            VdfText.TryParse(backupText, out var backup))
        {
            AppLog.Warn($"{name} could not be read ({error ?? "empty"}), so the copy saved at {File.GetLastWriteTime(backupPath):yyyy-MM-dd HH:mm} was used instead.");
            return backup;
        }

        if (state == VdfReadState.Empty)
        {
            // Nothing is left in the file and there is no earlier copy, so there is nothing to lose by starting it fresh.
            AppLog.Warn($"{name} is empty and there is no earlier copy, so it is started fresh.");
            return VdfText.Empty();
        }

        // Keep the file as it was, then carry on with whatever can still be found in it.
        var raw = KeepUnreadableCopy(path);
        var salvaged = name.Equals("local.vdf", StringComparison.OrdinalIgnoreCase)
            ? SalvageConnectCache(raw)
            : SalvageLoginUsers(raw);
        AppLog.Warn($"{name} could not be read ({error}) and there is no earlier copy, so the {CountSalvaged(salvaged)} entries rescued from it were kept.");
        return salvaged;
    }

    // Saves a file that could not be read next to the backups, for support, and returns its text.
    private static string KeepUnreadableCopy(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var raw = File.ReadAllText(path);
            SaveBackup($"{name}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", raw);
            return raw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read {name} to rescue its entries: {ex.Message}");
            return "";
        }
    }

    // Pulls each "<SteamID64>" { "key" "value" ... } block out of a loginusers.vdf that does not parse as a whole.
    private static VdfText SalvageLoginUsers(string text)
    {
        var users = VdfText.Empty();
        foreach (Match block in Regex.Matches(text, @"""(7656\d{13})""\s*\{([^{}]*)\}"))
        {
            foreach (Match pair in Regex.Matches(block.Groups[2].Value, @"""([^""\r\n]*)""[ \t]+""([^""\r\n]*)"""))
            {
                users.SetValue(["users", block.Groups[1].Value], pair.Groups[1].Value, pair.Groups[2].Value);
            }
        }

        return users;
    }

    // Pulls each ConnectCache login token out of a local.vdf that does not parse as a whole. The key is crc32 of the
    // account name in hex without leading zeros, plus "1", so it can be as short as two characters; the value is a
    // long hex string.
    private static VdfText SalvageConnectCache(string text)
    {
        var local = VdfText.Empty();
        foreach (Match pair in Regex.Matches(text, @"""([0-9a-fA-F]{1,8}1)""\s+""([0-9a-fA-F]{64,})"""))
        {
            local.SetValue(ConnectCachePath, pair.Groups[1].Value, pair.Groups[2].Value);
        }

        return local;
    }

    private static int CountSalvaged(VdfText document) =>
        document.DescribePath(ConnectCachePath) is not null
            ? document.GetValues(ConnectCachePath).Count
            : CountUsers(document);

    /// <summary>
    /// Merges the extra ConnectCache blocks older loader versions added next to Steam's own into the one Steam reads,
    /// and logs where the saved logins are. Returns how many blocks were merged.
    /// </summary>
    private static int RepairConnectCache(VdfText local)
    {
        var merged = local.MergeShadowedObjects(ConnectCachePath);
        if (merged > 0)
        {
            AppLog.Warn($"local.vdf had {merged} extra block(s) next to Steam's own that Steam never reads, most likely added by an older loader version. Their saved logins were moved into the block Steam reads.");
        }

        AppLog.Info($"Saved logins in local.vdf: {local.GetValues(ConnectCachePath).Count}, under {local.DescribePath(ConnectCachePath) ?? "(none yet)"}.");
        return merged;
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

    private static int CountUsers(VdfText loginUsers) => loginUsers.GetObjectKeys("users").Count;

    /// <summary>
    /// Adds the account to Steam's accounts list in config.vdf and turns off "always show the account picker", without
    /// touching anything else in the file. Rebuilding config.vdf instead, as earlier versions did, deleted the Steam
    /// Guard "remember this PC" tokens of every account along with the rest of Steam's settings.
    /// </summary>
    private static void UpdateConfigVdf(string path, string accountName, string steamId)
    {
        var config = LoadConfig(path);
        config.SetValue([.. ConfigAccountsPath, accountName], "SteamID", steamId);
        if (config.SetValueEverywhere("AlwaysShowUserChooser", "0") > 0)
        {
            AppLog.Info("Turned off Steam's \"always show the account picker\" setting, so it signs in by itself.");
        }

        VdfDocument.SaveText(path, config.Text);
    }

    private static VdfText LoadConfig(string path)
    {
        var state = VdfDocument.LoadForUpdate(path, out _, out var text, out var error);
        if (state == VdfReadState.Loaded && VdfText.TryParse(text, out var config))
        {
            SaveBackup("config.vdf", text);
            return config;
        }

        if (state != VdfReadState.Missing)
        {
            KeepUnreadableCopy(path);
            AppLog.Warn($"config.vdf could not be read ({error ?? "empty"}), so a new one was started. Steam fills in the rest when it starts.");
        }

        // The same few entries the original loader wrote. Steam adds everything else itself.
        var created = VdfText.Empty();
        created.SetValue(ConfigSteamPath, "AutoUpdateWindowEnabled", "0");
        created.SetValue(ConfigSteamPath, "MTBF", Random.Shared.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture));
        return created;
    }

    // Marks the account as the one Steam signs in to next, the way Steam itself records a remembered account. Values
    // of the account's entry that are not listed here, and every other account, stay as they are.
    private static void MarkSignedIn(VdfText loginUsers, string accountName, string steamId, string personaName)
    {
        string[] user = ["users", steamId];
        loginUsers.SetValueInEachObject(["users"], "MostRecent", "0", exceptKey: steamId);
        loginUsers.SetValue(user, "AccountName", accountName);
        loginUsers.SetValueIfMissing(user, "PersonaName", personaName);
        loginUsers.SetValue(user, "RememberPassword", "1");
        loginUsers.SetValue(user, "WantsOfflineMode", "0");
        loginUsers.SetValue(user, "SkipOfflineModeWarning", "0");
        loginUsers.SetValue(user, "AllowAutoLogin", "1");
        loginUsers.SetValue(user, "MostRecent", "1");
        loginUsers.SetValue(user, "Timestamp", DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    // The backup always matches what the loader last wrote, so a later fallback keeps the account signed in here.
    private static void SaveAndBackUp(string path, VdfText document)
    {
        VdfDocument.SaveText(path, document.Text);
        SaveBackup(Path.GetFileName(path), document.Text);
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

    // Before a sign-in changes local.vdf, take each remembered account's saved login (ConnectCache, crc32(account
    // name)+"1") and cache it with the account, so Cached Accounts can give it back later. The block is found the way
    // Steam finds it. When local.vdf cannot be read, nothing is taken and the restore only selects the account.
    private static void PopulateConnectCacheTokens(
        SteamPaths paths,
        IReadOnlyList<CachedSteamLoginAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        Dictionary<string, string> connectCache;
        try
        {
            connectCache = VdfText.Parse(File.ReadAllText(paths.LocalVdfPath)).GetValues(ConnectCachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return;
        }

        foreach (var account in accounts)
        {
            if (!string.IsNullOrWhiteSpace(account.AccountName) &&
                connectCache.TryGetValue(Crc32.ComputeSteamAccountKey(account.AccountName), out var token) &&
                !string.IsNullOrWhiteSpace(token))
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
