using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>
/// One report with everything support needs when an account will not sign in: which Steam the loader writes to
/// against the one Windows and the running Steam point at, Steam's sign-in settings and account files, what Steam
/// logged about signing in, and the loader's own log. Login tokens, keys and the Windows user name are removed.
/// </summary>
internal static class DiagnosticsService
{
    private static readonly Regex TokenPattern = new(@"\bey[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled);

    // Steam's own lines about signing in; the rest of its log is connection noise.
    private static readonly string[] SteamSignInMarkers =
    [
        "LogOn", "Logging on", "Using JWT", "Invalid", "Expired", "Access Denied", "Clearing in-memory token",
        "SetSteamID", "Logged Off", "ConnectionDisconnected", "not auto reconnecting",
    ];

    /// <summary>Writes the report to the Desktop (or the data folder if that fails) and returns its path.</summary>
    public static string SaveReport()
    {
        var report = Redact(BuildReport());
        var name = $"nfa-loader-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, name);
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var fallback = Path.Combine(AppState.SettingsService.AppFolderPath, name);
            Directory.CreateDirectory(AppState.SettingsService.AppFolderPath);
            File.WriteAllText(fallback, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return fallback;
        }
    }

    private static string BuildReport()
    {
        var report = new StringBuilder();
        report.AppendLine("nfa.pub Loader diagnostics");
        report.AppendLine($"Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        Section(report, "App", () => AppSection(report));
        Section(report, "Steam folder", () => SteamFolderSection(report, out _));
        Section(report, "Steam sign-in settings (registry)", () => RegistrySection(report));
        Section(report, "Steam account files", () => SteamFilesSection(report));
        Section(report, "History", () => HistorySection(report));
        Section(report, "Steam log: signing in (connection_log.txt)", () => SteamLogSection(report));
        Section(report, "Loader log (last 250 lines)", () => Tail(report, AppLog.LogFilePath, 250, null));
        return report.ToString();
    }

    private static void Section(StringBuilder report, string title, Action write)
    {
        report.AppendLine($"== {title} ==");
        try
        {
            write();
        }
        catch (Exception ex)
        {
            report.AppendLine($"(could not read: {ex.GetType().Name}: {ex.Message})");
        }

        report.AppendLine();
    }

    private static void AppSection(StringBuilder report)
    {
        report.AppendLine($"Version: {GitHubUpdateService.CurrentVersion}");
        report.AppendLine($"Windows: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        report.AppendLine($"Running as administrator: {IsElevated()}");
        report.AppendLine($"Data folder: {AppState.SettingsService.AppFolderPath}");
        report.AppendLine($"Unfinished purchase waiting: {(AppState.GetPendingPurchase() is null ? "no" : "yes")}");
    }

    private static void SteamFolderSection(StringBuilder report, out SteamPaths? paths)
    {
        paths = null;
        var persisted = SteamPathCoordinator.GetPersistedInstallPath();
        var resolved = SteamPathCoordinator.TryResolveInstallPath();
        var registryPath = Normalize(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        var registryExe = Normalize(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string);

        report.AppendLine($"Loader uses: {resolved ?? "(none found)"}");
        report.AppendLine($"Saved in the loader's settings: {persisted ?? "(none)"}");
        report.AppendLine($"Windows says Steam is at: {registryPath ?? "(not set)"}");
        report.AppendLine($"Windows says steam.exe is: {registryExe ?? "(not set)"}");

        foreach (var steam in Process.GetProcessesByName("steam"))
        {
            string where;
            try
            {
                where = steam.MainModule?.FileName ?? "(unknown)";
            }
            catch (Exception ex)
            {
                // Typically access denied: Steam runs as administrator and the loader does not.
                where = $"(cannot read: {ex.Message})";
            }

            report.AppendLine($"Running steam.exe (PID {steam.Id}): {where}");
        }

        if (resolved is not null && registryPath is not null)
        {
            var same = string.Equals(
                Path.GetFullPath(resolved).TrimEnd('\\'),
                Path.GetFullPath(registryPath).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            report.AppendLine(same
                ? "Loader folder matches Windows' Steam folder: yes"
                : "Loader folder matches Windows' Steam folder: NO, the loader writes to a different Steam than Windows starts");
        }

        if (resolved is not null)
        {
            paths = new SteamPathService().BuildPaths(resolved);
        }
    }

    private static void RegistrySection(StringBuilder report)
    {
        const string steamKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";
        report.AppendLine($"AutoLoginUser: {Registry.GetValue(steamKey, "AutoLoginUser", null) ?? "(not set)"}");
        report.AppendLine($"RememberPassword: {Registry.GetValue(steamKey, "RememberPassword", null) ?? "(not set)"}");
        report.AppendLine($"ActiveUser (account Steam is signed in to, 0 = none): {Registry.GetValue(steamKey + @"\ActiveProcess", "ActiveUser", null) ?? "(not set)"}");
    }

    private static void SteamFilesSection(StringBuilder report)
    {
        SteamFolderSection(new StringBuilder(), out var paths);
        if (paths is null)
        {
            report.AppendLine("(no Steam folder)");
            return;
        }

        var loginUsersPath = Path.Combine(paths.ConfigPath, "loginusers.vdf");
        foreach (var file in new[] { Path.Combine(paths.ConfigPath, "config.vdf"), loginUsersPath, paths.LocalVdfPath })
        {
            var info = new FileInfo(file);
            report.AppendLine(info.Exists
                ? $"{file}: {info.Length} bytes, changed {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}"
                : $"{file}: MISSING");
        }

        var connectCache = VdfDocument.GetObject(
            VdfDocument.GetObject(
                VdfDocument.GetObject(
                    VdfDocument.GetObject(
                        VdfDocument.GetObject(VdfDocument.LoadOrEmpty(paths.LocalVdfPath), "MachineUserConfigStore") ?? [],
                        "Software") ?? [],
                    "Valve") ?? [],
                "Steam") ?? [],
            "ConnectCache") ?? [];
        report.AppendLine($"Saved logins in local.vdf: {connectCache.Count}");

        var users = VdfDocument.GetObject(VdfDocument.LoadOrEmpty(loginUsersPath), "users") ?? [];
        report.AppendLine($"Accounts Steam remembers (loginusers.vdf): {users.Count}");
        foreach (var (steamId, value) in users)
        {
            if (value is not Dictionary<string, object> user)
            {
                continue;
            }

            var accountName = VdfDocument.GetString(user, "AccountName") ?? "";
            var hasLogin = accountName.Length > 0 && connectCache.ContainsKey(Crc32.ComputeSteamAccountKey(accountName));
            report.AppendLine(
                $"  {steamId}: name {(accountName == steamId ? "= SteamID" : accountName.Length == 0 ? "(empty)" : "is a login name")}, " +
                $"saved login {(hasLogin ? "yes" : "no")}, RememberPassword {VdfDocument.GetString(user, "RememberPassword") ?? "-"}, " +
                $"AllowAutoLogin {VdfDocument.GetString(user, "AllowAutoLogin") ?? "-"}, MostRecent {VdfDocument.GetString(user, "MostRecent") ?? "-"}");
        }
    }

    private static void HistorySection(StringBuilder report)
    {
        var accounts = AppState.HistoryAccounts;
        report.AppendLine($"Accounts: {accounts.Count}");
        foreach (var account in accounts)
        {
            report.AppendLine(
                $"  {account.SteamId}: name {(account.AccountName == account.SteamId ? "= SteamID" : "is a login name")}, " +
                $"token {(string.IsNullOrWhiteSpace(account.EyaToken) ? "missing" : "present")}, " +
                $"expires {account.TokenExpiresAt?.ToString("yyyy-MM-dd") ?? "-"}, checked {(account.JwtAvailable is null ? "never" : account.JwtAvailable.Value ? "valid" : "invalid")}, " +
                $"last sign-in {(account.LastLoginAt == default ? "-" : account.LastLoginAt.ToString("yyyy-MM-dd HH:mm"))}, " +
                $"from nfa.pub {(string.IsNullOrWhiteSpace(account.NfaOrder) ? "no" : "yes")}");
        }
    }

    private static void SteamLogSection(StringBuilder report)
    {
        SteamFolderSection(new StringBuilder(), out var paths);
        if (paths is null)
        {
            report.AppendLine("(no Steam folder)");
            return;
        }

        Tail(report, Path.Combine(paths.InstallPath, "logs", "connection_log.txt"), 120,
            line => SteamSignInMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    // The last lines of a file, optionally only those that match. Read shared, since Steam and the loader keep writing.
    private static void Tail(StringBuilder report, string path, int count, Func<string, bool>? keep)
    {
        if (!File.Exists(path))
        {
            report.AppendLine($"{path}: MISSING");
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Enough of the end of the file for the lines wanted, without reading a log of many megabytes whole.
        const long window = 2 * 1024 * 1024;
        if (stream.Length > window)
        {
            stream.Seek(-window, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r'));
        if (keep is not null)
        {
            lines = lines.Where(keep);
        }

        foreach (var line in lines.TakeLast(count))
        {
            report.AppendLine(line);
        }
    }

    private static string Redact(string text)
    {
        text = TokenPattern.Replace(text, "[token removed]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        if (Environment.UserName is { Length: > 2 } user)
        {
            text = Regex.Replace(text, $@"(?<=[\\/]){Regex.Escape(user)}(?=[\\/])", "%USERNAME%", RegexOptions.IgnoreCase);
        }

        return text;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string? Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', '\\');
}
