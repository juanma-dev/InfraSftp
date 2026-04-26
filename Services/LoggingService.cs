namespace InfraSftp.Services;

/// <summary>
/// Always-on local file logger. Writes plain-text lines to
/// <c>%APPDATA%/InfraSftp/logs/app-yyyyMMdd.log</c>, one file per day, and
/// trims files older than <see cref="RetentionDays"/> on first write of a
/// session.
/// </summary>
/// <remarks>
/// This is the privacy-friendly fallback for users who keep crash reporting
/// disabled (the default). It also runs in parallel with Sentry when reports
/// are opted in, so a crash always has a local trace even if the network is
/// dead. Writes are serialised with a lock so concurrent log calls from the
/// SFTP background threads don't tear lines.
/// </remarks>
public sealed class LoggingService
{
    private const int RetentionDays = 7;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InfraSftp", "logs");

    private static readonly object _gate = new();
    private static bool _retentionRan;

    public LoggingService()
    {
        Directory.CreateDirectory(LogDir);
        TrimOldLogsOnce();
    }

    public void Info(string message)      => Write("INFO",  message, null);
    public void Warn(string message)      => Write("WARN",  message, null);
    public void Error(string message)     => Write("ERROR", message, null);
    public void Exception(Exception ex, string? context = null)
        => Write("ERROR", context ?? ex.GetType().Name, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var path = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var line = $"{DateTime.Now:O} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex;

        // Best-effort: a locked log file (e.g. user opened it in a tail viewer
        // that grabs an exclusive handle) must never bring the app down.
        try
        {
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* swallow */ }
    }

    private static void TrimOldLogsOnce()
    {
        if (_retentionRan) return;
        _retentionRan = true;
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(LogDir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* swallow */ }
    }

    public static string LogDirectory => LogDir;
}
