using System.Diagnostics;
using Microsoft.Win32;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamPathService
{
    /// <summary>
    /// Detects the Steam install directory: returns the first candidate, in order, that really contains steam.exe; returns null if none does
    /// (no fallback to a leftover directory that "exists but has no steam.exe", which would only push a clear failure out to when steam.exe is launched).
    /// For how resolving, caching and the failure dialog fit together, see <see cref="SteamPathCoordinator"/>.
    /// </summary>
    public string? AutoDetectInstallPath()
    {
        AppLog.Info("Detecting the Steam install directory.");

        foreach (var (source, candidate) in EnumerateInstallPathCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                AppLog.Info($"  Candidate[{source}]: (empty)");
                continue;
            }

            var directory = candidate.Trim().TrimEnd('\\', '/');
            var hasExe = ContainsSteamExe(directory);
            AppLog.Info($"  Candidate[{source}]: \"{directory}\" hasSteamExe={hasExe}");

            if (hasExe)
            {
                AppLog.Info($"Detected Steam install directory: \"{directory}\"");
                return directory;
            }
        }

        AppLog.Warn("Could not detect a Steam install directory that contains steam.exe.");
        return null;
    }

    /// <summary>Whether the directory is a valid Steam install root (exists and contains steam.exe). An empty path or an exception counts as no.</summary>
    public static bool ContainsSteamExe(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory.Trim().TrimEnd('\\', '/'), "steam.exe"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a path to its real on-disk casing with backslashes. HKCU\Software\Valve\Steam\SteamPath is
    /// all lowercase with forward slashes (for example c:/program files (x86)/steam), which looks odd when shown. Each segment's real casing comes from a directory listing;
    /// if that fails (segment missing or no access), it at least fixes the separators and uppercases the drive letter. Windows paths are case-insensitive, so this is only for looks and consistency.
    /// </summary>
    public static string NormalizeInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = FixSeparators(path);
        try
        {
            var root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root))
            {
                return normalized;
            }

            var current = root.ToUpperInvariant(); // Uppercase drive letter: c:\ → C:\
            foreach (var segment in normalized[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var realName = segment;
                try
                {
                    var matches = new DirectoryInfo(current).GetFileSystemInfos(segment);
                    if (matches.Length > 0)
                    {
                        realName = matches[0].Name; // Real on-disk casing
                    }
                }
                catch
                {
                    // Segment missing, no access, or contains wildcards: keep it as is and continue.
                }

                current = Path.Combine(current, realName);
            }

            return current.TrimEnd('\\');
        }
        catch
        {
            return normalized;
        }

        static string FixSeparators(string p)
        {
            var trimmed = p.Trim().TrimEnd('\\', '/');
            try
            {
                // GetFullPath turns forward slashes into backslashes and normalizes; casing is unchanged.
                return Path.GetFullPath(trimmed).TrimEnd('\\');
            }
            catch
            {
                return trimmed.Replace('/', '\\');
            }
        }
    }

    /// <summary>Builds the full set of paths (config directory + local.vdf) from a resolved install directory.</summary>
    public SteamPaths BuildPaths(string installPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            AppLog.Error("Could not get the LocalAppData directory.");
            throw new InvalidOperationException(Loc.T("Steam_Error_LocalAppDataNotFound"));
        }

        var normalized = installPath.Trim().TrimEnd('\\', '/');
        var paths = new SteamPaths(
            normalized,
            Path.Combine(localAppData, "Steam", "local.vdf"),
            Path.Combine(normalized, "config"),
            Path.Combine(normalized, "userdata"));
        AppLog.Info($"Using Steam install directory=\"{normalized}\"  config directory=\"{paths.ConfigPath}\"  local.vdf=\"{paths.LocalVdfPath}\"");
        return paths;
    }

    // Candidate order matches the original binary, plus more reliable sources it did not cover.
    // HKCU\Software\Valve\Steam\SteamPath is the authoritative value Steam writes itself on every start.
    // The old code never read it, so users with a missing or stale HKLM InstallPath (non-admin install,
    // registry not updated after moving drives, protocol registered machine-wide in HKLM instead of HKCU) could not find Steam.
    private static IEnumerable<(string Source, string? Path)> EnumerateInstallPathCandidates()
    {
        yield return ("HKCU SteamPath", ReadRegistryString(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamPath"));

        var steamExe = ReadRegistryString(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamExe");
        yield return ("HKCU SteamExe", string.IsNullOrWhiteSpace(steamExe) ? null : Path.GetDirectoryName(steamExe));

        yield return ("ProgramFiles(x86)", GetDefaultInstallPath("ProgramFiles(x86)"));
        yield return ("ProgramFiles", GetDefaultInstallPath("ProgramFiles"));

        yield return ("HKLM64 InstallPath", ReadRegistryString(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath"));
        yield return ("HKLM32 InstallPath", ReadRegistryString(
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            @"SOFTWARE\Valve\Steam",
            "InstallPath"));

        yield return ("Running process", GetInstallPathFromRunningProcess());

        yield return ("steam protocol (HKCU)", GetInstallPathFromProtocolRegistry(RegistryHive.CurrentUser));
        yield return ("steam protocol (HKLM)", GetInstallPathFromProtocolRegistry(RegistryHive.LocalMachine));
    }

    private static string? GetDefaultInstallPath(string environmentVariable)
    {
        var root = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "Steam");
    }

    private static string? GetInstallPathFromRunningProcess()
    {
        foreach (var processName in new[] { "steam", "steamwebhelper" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            return Path.GetDirectoryName(fileName);
                        }
                    }
                    catch
                    {
                        // Some processes deny MainModule access; other candidates usually cover this.
                    }
                }
            }
        }

        return null;
    }

    private static string? GetInstallPathFromProtocolRegistry(RegistryHive hive)
    {
        var command = ReadRegistryString(
            hive,
            RegistryView.Default,
            @"Software\Classes\steam\Shell\Open\Command",
            "");

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var exePath = ExtractExecutablePath(command);
        return string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
    }

    private static string? ReadRegistryString(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            return endQuote > 1 ? command[1..endQuote] : null;
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? command[..(exeIndex + 4)] : null;
    }
}
