using System.Text;

namespace NfaLoader.Services;

/// <summary>
/// Lightweight file log for diagnosing problems that only reproduce on some machines, such as "Steam won't start for some users".
/// Written to %AppData%\nfa.pub Loader\logs\nfa-loader.log; past 1 MB it rolls over into a single .1 backup.
/// All best-effort: no exception from the logger itself may affect the main flow (caught and ignored).
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogDirectory;

    /// <summary>Full path of the diagnostic log file, so the UI can tell users where to find it to send back.</summary>
    public static string LogFilePath { get; }

    static AppLog()
    {
        string directory;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            directory = Path.Combine(appData, "nfa.pub Loader", "logs");
        }
        catch
        {
            directory = Path.Combine(Path.GetTempPath(), "nfa.pub Loader", "logs");
        }

        LogDirectory = directory;
        LogFilePath = Path.Combine(directory, "nfa-loader.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{Describe(exception)}");

    private static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.Append("    ")
                .Append(current.GetType().FullName)
                .Append(": ")
                .AppendLine(current.Message);
        }

        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            builder.AppendLine(exception.StackTrace);
        }

        return builder.ToString().TrimEnd();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RollIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // A logging failure must never affect the main flow.
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var backup = LogFilePath + ".1";
            File.Delete(backup); // Does not throw if the file is missing.
            File.Move(LogFilePath, backup);
        }
        catch
        {
            // If rolling fails, keep appending to the current file; the main flow is unaffected.
        }
    }
}
