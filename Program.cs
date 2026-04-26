using Avalonia;
using System;
using System.Reflection;
using InfraSftp.Services;
using Velopack;

namespace InfraSftp;

sealed class Program
{
    // Boot-time service singletons. Created here (not in App / VM) so the
    // crash + log handlers are armed before anything Avalonia-related runs:
    // a XAML parse error or theme-resource crash during startup would
    // otherwise vanish into the void.
    public static LoggingService Log { get; } = new();
    public static CrashReportingService Crash { get; } = new(Log);
    public static UpdateService Updates { get; } = new(Log, Crash);

    // Avalonia + Velopack note: Velopack hijacks Main during install / update
    // hooks (e.g. when the installer relaunches the new exe with --install) and
    // exits cleanly. We MUST call VelopackApp.Build().Run() before any Avalonia
    // initialisation; otherwise the hook either runs UI it shouldn't or misses
    // the lifecycle event entirely. This is required by every Velopack app.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Read the user's telemetry choice once, before Sentry has a chance to
        // capture anything. The settings file is small, lives under %APPDATA%
        // and the load is fail-safe (returns defaults on any error), so doing
        // it here avoids dragging the whole VM init in just for one bool.
        var settings = new SettingsService().Load();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        Log.Info($"InfraSftp {version} starting (telemetry={(settings.EnableTelemetry ? "on" : "off")})");
        Crash.Initialize(settings.EnableTelemetry, version);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Last-resort net for crashes that escape Avalonia's own dispatcher.
            // Without this, a fatal startup error would only land in Windows
            // Event Viewer; with it, we always have a local trace and (if
            // opted in) a Sentry event.
            Crash.Capture(ex, "Fatal startup");
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
