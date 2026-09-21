using System.Diagnostics;
using System.Globalization;
using System.IO;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>A CS2 config file to force-push (cloud file name + content bytes).</summary>
internal sealed record Cs2CfgFile(string Name, byte[] Data);

/// <summary>A candidate "settings source" account: its local userdata has a CS2 config.</summary>
internal sealed record Cs2SettingsSource(string SteamId64, uint AccountId);

/// <summary>Push result: success, number of files written, failure reason, an "account-level Steam Cloud is off for this account" flag, and the number of files that failed in a partial write.</summary>
internal sealed record Cs2CloudPushResult(
    bool Ok, int Pushed, string? Error, bool AccountCloudDisabled = false, int PartialFailed = 0);

/// <summary>
/// "Cloud force-push" sync for CS2 (AppID 730) settings.
///
/// Approach: crosshair, sensitivity, viewmodel, key binds and so on are Source2 client convars, so copying them across accounts goes through an official SDK cloud force-push:
///   1. Read cs2_user_*.vcfg from the source account's userdata/&lt;accountId&gt;/730/<b>remote</b>. This is the local mirror of the cloud files,
///      and the file names are the cloud file names (tested: the cloud has cs2_user_convars.vcfg / cs2_user_keys.vcfg, which differ from the
///      cs2_user_convars_0_slot0.vcfg name in local/cfg, so we must read remote and not local/cfg);
///   2. After the target account signs in, use the Steamworks cloud API (<see cref="SteamworksNative"/>) to FileWrite them under the same names to
///      the CS2 cloud of the "currently signed-in account". Steam handles delivering them; we do not hand-roll any cloud logic.
/// Note: the video settings file cs2_video.txt is not in the cloud (it is a per-machine local file), so cloud sync cannot overwrite it.
///
/// Steamworks calls do not run in this process: Steam treats "a process that called SteamAPI_Init as 730" as the CS2 game process,
/// the "Running" status is not cleared until that process exits, and clicking "Stop" gets it killed by Steam. So ForcePush only writes the files to push
/// to a temp folder, then starts a short-lived helper process from our own exe (<see cref="Cs2CloudPushWorker"/>) to do init, write to the cloud and exit.
///
/// Requirements: the target account owns CS2, Steam is signed in, and steam_api64.dll is in the run folder (see SteamworksNative).
/// Best-effort throughout: any failure is only logged or shown as a notice and never interrupts the main sign-in flow.
/// </summary>
internal sealed class Cs2CloudService
{
    private const string Cs2AppFolder = "730";
    // Personal account SteamID64 = this base + 32-bit accountId (accountId is the userdata folder name).
    private const ulong SteamId64Base = 76561197960265728UL;

    // Run one push helper at a time: when a background push after sign-in and "Push now" on the settings page overlap, they run one after the other,
    // so two Steamworks sessions as 730 never write the same cloud file at once with results that cannot be told apart.
    private static readonly object PushGate = new();

    // Generation number for sign-in pushes (last writer wins): each new sign-in push increments it; an older push holding the lock polls, sees the generation changed
    // and kills its helper at once to step aside. Otherwise, when switching accounts quickly, the old account's push (its target is already signed out and bound to fail) holds the lock
    // for tens of seconds and starves the newest push, the only one that can succeed, until it times out and gives up. "Push now" does not take part in generation preemption.
    private static long _loginPushGeneration;

    /// <summary>Lists candidate source accounts that "have CS2 settings in the cloud" (cs2_user_*.vcfg under remote), for the source dropdown on the settings page.</summary>
    public IReadOnlyList<Cs2SettingsSource> EnumerateSources(string? userdataPath)
    {
        var result = new List<Cs2SettingsSource>();
        if (string.IsNullOrWhiteSpace(userdataPath) || !Directory.Exists(userdataPath))
        {
            return result;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(userdataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Failed to enumerate userdata folder: {userdataPath}, {ex.Message}");
            return result;
        }

        foreach (var dir in dirs)
        {
            if (!uint.TryParse(Path.GetFileName(dir), out var accountId) || accountId == 0)
            {
                continue;
            }

            if (!HasCs2Config(RemoteDir(userdataPath, accountId)))
            {
                continue;
            }

            var steamId64 = (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
            result.Add(new Cs2SettingsSource(steamId64, accountId));
        }

        return result;
    }

    /// <summary>
    /// Reads the cloud CS2 settings files (cs2_user_convars.vcfg / cs2_user_keys.vcfg) under the source account's remote folder as the content to force-push;
    /// the file names are the cloud names, so FileWrite them as they are. Returns empty when there is no source or no config.
    /// </summary>
    public IReadOnlyList<Cs2CfgFile> ReadSourceCfgFiles(string userdataPath, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64) || !TryAccountId(sourceSteamId64, out var accountId))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var remoteDir = RemoteDir(userdataPath, accountId);
        if (!Directory.Exists(remoteDir))
        {
            return Array.Empty<Cs2CfgFile>();
        }

        var result = new List<Cs2CfgFile>();
        try
        {
            // Only take user settings (convars = crosshair/sensitivity/viewmodel etc., keys = key binds); skip account-level state such as socache.dt/voice_ban.dt.
            foreach (var path in Directory.GetFiles(remoteDir, "cs2_user_*.vcfg"))
            {
                try
                {
                    result.Add(new Cs2CfgFile(Path.GetFileName(path), File.ReadAllBytes(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Failed to read source CS2 cloud file: {path}, {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Failed to enumerate source CS2 remote folder: {remoteDir}, {ex.Message}");
        }

        return result;
    }

    /// <summary>Called at sign-in (on a background thread): force-pushes the source account's config to the cloud of the account that just signed in. Swallows all exceptions.</summary>
    public void PushSourceForLogin(
        SteamPaths paths, string? sourceSteamId64, string targetSteamId64, IProgress<string>? progress)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSteamId64) ||
                !TryAccountId(sourceSteamId64, out var sourceId) ||
                !TryAccountId(targetSteamId64, out var targetId) ||
                sourceId == targetId)
            {
                return;
            }

            var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
            if (files.Count == 0)
            {
                return;
            }

            progress?.Report(Loc.T("Cs2Cloud_Progress_Pushing"));
            // Steam needs a few seconds to finish signing in after login; ForcePush retries and waits internally.
            // Pass the target SteamID: before writing to the cloud, check that the signed-in account really is it. If the wrong account signed in, give up rather than overwrite someone else's cloud.
            var result = ForcePush(files, maxWaitSeconds: 40, expectedSteamId64: targetSteamId64);
            progress?.Report(DescribeResult(result));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CS2 cloud push (at sign-in) failed, ignoring: {ex.Message}");
        }
    }

    /// <summary>"Push now" on the settings page: force-pushes the source account's config to the currently signed-in account's cloud (assumes Steam is already running and signed in).</summary>
    public Cs2CloudPushResult PushSourceNow(SteamPaths paths, string? sourceSteamId64)
    {
        if (string.IsNullOrWhiteSpace(sourceSteamId64))
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSourceSelected"));
        }

        var files = ReadSourceCfgFiles(paths.UserdataPath, sourceSteamId64);
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        return ForcePush(files, maxWaitSeconds: 4);
    }

    /// <summary>Turns a push result into user-facing text (success / success but account cloud is off / failure).</summary>
    public static string DescribeResult(Cs2CloudPushResult result)
    {
        if (!result.Ok)
        {
            return Loc.Tf("Cs2Cloud_Progress_Failed_Format", result.Error ?? string.Empty);
        }

        if (result.PartialFailed > 0)
        {
            return Loc.Tf("Cs2Cloud_Progress_Partial_Format", result.Pushed, result.Pushed + result.PartialFailed);
        }

        return result.AccountCloudDisabled
            ? Loc.Tf("Cs2Cloud_Progress_DoneNoCloud_Format", result.Pushed)
            : Loc.Tf("Cs2Cloud_Progress_Done_Format", result.Pushed);
    }

    /// <summary>
    /// Force-pushes the given files to the CS2 (730) cloud of the "currently signed-in account": the files go to a temp folder and a short-lived helper process does the work
    /// (Steamworks init must happen in a process that exits right away, or Steam keeps showing CS2 as "Running"; see the class comment).
    /// Within <paramref name="maxWaitSeconds"/> the helper retries SteamAPI_Init every 2s (waiting for Steam to finish signing in).
    /// When <paramref name="expectedSteamId64"/> is not empty, it checks before writing that the signed-in account is that SteamID;
    /// if not, it counts as "not yet signed in to the target account" and keeps waiting, and even on timeout it never writes to another account (so it cannot overwrite someone else's cloud settings).
    /// "Push now" passes null, meaning push to whichever account is signed in.
    /// </summary>
    public Cs2CloudPushResult ForcePush(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, string? expectedSteamId64 = null)
    {
        if (files.Count == 0)
        {
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_NoSource"));
        }

        // The sign-in path (with a target account) takes a generation number first: an older sign-in push holding the lock notices within ~1s and steps aside,
        // so the TryEnter below almost always succeeds for the newest sign-in push.
        long? generation = null;
        if (!string.IsNullOrWhiteSpace(expectedSteamId64))
        {
            generation = Interlocked.Increment(ref _loginPushGeneration);
        }

        // Try the lock with a time limit instead of queuing forever: a background sign-in push can hold the lock for tens of seconds (the helper waits for Steam to finish signing in),
        // and if "Push now" blocked forever during that time it would look like a hang with no feedback for minutes. If we cannot get the lock, tell the user to try again later.
        if (!Monitor.TryEnter(PushGate, TimeSpan.FromSeconds(maxWaitSeconds)))
        {
            AppLog.Warn("Another CS2 cloud push is in progress, not waiting this time.");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_PushBusy"));
        }

        try
        {
            return RunPushHelper(files, maxWaitSeconds, expectedSteamId64, generation);
        }
        finally
        {
            Monitor.Exit(PushGate);
        }
    }

    // Whether this push has been replaced by a newer sign-in push (generation=null means it does not take part in preemption).
    private static bool IsSuperseded(long? generation) =>
        generation is { } gen && Interlocked.Read(ref _loginPushGeneration) != gen;

    // Write payload → start the helper from our own exe → wait for it to exit → parse result.txt. The temp folder is cleaned up in finally.
    private static Cs2CloudPushResult RunPushHelper(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, string? expectedSteamId64, long? generation)
    {
        // A newer sign-in arrived while we waited for the lock: this push's target account will never sign in, so step aside without starting a process.
        if (IsSuperseded(generation))
        {
            AppLog.Info("CS2 cloud push was replaced by a newer sign-in (helper not started).");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_Superseded"));
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            AppLog.Warn("Could not get the current process exe path, cannot start the CS2 cloud push helper.");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperLaunchFailed"));
        }

        // If the GUI is closed during a push (the sign-in push is a background Task and finally is not guaranteed to run), the payload folder is orphaned,
        // so sweep old leftovers here. The 1 hour threshold is far beyond the helper's ~70s lifetime, so it will not delete another instance's live folder.
        SweepStalePayloadDirs();

        var payloadDir = Path.Combine(Path.GetTempPath(), "nfa.pub Loader", "cs2push-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(payloadDir);
            foreach (var file in files)
            {
                // Name comes from enumerating the source folder and should be a bare file name; run it through GetFileName again to guard against path joining surprises.
                File.WriteAllBytes(Path.Combine(payloadDir, Path.GetFileName(file.Name)), file.Data);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // steam_appid.txt falls back to resolving against the working directory, so the helper's working directory is always the exe folder.
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Cs2CloudPushWorker.CommandLineSwitch);
            startInfo.ArgumentList.Add(payloadDir);
            startInfo.ArgumentList.Add(maxWaitSeconds.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(expectedSteamId64)
                ? Cs2CloudPushWorker.NoExpectedSteamIdToken
                : expectedSteamId64.Trim());

            AppLog.Info($"Starting CS2 cloud push helper: {files.Count} file(s), maxWait={maxWaitSeconds}s.");
            using var helper = Process.Start(startInfo);
            if (helper is null)
            {
                AppLog.Warn("Process.Start returned null, the system did not create the CS2 cloud push helper.");
                return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperLaunchFailed"));
            }

            // Poll in 1s steps: the helper has its own maxWaitSeconds retry wait, and this adds margin for exiting and writing the result;
            // if a newer sign-in push arrives meanwhile (generation number changed), kill this helper at once to step aside: its target account is already signed out,
            // and waiting longer would only hold the lock and starve the newest push.
            var deadline = Environment.TickCount64 + (maxWaitSeconds + 30) * 1000L;
            while (!helper.WaitForExit(1000))
            {
                if (IsSuperseded(generation))
                {
                    AppLog.Info("A newer sign-in push arrived, stopping this CS2 cloud push helper to step aside.");
                    TryKillHelper(helper);
                    return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_Superseded"));
                }

                if (Environment.TickCount64 >= deadline)
                {
                    AppLog.Warn("CS2 cloud push helper timed out without exiting, killing it.");
                    TryKillHelper(helper);
                    return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperTimeout"));
                }
            }

            return ReadHelperResult(Path.Combine(payloadDir, Cs2CloudPushWorker.ResultFileName));
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to dispatch the CS2 cloud push helper.", ex);
            return new Cs2CloudPushResult(false, 0, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(payloadDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                AppLog.Warn($"Failed to clean up CS2 cloud push temp folder: {payloadDir}, {ex.Message}");
            }
        }
    }

    // Deletes leftover cs2push-* folders under %TEMP%\nfa.pub Loader created more than 1 hour ago. Best-effort throughout.
    private static void SweepStalePayloadDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "nfa.pub Loader");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(root, "cs2push-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                    {
                        Directory.Delete(dir, recursive: true);
                        AppLog.Info($"Swept leftover CS2 push temp folder: {dir}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn($"Failed to sweep leftover CS2 push temp folder: {dir}, {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Failed to enumerate CS2 push temp folders: {ex.Message}");
        }
    }

    private static void TryKillHelper(Process helper)
    {
        try
        {
            helper.Kill(entireProcessTree: true);
            helper.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to stop CS2 cloud push helper: {ex.Message}");
        }
    }

    // Parses the key=value result file the helper writes back; errorKey is turned into localized text here (the helper does no localization).
    private static Cs2CloudPushResult ReadHelperResult(string resultPath)
    {
        string[] lines;
        try
        {
            if (!File.Exists(resultPath))
            {
                AppLog.Warn("CS2 cloud push helper exited but left no result file (it may have crashed or been blocked by security software).");
                return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperNoResult"));
            }

            lines = File.ReadAllLines(resultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Failed to read CS2 cloud push result file: {ex.Message}");
            return new Cs2CloudPushResult(false, 0, Loc.T("Cs2Cloud_Error_HelperNoResult"));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('\uFEFF'); // Tolerate a BOM so the first line's key name does not carry an invisible character.
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        var ok = values.GetValueOrDefault("ok") == "1";
        _ = int.TryParse(
            values.GetValueOrDefault("pushed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pushed);
        _ = int.TryParse(
            values.GetValueOrDefault("partialFailed"), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var partialFailed);
        var accountCloudDisabled = values.GetValueOrDefault("accountCloudDisabled") == "1";

        string? error = null;
        if (!ok)
        {
            var errorKey = values.GetValueOrDefault("errorKey");
            error = !string.IsNullOrWhiteSpace(errorKey)
                ? Loc.T(errorKey)
                : values.GetValueOrDefault("errorText") is { Length: > 0 } text
                    ? text
                    : Loc.T("Cs2Cloud_Error_HelperNoResult");
        }

        return new Cs2CloudPushResult(ok, pushed, error, accountCloudDisabled, partialFailed);
    }

    // Local mirror folder of the account's CS2 cloud files: userdata/<accountId>/730/remote (file names are the cloud names).
    private static string RemoteDir(string userdataPath, uint accountId) => Path.Combine(
        userdataPath, accountId.ToString(CultureInfo.InvariantCulture), Cs2AppFolder, "remote");

    private static bool HasCs2Config(string remoteDir)
    {
        try
        {
            return Directory.Exists(remoteDir) && Directory.EnumerateFiles(remoteDir, "cs2_user_*.vcfg").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // userdata folder name = low 32 bits of the SteamID64 (accountId).
    private static bool TryAccountId(string steamId64, out uint accountId)
    {
        accountId = 0;
        if (!ulong.TryParse(steamId64.Trim(), out var id))
        {
            return false;
        }

        accountId = (uint)(id & 0xFFFFFFFF);
        return accountId != 0;
    }

}
