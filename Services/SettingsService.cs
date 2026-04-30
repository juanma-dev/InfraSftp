using System.Text.Json;
using Avalonia;
using Avalonia.Styling;

namespace InfraSftp.Services;

public enum AppTheme { Dark, Light, System }

// Snapshot of one open tab — written at window close and replayed on next launch.
// Remote tabs come back disconnected; the user's first interaction (or app idle
// reconnect) re-establishes the SFTP session, so credentials are never required
// at startup just to redraw the layout.
public class TabState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsRemote { get; set; }
    public string Path { get; set; } = "";
}

public class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool ShowHidden { get; set; } = false;

    // When false (default), recursive transfers skip a file at the destination
    // if it already exists with the same size AND a last-modified time within
    // 2s of the source — the heuristic rsync uses by default. When true, every
    // file is re-transferred unconditionally.
    //
    // The 2s tolerance absorbs timestamp-resolution mismatches between FAT
    // (2s), NTFS (~100ns) and ext4 (1ns); it's the same window rsync ships
    // with as `--modify-window=2`.
    public bool ForceTransfer { get; set; } = false;

    // Crash-report telemetry (Sentry). Opt-in by design: defaults to false so
    // the app never sends data unless the user explicitly enables it in the
    // Settings modal. Local logs under %APPDATA%/InfraSftp/logs are written
    // regardless and are the privacy-preserving fallback for users who keep
    // this off.
    public bool EnableTelemetry { get; set; } = false;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    // Persisted tab layout. Empty on first run — the VM seeds a Local tab.
    public List<TabState> LeftTabs { get; set; } = new();
    public List<TabState> RightTabs { get; set; } = new();
    public string? ActiveLeftTabId { get; set; }
    public string? ActiveRightTabId { get; set; }
}

/// <summary>
/// Persists <see cref="AppSettings"/> to %APPDATA%/InfraSftp/settings.json and
/// applies the chosen <see cref="AppTheme"/> to the running Avalonia app by
/// swapping <c>Application.Current.RequestedThemeVariant</c>.
/// </summary>
public class SettingsService
{
    private static readonly string Path_ = Path.Combine(AppPaths.Root, "settings.json");

    public SettingsService() { }

    public AppSettings Load()
    {
        if (!File.Exists(Path_)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path_, json);
    }

    // Applies theme to the live application. Safe to call from any thread —
    // Avalonia marshals the variant change internally.
    public static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current == null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark  => ThemeVariant.Dark,
            _              => ThemeVariant.Default, // System
        };
    }
}
