using System.IO;
using System.Security;

namespace iPodManager;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string DefaultPath = Path.Combine(
        AppContext.BaseDirectory, "iPodManager.log");
    internal static string PathToLog { get; private set; } = DefaultPath;

    // Keep the current session and exactly one predecessor.
    internal static void Start(string? path = null)
    {
        lock (Gate)
        {
            PathToLog = path ?? DefaultPath;
            try
            {
                string directory = Path.GetDirectoryName(PathToLog)!;
                string previousPath = Path.Combine(
                    directory,
                    $"{Path.GetFileNameWithoutExtension(PathToLog)}.previous{Path.GetExtension(PathToLog)}");
                Directory.CreateDirectory(directory);
                if (File.Exists(PathToLog))
                    File.Move(PathToLog, previousPath, overwrite: true);
                File.WriteAllText(PathToLog, string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Diagnostics cannot become an application startup dependency.
            }
        }
    }

    internal static void Info(string message) => Write("INFO", message, null);
    internal static void Warn(string message) => Write("WARN", message, null);
    internal static void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private static void Write(string severity, string message, Exception? exception)
    {
        lock (Gate)
        {
            try
            {
                string entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{severity}] {message}{Environment.NewLine}";
                if (exception != null) entry += exception + Environment.NewLine;
                File.AppendAllText(PathToLog, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // No fallback logger or recursive report on a failed diagnostic write.
            }
        }
    }
}
