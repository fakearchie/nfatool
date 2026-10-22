using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed partial class SteamProcessService
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;
    private const int ErrorElevationRequired = 740;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Steam processes that can outlive steam.exe: the browser helper, the crash reporter and the Remote Play client.
    private static readonly string[] HelperProcessNames = ["steamwebhelper", "steamerrorreporter", "streaming_client"];

    public void EnsureSteamStopped(SteamPaths paths, IProgress<string>? progress)
    {
        var steamExe = Path.Combine(paths.InstallPath, "steam.exe");
        LogElevation(steamExe);

        if (!IsSteamRunning())
        {
            AppLog.Info("Steam is not running, nothing to close.");
            EndLeftoverHelpers(paths);
            return;
        }

        AppLog.Info("Steam is running, trying to close it...");
        progress?.Report(Loc.T("Steam_Progress_StoppingSteam"));

        if (File.Exists(steamExe))
        {
            try
            {
                using var shutdown = StartSteam(new ProcessStartInfo
                {
                    FileName = steamExe,
                    UseShellExecute = false
                }.WithArguments("-shutdown"), out _);

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

        // Steam writes the accounts it remembers as it closes, so it gets a while to finish before it is killed.
        for (var i = 0; i < 20; i++)
        {
            if (!IsSteamRunning())
            {
                AppLog.Info("Steam has exited.");
                EndLeftoverHelpers(paths);
                return;
            }

            Thread.Sleep(1000);
        }

        AppLog.Warn("Steam did not exit within 20 seconds, killing its processes.");
        progress?.Report(Loc.T("Steam_Progress_KillingSteam"));
        var accessDenied = KillProcesses("steam");

        for (var i = 0; i < 5; i++)
        {
            if (!IsSteamRunning())
            {
                AppLog.Info("Killed the Steam processes.");
                EndLeftoverHelpers(paths);
                return;
            }

            Thread.Sleep(1000);
        }

        // If we carry on, the old Steam writes loginusers.vdf back on exit over the new config, so the sign-in "succeeds" but the account never switches.
        AppLog.Error("Steam is still running after the kill, aborting sign-in.");

        // Windows refuses to end a Steam that runs as administrator while the loader does not, so tell the user how to get past that.
        throw new InvalidOperationException(accessDenied && !IsLoaderElevated()
            ? Loc.T("Steam_Error_SteamRunsAsAdmin")
            : Loc.T("Steam_Error_CannotStopSteam"));
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
            using var process = StartSteam(startInfo, out var throughShell);
            if (process is null)
            {
                // The shell does not always hand back a process, even when Steam was started.
                if (throughShell)
                {
                    AppLog.Info("Started Steam through the Windows prompt, no process handle returned.");
                    return;
                }

                AppLog.Error("Process.Start returned null, Windows did not create a Steam process.");
                throw new InvalidOperationException(Loc.T("Steam_Error_LaunchNoProcess"));
            }

            AppLog.Info($"Started the Steam process, PID={process.Id}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            AppLog.Error("The user declined the Windows prompt to start Steam as administrator.", ex);
            throw new InvalidOperationException(Loc.T("Steam_Error_ElevationDeclined"), ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // The raw Win32Exception message (blocked by antivirus, file not executable, access denied, etc.) matters for diagnosis.
            AppLog.Error($"Launching Steam threw an exception: \"{steamExe}\"", ex);
            throw new InvalidOperationException(Loc.Tf("Steam_Error_LaunchFailed_Format", ex.Message), ex);
        }
    }

    /// <summary>The Windows session this loader runs in, so another signed-in Windows user's Steam is left alone.</summary>
    internal static int CurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    /// <summary>
    /// Full path of a process's exe, or null when it cannot be read. Uses the limited query right, which Windows also grants
    /// for a process that runs as administrator, unlike <see cref="Process.MainModule"/>.
    /// </summary>
    internal static unsafe string? TryGetImagePath(Process process)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle.IsInvalid)
        {
            return null;
        }

        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var size = (uint)capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    // steam.exe with "Run this program as an administrator" set cannot be started through CreateProcess, which fails with ERROR_ELEVATION_REQUIRED.
    // Starting it again through the shell makes Windows show its prompt (UAC) instead.
    private static Process? StartSteam(ProcessStartInfo startInfo, out bool throughShell)
    {
        throughShell = false;
        try
        {
            return Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && !startInfo.UseShellExecute)
        {
            AppLog.Warn($"steam.exe needs administrator rights ({ex.Message}), starting it again so Windows asks for permission.");
            startInfo.UseShellExecute = true;
            throughShell = true;
            return Process.Start(startInfo);
        }
    }

    private static void LogElevation(string steamExe)
    {
        // "~ RUNASADMIN" here means Steam is set to run as administrator, which a loader without administrator rights can neither stop nor start without a Windows prompt.
        AppLog.Info(
            $"Loader runs as administrator: {IsLoaderElevated()}  steam.exe compatibility flags: HKCU=\"{ReadCompatibilityLayers(Registry.CurrentUser, steamExe) ?? "(none)"}\"  HKLM=\"{ReadCompatibilityLayers(Registry.LocalMachine, steamExe) ?? "(none)"}\"");
    }

    private static string? ReadCompatibilityLayers(RegistryKey hive, string exePath)
    {
        try
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            return key?.GetValue(exePath) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLoaderElevated()
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

    private static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    // Once steam.exe is gone, its helpers from the same Steam folder are ended too (as WareStore does), so none of them holds on to Steam's files
    // or gets in the way of the next start. Best effort: a helper that will not end is only logged and never stops the sign-in.
    private static void EndLeftoverHelpers(SteamPaths paths)
    {
        string folder;
        int sessionId;
        try
        {
            folder = Path.GetFullPath(paths.InstallPath).TrimEnd('\\') + "\\";
            sessionId = CurrentSessionId();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Skipping the check for leftover Steam processes: {ex.Message}");
            return;
        }

        foreach (var processName in HelperProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != sessionId)
                        {
                            continue;
                        }

                        var imagePath = TryGetImagePath(process);
                        if (imagePath is null || !imagePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AppLog.Info($"Ending leftover Steam process {processName} (PID={process.Id}): \"{imagePath}\"");
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Could not end leftover Steam process {processName} (PID={process.Id}), continuing: {ex.Message}");
                    }
                }
            }
        }
    }

    // Returns true when Windows refused to end a process, which means it runs as administrator and the loader does not.
    private static bool KillProcesses(string processName)
    {
        var accessDenied = false;
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
                    accessDenied |= IsAccessDenied(ex);
                }
            }
        }

        return accessDenied;
    }

    // Kill(true) reports a failure for the process tree as an AggregateException around the Win32Exception.
    private static bool IsAccessDenied(Exception ex) => ex switch
    {
        Win32Exception win32 => win32.NativeErrorCode == ErrorAccessDenied,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsAccessDenied),
        _ => false
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* exeName,
        ref uint size);
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
