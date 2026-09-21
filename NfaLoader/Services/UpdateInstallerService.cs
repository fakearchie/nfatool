using System.Diagnostics;
using System.Security.Cryptography;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class UpdateInstallerService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private const int DownloadBufferSize = 80 * 1024;

    // How long with no progress between reads counts as "stalled". With ResponseHeadersRead, HttpClient.Timeout only covers the response headers,
    // not ReadAsync on the body. Through a proxy site the other end may send the headers and then hang silently, so we need our own idle timeout.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(60);

    public async Task<string> DownloadInstallerAsync(
        GitHubUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ArtifactUrl))
        {
            throw new InvalidOperationException("The update download URL is empty.");
        }

        var fileName = ResolveInstallerFileName(update);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This update is not an installer. Download it manually from the releases page.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "nfa.pub Loader", "updates", update.LatestVersion);
        Directory.CreateDirectory(tempRoot);

        var targetPath = Path.Combine(tempRoot, fileName);
        var downloadPath = targetPath + ".downloading";

        // Idle timeout: combine the user's cancellation and a "60s without progress, reset after every successful read" into one token. Timing each idle gap instead of the total duration
        // keeps large files on slow links from being killed by a total time limit.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(DownloadIdleTimeout);
        var idleToken = idleCts.Token;

        try
        {
            using var response = await HttpClient.GetAsync(
                update.ArtifactUrl,
                HttpCompletionOption.ResponseHeadersRead,
                idleToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(idleToken))
            await using (var target = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithProgressAsync(source, target, totalBytes, progress, idleCts, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(update.ArtifactSha256))
            {
                var actualHash = ComputeSha256(downloadPath);
                if (!string.Equals(actualHash, update.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The installer failed verification. Try again later.");
                }
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(downloadPath, targetPath);
            return targetPath;
        }
        catch (OperationCanceledException) when (idleToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Idle timeout (not a user cancel): turn it into an explicit timeout exception so the UI can show different text.
            TryDeletePartial(downloadPath);
            throw new TimeoutException("Download timed out: no data received for a long time. Check your network or switch the update source, then try again.");
        }
        catch
        {
            TryDeletePartial(downloadPath);
            throw;
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to clean up the temp file of an unfinished update download.", ex);
        }
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            Arguments = "/CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
        };

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public void ForceCloseOtherInstances(string processName, int currentProcessId)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best-effort: other instances may have already exited, or we lack permission.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("nfa-pub-loader-installer");
        return client;
    }

    private static string ResolveInstallerFileName(GitHubUpdateInfo update)
    {
        if (!string.IsNullOrWhiteSpace(update.ArtifactName))
        {
            return Path.GetFileName(update.ArtifactName);
        }

        if (!string.IsNullOrWhiteSpace(update.ArtifactUrl) &&
            Uri.TryCreate(update.ArtifactUrl, UriKind.Absolute, out var uri))
        {
            var fileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return "nfa-pub-loader-installer.exe";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream target,
        long? totalBytes,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationTokenSource idleCts,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
            if (read == 0)
            {
                break;
            }

            // Reset the idle timeout window on every chunk read, so it only cancels on "no progress for the whole window".
            idleCts.CancelAfter(DownloadIdleTimeout);

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new UpdateDownloadProgress(received, totalBytes));
        }
    }
}

internal sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes);
