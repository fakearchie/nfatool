using System.Text.RegularExpressions;
using Microsoft.Win32;
using NfaLoader.Localization;

namespace NfaLoader.Services;

internal static class CsGcSession
{
    public const uint Cs2AppId = 730;
    public const uint ClientHello = 4006;
    public const uint ClientWelcome = 4004;

    // The last version known at release time, used only when it cannot be looked up.
    private const uint FallbackClientVersion = 2_000_933;

    // SteamDB tracks every CS2 update, so its copy of steam.inf always carries the version the GC currently expects.
    private const string TrackedSteamInfUrl =
        "https://raw.githubusercontent.com/SteamDatabase/GameTracking-CS2/master/game/csgo/steam.inf";

    private static readonly TimeSpan VersionCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly HttpClient VersionHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly SemaphoreSlim VersionLock = new(1, 1);
    private static uint? _cachedVersion;
    private static DateTimeOffset _cachedAt;

    public static Task<byte[]> ConnectAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return RequestWelcomeAsync(
            cmClient,
            TimeSpan.FromSeconds(20),
            cancellationToken);
    }

    public static async Task<byte[]> RequestWelcomeAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // The GC never welcomes a client that reports an outdated version: it stays silent until the timeout.
        var version = await GetClientVersionAsync(cancellationToken);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var welcomeTask = cmClient.WaitForGcMessageAsync(
                Cs2AppId,
                ClientWelcome,
                TimeSpan.FromSeconds(4),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                Cs2AppId,
                ClientHello,
                EncodeClientHello(version),
                cancellationToken);

            try
            {
                var message = await welcomeTask;
                return message.Payload;
            }
            catch (TimeoutException)
            {
            }
        }

        AppLog.Warn($"The CS2 GC did not answer within {timeout.TotalSeconds:0} s (client version {version}).");
        throw new TimeoutException(Loc.T("Cs_Gc_ConnectTimeout"));
    }

    public static byte[] EncodeClientHello(uint clientVersion)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(1, clientVersion);
            writer.WriteUInt32(3, 0);
            writer.WriteUInt32(4, 0);
            writer.WriteUInt32(9, 0);
        });
    }

    public static uint GetAccountId(ulong steamId64) =>
        (uint)(steamId64 & 0xFFFFFFFF);

    /// <summary>
    /// The CS2 client version to report. CS2 updates often and the GC ignores older versions, so it is looked up:
    /// SteamDB's tracked steam.inf first, then the CS2 installed on this PC, then the last version known at release.
    /// </summary>
    private static async Task<uint> GetClientVersionAsync(CancellationToken cancellationToken)
    {
        await VersionLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedVersion is { } cached && DateTimeOffset.UtcNow - _cachedAt < VersionCacheLifetime)
            {
                return cached;
            }

            uint? online = null;
            try
            {
                online = ReadClientVersion(await VersionHttp.GetStringAsync(TrackedSteamInfUrl, cancellationToken));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                AppLog.Warn($"Could not look up the current CS2 version online: {ex.Message}");
            }

            var version = online ?? Math.Max(ReadInstalledClientVersion() ?? 0, FallbackClientVersion);
            AppLog.Info($"Using CS2 client version {version} ({(online is null ? "installed or built in" : "online")}).");
            _cachedVersion = version;
            _cachedAt = DateTimeOffset.UtcNow;
            return version;
        }
        finally
        {
            VersionLock.Release();
        }
    }

    private static uint? ReadClientVersion(string steamInf)
    {
        var match = Regex.Match(steamInf, @"^\s*ClientVersion\s*=\s*(\d+)", RegexOptions.Multiline);
        return match.Success && uint.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    // Looks in every Steam library for an installed CS2.
    private static uint? ReadInstalledClientVersion()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steamPath)
            {
                return null;
            }

            var libraries = new List<string> { steamPath };
            var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFolders))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(libraryFolders), @"""path""\s+""([^""]+)"""))
                {
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }

            uint? best = null;
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var steamInf = Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "steam.inf");
                if (File.Exists(steamInf) && ReadClientVersion(File.ReadAllText(steamInf)) is { } version)
                {
                    best = Math.Max(best ?? 0, version);
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read the installed CS2 version: {ex.Message}");
            return null;
        }
    }
}
