using System.Globalization;
using System.IO;
using System.Text;

namespace NfaLoader.Services;

/// <summary>
/// Helper process for "force push CS2 settings to the cloud" (<c>NfaLoader.exe --cs2-cloud-push ...</c>, see Program.cs).
///
/// Why a separate process: the Steam client treats "the process that called SteamAPI_Init with AppID 730" as the CS2 game process,
/// and the "Running" state only clears once that process exits (SteamAPI_Shutdown only disconnects the API, it does not end the game session).
/// An early version called init inside the loader's GUI process, so CS2 showed "Running" in Steam forever,
/// and when the user clicked "Stop", Steam killed the process it thought was the game: the loader itself.
/// So init, cloud writes and shutdown all run in this short-lived process: it exits right after pushing, Steam clears "Running" within seconds,
/// and the "Stop" button can at most kill this helper, never the main window.
///
/// Protocol: argv = [switch, payloadDir, maxWaitSeconds, expectedSteamId64|"-"].
/// payloadDir holds the cfg files to push (file name = cloud name); the result is written to payloadDir\result.txt (key=value lines).
/// This process does no localization: errors go back as an i18n key (errorKey) or raw text (errorText), and the main process translates them for display.
/// </summary>
internal static class Cs2CloudPushWorker
{
    public const string CommandLineSwitch = "--cs2-cloud-push";
    public const string ResultFileName = "result.txt";
    public const string NoExpectedSteamIdToken = "-";

    private const uint Cs2AppId = 730;

    /// <summary>Helper process body. Returns the exit code: 0 = result file written (success or failure is in the file), non-zero = could not even write the result.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 4 || !Directory.Exists(args[1]))
        {
            AppLog.Warn("[cs2push] Helper process got too few arguments or the payload folder does not exist, exiting.");
            return 2;
        }

        var payloadDir = args[1];
        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxWaitSeconds))
        {
            maxWaitSeconds = 4;
        }

        ulong? expectedSteamId = null;
        if (args[3] != NoExpectedSteamIdToken &&
            ulong.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            expectedSteamId = parsed;
        }

        AppLog.Info($"[cs2push] Helper process started: maxWait={maxWaitSeconds}s expected={args[3]}");

        WorkerOutcome outcome;
        try
        {
            var files = LoadPayload(payloadDir);
            outcome = files.Count == 0
                ? WorkerOutcome.Fail("Cs2Cloud_Error_NoSource")
                : PushWithRetry(files, maxWaitSeconds, expectedSteamId);
        }
        catch (Exception ex)
        {
            AppLog.Error("[cs2push] Push threw an exception.", ex);
            outcome = WorkerOutcome.FailText(ex.Message);
        }

        var written = TryWriteResult(payloadDir, outcome);
        AppLog.Info($"[cs2push] Helper process finished: ok={outcome.Ok} pushed={outcome.Pushed} resultFile={(written ? "written" : "write failed")}");
        return written ? 0 : 3;
    }

    private sealed record WorkerOutcome(
        bool Ok, int Pushed, int PartialFailed, bool AccountCloudDisabled, string? ErrorKey, string? ErrorText)
    {
        public static WorkerOutcome Fail(string errorKey) => new(false, 0, 0, false, errorKey, null);

        public static WorkerOutcome FailText(string errorText) => new(false, 0, 0, false, null, errorText);
    }

    private static IReadOnlyList<Cs2CfgFile> LoadPayload(string payloadDir)
    {
        var result = new List<Cs2CfgFile>();
        foreach (var path in Directory.GetFiles(payloadDir))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, ResultFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new Cs2CfgFile(name, File.ReadAllBytes(path)));
        }

        return result;
    }

    // Retry every 2s for up to maxWaitSeconds (waiting for Steam to start and sign in, or to reach the target account), same semantics as the old in-process version.
    private static WorkerOutcome PushWithRetry(
        IReadOnlyList<Cs2CfgFile> files, int maxWaitSeconds, ulong? expectedSteamId)
    {
        EnsureAppIdContext();

        var attempts = Math.Max(1, maxWaitSeconds / 2);
        var lastRetryOutcome = WorkerOutcome.Fail("Cs2Cloud_Error_InitFailed");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var (retry, outcome) = TryPushOnce(files, expectedSteamId);
            if (!retry)
            {
                return outcome;
            }

            lastRetryOutcome = outcome; // Keep the last retry reason (init failed / wrong account) to tell the user after the timeout.
            if (attempt < attempts - 1)
            {
                Thread.Sleep(2000);
            }
        }

        return lastRetryOutcome;
    }

    // One attempt. Returns (retry, outcome): retry=true means "worth waiting for Steam to sign in, or reach the target account, and trying again"
    // (SteamAPI_Init failed, or the signed-in account is not the target yet);
    // everything else (DLL missing, no interface, write done, exception) is retry=false and returns outcome as is, with no more retries.
    private static (bool Retry, WorkerOutcome Outcome) TryPushOnce(
        IReadOnlyList<Cs2CfgFile> files, ulong? expectedSteamId)
    {
        var inited = false;
        try
        {
            var errMsg = new byte[1024]; // SteamErrMsg = char[k_cchMaxSteamErrMsg=1024]
            var initResult = SteamworksNative.SteamAPI_InitFlat(errMsg);
            inited = initResult == 0; // 0 = k_ESteamAPIInitResult_OK
            if (!inited)
            {
                AppLog.Warn($"[cs2push] SteamAPI_InitFlat failed (result={initResult}): {DecodeErrMsg(errMsg)}");
                return (true, WorkerOutcome.Fail("Cs2Cloud_Error_InitFailed"));
            }

            // Push at sign-in: check the signed-in account is the target, and keep waiting if not (better to time out than write to the wrong account).
            if (expectedSteamId is { } expected && !IsLoggedInAs(expected))
            {
                return (true, WorkerOutcome.Fail("Cs2Cloud_Error_WrongAccount"));
            }

            var remoteStorage = SteamworksNative.SteamAPI_SteamRemoteStorage_v016();
            if (remoteStorage == IntPtr.Zero)
            {
                return (false, WorkerOutcome.Fail("Cs2Cloud_Error_NoInterface"));
            }

            // App-level cloud (CS2 Properties > General "Keep game saves in the Steam Cloud", registry Apps\730\Cloud): the SDK can turn this layer on,
            // so turn it on here and read it back to confirm (if it did not stick, the account-level master switch is usually off, see below).
            SteamworksNative.SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(remoteStorage, true);
            if (!SteamworksNative.SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(remoteStorage))
            {
                AppLog.Warn("[cs2push] Asked to turn on CS2 app-level cloud, but it still reads back as off (usually the account-level master switch is off, which makes the app-level setting void).");
            }

            // Account-level cloud is the master switch in Steam Settings > Cloud. It is a server-side account setting that neither the SDK nor local code can turn on;
            // while it is off, FileWrite only writes locally and nothing reaches the cloud. All we can do is detect it and report it, and the UI tells the user to flip that one switch.
            var accountCloudDisabled =
                !SteamworksNative.SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(remoteStorage);
            if (accountCloudDisabled)
            {
                AppLog.Warn("[cs2push] This account has account-level Steam Cloud turned off (Steam Settings > Cloud > Enable Steam Cloud), so FileWrite only writes locally and nothing reaches the cloud.");
            }

            var pushed = 0;
            foreach (var file in files)
            {
                if (SteamworksNative.SteamAPI_ISteamRemoteStorage_FileWrite(
                        remoteStorage, file.Name, file.Data, file.Data.Length))
                {
                    pushed++;
                }
                else
                {
                    AppLog.Warn($"[cs2push] CS2 cloud FileWrite failed: {file.Name}");
                }
            }

            AppLog.Info($"[cs2push] CS2 cloud force push: wrote {pushed}/{files.Count} files (accountCloudDisabled={accountCloudDisabled}).");
            if (pushed == 0)
            {
                return (false, WorkerOutcome.Fail("Cs2Cloud_Error_WriteFailed"));
            }

            // Some files failed to write: still counts as pushed, but tell the user plainly it is incomplete.
            return (false, new WorkerOutcome(true, pushed, files.Count - pushed, accountCloudDisabled, null, null));
        }
        catch (DllNotFoundException)
        {
            return (false, WorkerOutcome.Fail("Cs2Cloud_Error_DllMissing"));
        }
        catch (EntryPointNotFoundException)
        {
            return (false, WorkerOutcome.Fail("Cs2Cloud_Error_DllVersion"));
        }
        catch (Exception ex)
        {
            AppLog.Error("[cs2push] CS2 cloud force push threw an exception.", ex);
            return (false, WorkerOutcome.FailText(ex.Message));
        }
        finally
        {
            // Shutdown only disconnects the API; the "Running" state clears when this process exits right after.
            // No need to clear the environment variables: this process starts no child processes and they die with it.
            if (inited)
            {
                SteamworksNative.SteamAPI_Shutdown();
            }
        }
    }

    // Check whether the signed-in Steam account's SteamID64 matches the expected target. If the ISteamUser interface is unavailable (wrong DLL version etc.), return true and let it through,
    // so "missing the ability to check does not break the feature"; it then falls back to "push to whichever account is signed in".
    private static bool IsLoggedInAs(ulong expectedSteamId64)
    {
        try
        {
            var user = SteamworksNative.SteamAPI_SteamUser_v023();
            if (user == IntPtr.Zero)
            {
                AppLog.Warn("[cs2push] Could not get the ISteamUser interface, skipping the signed-in account check.");
                return true;
            }

            var actual = SteamworksNative.SteamAPI_ISteamUser_GetSteamID(user);
            if (actual == expectedSteamId64)
            {
                return true;
            }

            AppLog.Warn($"[cs2push] Signed-in account ({actual}) does not match the target account ({expectedSteamId64}), not writing to the cloud yet, still waiting.");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            AppLog.Warn("[cs2push] steam_api64.dll has no ISteamUser v023 export, skipping the signed-in account check.");
            return true;
        }
    }

    // Make this process initialize Steamworks as CS2: set the SteamAppId environment variable (the SDK reads it first, the most reliable option),
    // and write a steam_appid.txt to the exe folder as a fallback (some SDK versions read that file from the working directory,
    // and the main process sets the working directory to the exe folder when it starts this helper).
    private static void EnsureAppIdContext()
    {
        var appId = Cs2AppId.ToString(CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", appId);
            Environment.SetEnvironmentVariable("SteamGameId", appId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] Failed to set the SteamAppId environment variable: {ex.Message}");
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, appId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] Failed to write steam_appid.txt: {ex.Message}");
        }
    }

    // Decode the error text from SteamErrMsg (char[1024], ASCII, null-terminated) for log diagnostics.
    private static string DecodeErrMsg(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static bool TryWriteResult(string payloadDir, WorkerOutcome outcome)
    {
        try
        {
            var builder = new StringBuilder()
                .Append("ok=").Append(outcome.Ok ? '1' : '0').Append('\n')
                .Append("pushed=").Append(outcome.Pushed.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("partialFailed=").Append(outcome.PartialFailed.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("accountCloudDisabled=").Append(outcome.AccountCloudDisabled ? '1' : '0').Append('\n');
            if (!string.IsNullOrEmpty(outcome.ErrorKey))
            {
                builder.Append("errorKey=").Append(outcome.ErrorKey).Append('\n');
            }

            if (!string.IsNullOrEmpty(outcome.ErrorText))
            {
                builder.Append("errorText=").Append(SingleLine(outcome.ErrorText)).Append('\n');
            }

            // No BOM: with a BOM the main process would parse the first line as "﻿ok" and lose the ok key.
            File.WriteAllText(
                Path.Combine(payloadDir, ResultFileName), builder.ToString(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[cs2push] Failed to write the result file: {ex.Message}");
            return false;
        }
    }

    // The result file is parsed line by line, so squash multi-line exception messages into one line.
    private static string SingleLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
