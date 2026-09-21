using System.Runtime.InteropServices;

namespace NfaLoader.Services;

/// <summary>
/// Minimal P/Invoke bindings for the Steamworks SDK flat C API (only what "force-push cfg to Steam Cloud" needs).
///
/// Important: these APIs may only be called from a short-lived helper process (<see cref="Cs2CloudPushWorker"/>), never from the main GUI process.
/// Steam treats "a process that called SteamAPI_Init as app 730" as the CS2 game process, so the store/library shows "Running" until that process exits,
/// and when the user clicks "Stop" Steam kills the process (SteamAPI_Shutdown only disconnects the API, it does not end the game session).
///
/// Requirements (if any is missing, <see cref="SteamAPI_InitFlat"/> returns non-zero or throws DllNotFound):
///   · <c>steam_api64.dll</c> is in the app folder (the Steamworks SDK redistributable, checked into the repo and copied to the output folder by the build);
///   · <c>steam_appid.txt</c> is in the app folder (contains 730 so the calling process initializes as CS2; Cs2CloudPushWorker writes it);
///   · Steam is running and signed in, and the signed-in account owns CS2 (730).
///
/// The bound interface version is STEAMREMOTESTORAGE_INTERFACE_VERSION016 (Steamworks SDK ~1.5x/1.6x).
/// If a different steam_api64.dll version is used and the accessor symbol <c>SteamAPI_SteamRemoteStorage_v016</c> does not match,
/// it throws EntryPointNotFoundException (caught and reported higher up).
/// LibraryImport is the Native AOT friendly source-generated P/Invoke; the DLL is resolved at runtime, so a missing DLL does not break the build.
///
/// Every import is marked <see cref="DefaultDllImportSearchPaths"/>(ApplicationDirectory | System32): steam_api64.dll is resolved only from the app folder + System32
/// and never falls back to the current working directory / PATH. Otherwise an attacker could plant a DLL with the same name in CWD/PATH and run arbitrary native code in this process,
/// which holds Steam refresh tokens/credentials (the real DLL is copied into the app folder by the build).
/// </summary>
internal static partial class SteamworksNative
{
    private const string Lib = "steam_api64";
    private const DllImportSearchPath SafeSearch =
        DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32;

    /// <summary>
    /// Initializes the Steamworks API (connects to the running Steam client as the app given by SteamAppId).
    /// Returns ESteamAPIInitResult: 0 = k_ESteamAPIInitResult_OK; pOutErrMsg is a char[1024] error message buffer.
    /// Note: the SDK 1.6x steam_api64.dll no longer exports a bare <c>SteamAPI_Init</c> (in the header it is an inline wrapper, and dumpbin
    /// shows no such export). Use the exported <c>SteamAPI_InitFlat</c>, otherwise it throws EntryPointNotFoundException at runtime.
    /// </summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial int SteamAPI_InitFlat(byte[] pOutErrMsg);

    /// <summary>Releases the Steamworks API. Call once for every successful <see cref="SteamAPI_InitFlat"/>.</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial void SteamAPI_Shutdown();

    /// <summary>Gets the current user's cloud storage interface (STEAMREMOTESTORAGE_INTERFACE_VERSION016).</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial IntPtr SteamAPI_SteamRemoteStorage_v016();

    /// <summary>Gets the current user interface (STEAMUSER_INTERFACE_VERSION023, same SDK ~1.5x/1.6x range as RemoteStorage v016).</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial IntPtr SteamAPI_SteamUser_v023();

    /// <summary>SteamID64 of the signed-in user (checked before writing to the cloud so another account's cloud files are never overwritten).</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);

    /// <summary>Writes a file synchronously to the current account's Steam Cloud for this app (overwrites a file with the same name).</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_FileWrite(
        IntPtr self, string pchFile, byte[] pvData, int cubData);

    /// <summary>Turns on cloud sync for the current app (account-level cloud must also be on, otherwise nothing is uploaded).</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    internal static partial void SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp(
        IntPtr self, [MarshalAs(UnmanagedType.I1)] bool bEnabled);

    /// <summary>Whether cloud sync is on for the current app.</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp(IntPtr self);

    /// <summary>Whether cloud sync is on at the account level.</summary>
    [LibraryImport(Lib)]
    [DefaultDllImportSearchPaths(SafeSearch)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount(IntPtr self);
}
