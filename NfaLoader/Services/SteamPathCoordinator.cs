using Microsoft.UI.Xaml.Controls;
using NfaLoader.Localization;
using NfaLoader.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace NfaLoader.Services;

/// <summary>
/// Top-level flow for resolving the Steam install path, persisting it, and falling back when that fails:
///   1. Prefer the path already persisted in settings (valid only if it still contains steam.exe);
///   2. Stale or empty → auto-detect, and write it back to settings on success. It is cached on first launch and reused for every sign-in after that, with no new detection each time;
///   3. Still not found → show a dialog so the user picks the folder that contains steam.exe, then persist it.
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
    /// Non-UI: returns a usable Steam install folder. The persisted value wins; if it is stale or empty, auto-detect and write it back to settings. Returns null if neither works.
    /// Only does disk/registry I/O, so it is safe to call on a background thread.
    /// </summary>
    public static string? TryResolveInstallPath()
    {
        var settings = AppState.SettingsService.Load();
        var persisted = settings.SteamInstallPath;
        if (SteamPathService.ContainsSteamExe(persisted))
        {
            // Normalize to the real on-disk casing and write it back. This upgrades the all-lowercase, forward-slash paths written by old versions/HKCU once, so the path displays cleanly.
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(persisted!));
        }

        if (!string.IsNullOrWhiteSpace(persisted))
        {
            AppLog.Warn($"Persisted Steam install folder is no longer valid (steam.exe not found), detecting again: \"{persisted}\"");
        }

        var detected = _service.AutoDetectInstallPath();
        if (detected is not null)
        {
            return PersistCanonical(settings, SteamPathService.NormalizeInstallPath(detected));
        }

        return null;
    }

    // Write to disk only when the value differs from the stored one, so not every resolve hits disk; also upgrades old all-lowercase/forward-slash values to the real on-disk casing.
    private static string PersistCanonical(AppSettings settings, string canonicalPath)
    {
        if (!string.Equals(settings.SteamInstallPath, canonicalPath, StringComparison.Ordinal))
        {
            settings.SteamInstallPath = canonicalPath;
            AppState.SettingsService.Save(settings);
            AppLog.Info($"Persisted Steam install folder: \"{canonicalPath}\"");
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

        if (!SteamPathService.ContainsSteamExe(folder.Path))
        {
            AppLog.Warn($"The folder the user picked does not contain steam.exe: \"{folder.Path}\"");
            AppState.ShowStatus(Loc.T("SteamPath_Dialog_InvalidContent"), InfoBarSeverity.Error);
            return false;
        }

        var normalized = SteamPathService.NormalizeInstallPath(folder.Path);
        var settings = AppState.SettingsService.Load();
        settings.SteamInstallPath = normalized;
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

            if (SteamPathService.ContainsSteamExe(folder.Path))
            {
                var normalized = SteamPathService.NormalizeInstallPath(folder.Path);
                var settings = AppState.SettingsService.Load();
                settings.SteamInstallPath = normalized;
                AppState.SettingsService.Save(settings);
                AppLog.Info($"User picked and persisted the Steam install folder:\"{normalized}\"");
                return true;
            }

            AppLog.Warn($"The folder the user picked does not contain steam.exe: \"{folder.Path}\"");
            content = Loc.T("SteamPath_Dialog_InvalidContent");
        }
    }

    private static async Task<StorageFolder?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*"); // FolderPicker needs at least one filter, otherwise PickSingleFolderAsync returns null right away
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            // Unpackaged WinUI: the picker must be bound to the window handle, otherwise it throws a COM exception.
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Hwnd);
            return await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open the Steam folder picker.", ex);
            AppState.ShowStatus(Loc.T("SteamPath_PickFail"), InfoBarSeverity.Error);
            return null;
        }
    }
}
