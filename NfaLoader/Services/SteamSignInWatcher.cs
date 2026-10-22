using System.Text;
using System.Text.RegularExpressions;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal enum SteamSignInOutcome
{
    /// <summary>Steam signed in to the account.</summary>
    SignedIn,

    /// <summary>Steam tried the account's login and the Steam servers refused it.</summary>
    Refused,

    /// <summary>Steam signed in, but to a different account.</summary>
    OtherAccount,

    /// <summary>Steam wrote nothing about signing in within the wait.</summary>
    NoAttempt,

    /// <summary>Steam's connection log could not be found or read, so nothing can be said.</summary>
    Unknown,
}

/// <param name="Detail">Steam's own word for a refusal, such as "Invalid Password" or "Expired".</param>
/// <param name="OtherSteamId">The account Steam signed in to instead, as a SteamID64.</param>
internal sealed record SteamSignInVerdict(SteamSignInOutcome Outcome, string? Detail = null, string? OtherSteamId = null);

/// <summary>Where Steam's connection log ended when Steam was launched, so only what Steam writes afterwards counts.</summary>
public sealed record SteamSignInMark(string LogPath, long Offset, uint AccountId);

/// <summary>
/// Tells whether Steam really signed in after the loader started it. The loader only writes Steam's files and starts
/// Steam, so without this a refused or ignored login looked exactly like a working one. Steam records every logon
/// attempt in logs/connection_log.txt, which is read from the point where Steam was started.
/// </summary>
internal static class SteamSignInWatcher
{
    private const ulong SteamId64Base = 76561197960265728;

    // "Using JWT 1823..., persistence: 1, steamid: [U:1:362013613], ..." names the account Steam is logging on with.
    private static readonly Regex UsingToken = new(@"Using JWT .*?steamid: \[U:1:(\d+)\]", RegexOptions.Compiled);

    // "RecvMsgClientLogOnResponse() : [U:1:362013613] 'OK'" or "... : [I:0:0] 'Invalid Password'".
    private static readonly Regex LogOnResponse = new(@"RecvMsgClientLogOnResponse\(\) : \[[A-Za-z]:\d+:(\d+)\] '([^']+)'", RegexOptions.Compiled);

    public static SteamSignInMark Mark(SteamPaths paths, string steamId)
    {
        var logPath = Path.Combine(paths.InstallPath, "logs", "connection_log.txt");
        long offset = 0;
        try
        {
            if (File.Exists(logPath))
            {
                offset = new FileInfo(logPath).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var accountId = ulong.TryParse(steamId, out var id64) ? (uint)(id64 & 0xFFFFFFFF) : 0;
        return new SteamSignInMark(logPath, offset, accountId);
    }

    public static async Task<SteamSignInVerdict> WaitAsync(SteamSignInMark mark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var offset = mark.Offset;
        var pending = "";
        uint? loggingOnWith = null;
        var sawLog = File.Exists(mark.LogPath);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            string chunk;
            try
            {
                if (!File.Exists(mark.LogPath))
                {
                    continue;
                }

                sawLog = true;
                using var stream = new FileStream(mark.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                // Steam starts a fresh log now and then; anything shorter than the mark is a new file.
                if (stream.Length < offset)
                {
                    offset = 0;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                chunk = await reader.ReadToEndAsync(cancellationToken);
                offset = stream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Only whole lines: the last one may still be being written.
            var text = pending + chunk;
            var lastBreak = text.LastIndexOf('\n');
            pending = lastBreak < 0 ? text : text[(lastBreak + 1)..];
            if (lastBreak < 0)
            {
                continue;
            }

            foreach (var line in text[..lastBreak].Split('\n'))
            {
                if (UsingToken.Match(line) is { Success: true } usingToken)
                {
                    loggingOnWith = uint.Parse(usingToken.Groups[1].Value);
                    continue;
                }

                if (LogOnResponse.Match(line) is not { Success: true } response)
                {
                    continue;
                }

                var responder = uint.Parse(response.Groups[1].Value);
                var result = response.Groups[2].Value;
                var account = responder != 0 ? responder : loggingOnWith ?? 0;

                if (string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    return account == mark.AccountId
                        ? new SteamSignInVerdict(SteamSignInOutcome.SignedIn)
                        : new SteamSignInVerdict(SteamSignInOutcome.OtherAccount, OtherSteamId: (SteamId64Base + account).ToString());
                }

                // A refusal for the account being signed in settles it. One for another account (Steam trying the
                // previous account first) is not ours, so keep watching.
                if (account == mark.AccountId || account == 0)
                {
                    return new SteamSignInVerdict(SteamSignInOutcome.Refused, result);
                }
            }
        }

        return new SteamSignInVerdict(sawLog ? SteamSignInOutcome.NoAttempt : SteamSignInOutcome.Unknown);
    }
}
