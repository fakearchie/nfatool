using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>Offline readable name of an account: nickname + login account name, either may be missing.</summary>
internal sealed record OfflineAccountName(string? PersonaName, string? AccountName);

/// <summary>
/// Maps SteamID64 to a readable account name offline, for the CS2 settings sync "source account" dropdown:
/// source accounts come from scanning the userdata folder, most are not in the app history, and a bare SteamID64 is hard to recognize.
/// Name sources are merged field by field in priority order (first one wins):
///   1. Steam config/loginusers.vdf: accounts signed in and remembered on this PC (nickname + account name);
///   2. App cache cached-login.json: local accounts the loader has cached;
///   3. Steam config/config.vdf Accounts: login account name only;
///   4. userdata/&lt;accountId&gt;/config/localconfig.vdf: the nickname in your own entry in the friends section,
///      a fallback for old accounts already removed from loginusers.vdf (the file can be several MB, so it is only parsed for accounts still missing a nickname).
/// Best-effort throughout: any source that cannot be read is logged and skipped, it never makes the settings page refresh fail.
/// </summary>
internal static class SteamAccountNameService
{
    public static IReadOnlyDictionary<string, OfflineAccountName> BuildOfflineNames(
        SteamPaths paths, IReadOnlyList<Cs2SettingsSource> sources)
    {
        var map = new Dictionary<string, OfflineAccountName>(StringComparer.OrdinalIgnoreCase);

        Merge(map, () => SteamConfigService.GetLoginUsersAccounts(
                VdfDocument.LoadOrEmpty(Path.Combine(paths.ConfigPath, "loginusers.vdf")))
            .Select(account =>
            {
                // The sign-in flow writes the login name as a placeholder PersonaName into loginusers.vdf (see UpdateLoginUsersVdf),
                // which is not a real nickname: clear it so the cached-login / localconfig fallbacks get a chance to fill in the real one.
                // Accounts whose real nickname happens to equal the login name are unaffected: the display layer falls back to the account name, same result.
                if (string.Equals(account.PersonaName, account.AccountName, StringComparison.OrdinalIgnoreCase))
                {
                    account.PersonaName = null;
                }

                return account;
            }), "loginusers.vdf");
        Merge(map, () => new SteamLoginCacheService().LoadAll(), "cached-login.json");
        Merge(map, () => SteamConfigService.GetConfigAccounts(
            Path.Combine(paths.ConfigPath, "config.vdf")), "config.vdf");

        foreach (var source in sources)
        {
            map.TryGetValue(source.SteamId64, out var existing);
            if (!string.IsNullOrWhiteSpace(existing?.PersonaName))
            {
                continue;
            }

            var persona = TryReadLocalConfigPersona(paths.UserdataPath, source.AccountId);
            if (!string.IsNullOrWhiteSpace(persona))
            {
                map[source.SteamId64] = new OfflineAccountName(persona, existing?.AccountName);
            }
        }

        return map;
    }

    private static void Merge(
        Dictionary<string, OfflineAccountName> map,
        Func<IEnumerable<CachedSteamLoginAccount>> read,
        string sourceName)
    {
        List<CachedSteamLoginAccount> accounts;
        try
        {
            accounts = read().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read account name source ({sourceName}): {ex.Message}");
            return;
        }

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.SteamId))
            {
                continue;
            }

            map.TryGetValue(account.SteamId, out var existing);
            map[account.SteamId] = new OfflineAccountName(
                FirstNonEmpty(existing?.PersonaName, account.PersonaName),
                FirstNonEmpty(existing?.AccountName, account.AccountName));
        }
    }

    /// <summary>Read your own nickname from userdata/&lt;accountId&gt;/config/localconfig.vdf; returns null if it cannot be read.</summary>
    private static string? TryReadLocalConfigPersona(string? userdataPath, uint accountId)
    {
        if (string.IsNullOrWhiteSpace(userdataPath))
        {
            return null;
        }

        var path = Path.Combine(userdataPath, accountId.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(path))
        {
            return null;
        }

        // LoadOrEmpty already swallows IO/parse exceptions and logs them, so this only has to handle "structure is not the expected shape".
        var document = VdfDocument.LoadOrEmpty(path);
        if (VdfDocument.GetObject(document, "UserLocalConfigStore") is not { } store ||
            VdfDocument.GetObject(store, "friends") is not { } friends)
        {
            return null;
        }

        // Own entry: friends/<accountId>/name; some Steam versions write PersonaName directly under friends.
        if (VdfDocument.GetObject(friends, accountId.ToString()) is { } self &&
            VdfDocument.GetString(self, "name") is { } name && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return VdfDocument.GetString(friends, "PersonaName");
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;
}
