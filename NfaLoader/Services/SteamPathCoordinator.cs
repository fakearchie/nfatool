using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

/// <summary>
/// Top-level flow for resolving the Steam install path, persisting it, and falling back when that fails:
///   1. A folder the user picked by hand wins while it still contains steam.exe;
///   2. Otherwise the Steam the user actually runs (the running steam.exe, then HKCU SteamPath) wins over the saved folder, so a moved or reinstalled Steam is followed;
///   3. Then the saved folder (valid only if it still contains steam.exe), then auto-detection, written back to settings on success;
///   4. Still not found → show a dialog so the user picks the folder that contains steam.exe, then persist it.
/// The non-UI <see cref="TryResolveInstallPath"/> / <see cref="ResolvePathsOrThrow"/> can be called on a background thread;
/// <see cref="EnsureResolvedAsync"/> and its dialog must only be called on the UI thread.
/// </summary>
internal static class SteamPathCoordinator
{
    private static readonly SteamPathService _service = new();

    // Only one "pick manually" dialog at a time (the startup check and the pre-sign-in check can fire together;
    // a second ShowAsync on the same XamlRoot throws).
    private static readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>
    /// Non-UI: returns a usable Steam install folder and writes it back to settings. Order: a folder picked by hand, the Steam the user actually runs,
    /// the saved folder, auto-detection. Returns null if none works.
    /// Only does disk/registry/process I/O, so it is safe to call on a background thread.
    /// </summary>
    public static string? TryResolveInstallPath()
    {
        var settings = AppState.SettingsService.Load();
        var persisted = settings.SteamInstallPath;
        var persistedValid = SteamPathService.ContainsSteamExe(persisted);

        // A saved folder that was only found automatically goes stale when Steam is moved or installed again elsewhere while the old folder keeps steam.exe.
        // Then the loader would stop the user's Steam and write the sign-in into a Steam they no longer use, so Steam's own location wins.
        var live = SteamPathService.FindLiveInstallPath(out var liveSource);
        var liveNormalized = live is null ? null : SteamPathService.NormalizeInstallPath(live);
        var differs = liveNormalized is not null && !string.IsNullOrWhiteSpace(persisted) &&
            !string.Equals(SteamPathService.NormalizeInstallPath(persisted), liveNormalized, StringComparison.OrdinalIgnoreCase);

        // Settings saved before the flag existed load it as null. Those versions kept the saved folder while it had steam.exe and never recorded a pick by hand,
        // so a saved folder that still works but is not the Steam in use is treated as picked by hand. Decided once: PersistCanonical below saves the answer.
        var userPicked = settings.SteamInstallPathUserPicked ?? (persistedValid && differs);
        if (settings.SteamInstallPathUserPicked is null && userPicked)
        {
            AppLog.Info($"Settings predate the picked-by-hand flag, keeping the saved Steam folder \"{persisted}\" (Steam last ran from \"{liveNormalized}\").");
        }

        if (liveNormalized is not null)
        {
            if (!(userPicked && persistedValid))
            {
                if (differs)
                {
                    AppLog.Warn($"Saved Steam folder \"{persisted}\" is not the Steam in use, switching to \"{liveNormalized}\" (from {liveSource}).");
                }

                return PersistCanonical(settings, liveNormalized, userPicked: false);
            }

            if (differs)
            {
                AppLog.Info($"Keeping the Steam folder picked by hand \"{persisted}\", although Steam itself is at \"{liveNormalized}\" (from {liveSource}).");
            }
        }

        if (persistedValid)
        {
            // Normalize to the real on-disk casing and write it back. This upgrades the all-lowercase, forward-slash paths written by old versions/HKCU once, so the path displays cleanly.
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(persisted!), userPicked);
        }

        if (!string.IsNullOrWhiteSpace(persisted))
        {
            AppLog.Warn($"Persisted Steam install folder is no longer valid (steam.exe not found), detecting again: \"{persisted}\"");
        }

        var detected = _service.AutoDetectInstallPath();
        if (detected is not null)
        {
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(detected), userPicked: false);
        }

        return null;
    }

    // Write to disk only when the value differs from the stored one, so not every resolve hits disk; also upgrades old all-lowercase/forward-slash values to the real on-disk casing.
    private static string PersistCanonical(AppSettings settings, string canonicalPath, bool userPicked)
    {
        if (!string.Equals(settings.SteamInstallPath, canonicalPath, StringComparison.Ordinal) ||
            settings.SteamInstallPathUserPicked != userPicked)
        {
            settings.SteamInstallPath = canonicalPath;
            settings.SteamInstallPathUserPicked = userPicked;
            AppState.SettingsService.Save(settings);
            AppLog.Info($"Persisted Steam install folder: \"{canonicalPath}\" (picked by hand: {userPicked})");
        }

        return canonicalPath;
    }

    /// <summary>
    /// Non-UI: resolves the full <see cref="SteamPaths"/>; throws if it cannot.
    /// This is the safety net for the sign-in flow (background thread). Normally the UI has already resolved and persisted the path with <see cref="EnsureResolvedAsync"/> before signing in.
    /// </summary>
    public static SteamPaths ResolvePathsOrThrow()
    {
        var installPath = TryResolveInstallPath()
            ?? throw new InvalidOperationException(Loc.T("SteamPath_Error_NotResolved"));
        return _service.BuildPaths(installPath);
    }

    /// <summary>
    /// UI: makes sure a usable Steam install folder is resolved. Tries the persisted value/auto-detection first (background thread), then shows a dialog so the user can pick.
    /// Returns true = ready, false = the user cancelled and nothing is set. Can be called at startup and before every sign-in.
    /// </summary>
    public static async Task<bool> EnsureResolvedAsync()
    {
        if (await Task.Run(TryResolveInstallPath) is not null)
        {
            return true;
        }

        await _promptGate.WaitAsync();
        try
        {
            // Check again inside the gate: another caller (such as the startup check) may have resolved it already, so don't show a second dialog.
            if (await Task.Run(TryResolveInstallPath) is not null)
            {
                return true;
            }

            return await PromptForManualPathAsync();
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <summary>The currently persisted Steam install folder (shown on the settings page, normalized to the real on-disk casing); null if not set.</summary>
    public static string? GetPersistedInstallPath()
    {
        var path = AppState.SettingsService.Load().SteamInstallPath;
        return string.IsNullOrWhiteSpace(path) ? null : SteamPathService.NormalizeInstallPath(path);
    }

    /// <summary>
    /// UI: the settings page "Change Steam path" action. Opens the folder picker directly (no "not found" dialog, since the user chose to change it),
    /// and persists the folder once it is confirmed to contain steam.exe. Returns whether the change succeeded. Lets users with several Steam installs pick which one to use.
    /// </summary>
    public static async Task<bool> PickAndPersistManuallyAsync()
    {
        var folder = await PickFolderAsync();
        if (folder is null)
        {
            return false; // User cancelled
        }

        if (!SteamPathService.ContainsSteamExe(folder))
        {
            AppLog.Warn($"The folder the user picked does not contain steam.exe: \"{folder}\"");
            AppState.ShowStatus(Loc.T("SteamPath_Dialog_InvalidContent"), InfoBarSeverity.Error);
            return false;
        }

        var normalized = SteamPathService.NormalizeInstallPath(folder);
        var settings = AppState.SettingsService.Load();
        settings.SteamInstallPath = normalized;
        settings.SteamInstallPathUserPicked = true;
        AppState.SettingsService.Save(settings);
        AppLog.Info($"User changed the Steam install folder on the settings page: \"{normalized}\"");
        return true;
    }

    private static async Task<bool> PromptForManualPathAsync()
    {
        var xamlRoot = MainWindow.Instance?.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            AppLog.Warn("The Steam folder needs to be picked manually, but the window is not ready yet. Skipping the dialog (the user will be asked again at sign-in).");
            return false;
        }

        var content = Loc.T("SteamPath_Dialog_Content");
        while (true)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.T("SteamPath_Dialog_Title"),
                Content = content,
                PrimaryButtonText = Loc.T("SteamPath_Dialog_Pick"),
                CloseButtonText = Loc.T("SteamPath_Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                AppLog.Info("User cancelled picking the Steam install folder.");
                return false;
            }

            var folder = await PickFolderAsync();
            if (folder is null)
            {
                // Picker was cancelled: go back to the dialog and let the user pick again or cancel for good.
                continue;
            }

            if (SteamPathService.ContainsSteamExe(folder))
            {
                var normalized = SteamPathService.NormalizeInstallPath(folder);
                var settings = AppState.SettingsService.Load();
                settings.SteamInstallPath = normalized;
                settings.SteamInstallPathUserPicked = true;
                AppState.SettingsService.Save(settings);
                AppLog.Info($"User picked and persisted the Steam install folder:\"{normalized}\"");
                return true;
            }

            AppLog.Warn($"The folder the user picked does not contain steam.exe: \"{folder}\"");
            content = Loc.T("SteamPath_Dialog_InvalidContent");
        }
    }

    // Returns the picked folder's path, or null when the user cancelled or the picker failed.
    private static async Task<string?> PickFolderAsync()
    {
        try
        {
            // The Windows App SDK picker, unlike Windows.Storage.Pickers, also opens when the unpackaged loader runs as administrator.
            var picker = new FolderPicker(Win32Interop.GetWindowIdFromWindow(MainWindow.Hwnd))
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            var result = await picker.PickSingleFolderAsync();
            return string.IsNullOrEmpty(result?.Path) ? null : result.Path;
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open the Steam folder picker.", ex);
            AppState.ShowStatus(Loc.T("SteamPath_PickFail"), InfoBarSeverity.Error);
            return null;
        }
    }
}
