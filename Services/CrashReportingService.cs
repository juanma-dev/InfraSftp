using Sentry;

namespace InfraSftp.Services;

/// <summary>
/// Hooks process-wide unhandled exception sources and routes them to the
/// always-on local <see cref="LoggingService"/>. When the user opts in to
/// telemetry, also forwards to Sentry; opt-in defaults to <c>false</c>.
/// </summary>
/// <remarks>
/// <para>
/// Toggling telemetry at runtime (via <see cref="SetTelemetryEnabled"/>)
/// initialises or shuts down the Sentry SDK without restarting the app, so
/// the privacy choice in Settings takes effect immediately on the next crash.
/// </para>
/// <para>
/// The local log path is always wired up regardless of the toggle. That way
/// users who never opt in still leave a trail we can ask them to attach to
/// a bug report manually.
/// </para>
/// </remarks>
public sealed class CrashReportingService
{
    private const string Dsn =
        "https://c4a46dcc668f6851b9acbbc364559c8a@o4511287066361856.ingest.us.sentry.io/4511287144611840";

    private readonly LoggingService _log;
    private IDisposable? _sentry;
    private bool _hooked;
    private string _release = "InfraSftp@unknown";

    public CrashReportingService(LoggingService log)
    {
        _log = log;
    }

    /// <summary>
    /// Wire up the global handlers and (if <paramref name="telemetryEnabled"/>)
    /// boot Sentry. Safe to call exactly once during app startup.
    /// </summary>
    public void Initialize(bool telemetryEnabled, string appVersion)
    {
        _release = $"InfraSftp@{appVersion}";

        if (!_hooked)
        {
            // AppDomain handler catches non-UI background-thread crashes
            // (e.g. SSH callbacks running inside Task.Run). It fires after
            // the OS has decided the process is going down — last chance to
            // log before the app dies.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Capture(ex, "AppDomain.UnhandledException");
            };

            // Faulted Tasks whose result is never observed normally swallow
            // their exception. The unhandled hook surfaces them so we don't
            // miss silent background failures.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Capture(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };

            _hooked = true;
        }

        if (telemetryEnabled) StartSentry();
    }

    /// <summary>Re-evaluates the telemetry choice; call from the settings toggle.</summary>
    public void SetTelemetryEnabled(bool enabled)
    {
        if (enabled && _sentry == null) StartSentry();
        else if (!enabled && _sentry != null) StopSentry();
    }

    /// <summary>Manual capture for caught-but-noteworthy exceptions.</summary>
    public void Capture(Exception ex, string? context = null)
    {
        _log.Exception(ex, context);
        if (_sentry != null)
        {
            try { SentrySdk.CaptureException(ex); }
            catch (Exception innerEx) { _log.Exception(innerEx, "Sentry capture failed"); }
        }
    }

    private void StartSentry()
    {
        try
        {
            _sentry = SentrySdk.Init(o =>
            {
                o.Dsn = Dsn;
                o.Release = _release;
                // Don't ship usernames, machine names, or IPs. The user
                // opted in to crash data, not profile data.
                o.SendDefaultPii = false;
                o.AutoSessionTracking = true;
                // Drop noisy framework breadcrumbs; keep our own LogToTrace().
                o.AttachStacktrace = true;
            });
            _log.Info("Sentry telemetry enabled");
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "Sentry init failed");
        }
    }

    private void StopSentry()
    {
        try
        {
            _sentry?.Dispose();
            _sentry = null;
            _log.Info("Sentry telemetry disabled");
        }
        catch (Exception ex)
        {
            _log.Exception(ex, "Sentry shutdown failed");
        }
    }
}
