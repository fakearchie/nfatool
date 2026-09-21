using System.Diagnostics;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class SteamProcessService
{
    public void EnsureSteamStopped(SteamPaths paths, IProgress<string>? progress)
    {
        if (!IsSteamRunning())
        {
            AppLog.Info("Steam is not running, nothing to close.");
            return;
        }

        AppLog.Info("Steam is running, trying to close it...");
        progress?.Report(Loc.T("Steam_Progress_StoppingSteam"));

        var steamExe = Path.Combine(paths.InstallPath, "steam.exe");
        if (File.Exists(steamExe))
        {
            try
            {
                using var shutdown = Process.Start(new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = false
                }.WithArguments("-shutdown"));

                shutdown?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Calling steam.exe -shutdown failed: {ex.Message}");
            }
        }
        else
        {
            AppLog.Warn($"steam.exe not found while closing Steam: \"{steamExe}\"");
        }

        for (var i = 0; i < 10; i++)
        {
            if (!IsSteamRunning())
            {
                AppLog.Info("Steam has exited.");
                return;
            }

            Thread.Sleep(1000);
        }

        AppLog.Warn("Steam did not exit within 10 seconds, killing its processes.");
        progress?.Report(Loc.T("Steam_Progress_KillingSteam"));
        KillProcesses("steam");
        KillProcesses("steamwebhelper");

        for (var i = 0; i < 5; i++)
        {
            if (!IsSteamRunning())
            {
                AppLog.Info("Killed the Steam processes.");
                return;
            }

            Thread.Sleep(1000);
        }

        // If we carry on, the old Steam writes loginusers.vdf back on exit over the new config, so the sign-in "succeeds" but the account never switches.
        AppLog.Error("Steam is still running after the kill, aborting sign-in.");
        throw new InvalidOperationException(Loc.T("Steam_Error_CannotStopSteam"));
    }

    public void LaunchSteamWithLogin(SteamPaths paths, string accountName)
    {
        var steamExe = Path.Combine(paths.InstallPath, "steam.exe");
        if (!File.Exists(steamExe))
        {
            AppLog.Error($"Launch failed: steam.exe not found: \"{steamExe}\"");
            throw new InvalidOperationException(Loc.Tf("Steam_Error_SteamExeNotFound_Format", steamExe));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = paths.InstallPath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("-login");
        startInfo.ArgumentList.Add(accountName);

        AppLog.Info($"Launching Steam: \"{steamExe}\" -login {accountName}");
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                AppLog.Error("Process.Start returned null, Windows did not create a Steam process.");
                throw new InvalidOperationException(Loc.T("Steam_Error_LaunchNoProcess"));
            }

            AppLog.Info($"Started the Steam process, PID={process.Id}.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // The raw Win32Exception message (blocked by antivirus, file not executable, access denied, etc.) matters for diagnosis.
            AppLog.Error($"Launching Steam threw an exception: \"{steamExe}\"", ex);
            throw new InvalidOperationException(Loc.Tf("Steam_Error_LaunchFailed_Format", ex.Message), ex);
        }
    }

    private static bool IsSteamRunning()
    {
        return Process.GetProcessesByName("steam").Length > 0 ||
            Process.GetProcessesByName("steamwebhelper").Length > 0;
    }

    private static void KillProcesses(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    // A single process that fails to die (usually Access Denied because Steam runs as admin) is not thrown here,
                    // the caller checks again whether anything is still running and handles it there.
                    AppLog.Warn($"Killing process {processName} (PID={process.Id}) failed: {ex.Message}");
                }
            }
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
