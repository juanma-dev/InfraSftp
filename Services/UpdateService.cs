using Velopack;
using Velopack.Sources;

namespace InfraSftp.Services;

/// <summary>
/// Thin wrapper over <see cref="UpdateManager"/> backed by GitHub Releases.
/// Exposes the bare lifecycle the UI needs — check on startup, download in
/// the background, apply on user confirmation.
/// </summary>
/// <remarks>
/// <para>
/// The repo is public, so no token is needed; GitHub allows 60 unauthenticated
/// API calls per hour per IP, which is plenty for the once-per-launch poll
/// pattern this service uses.
/// </para>
/// <para>
/// All public methods are no-ops when the app is running uninstalled (e.g.
/// from <c>dotnet run</c> during development) — <see cref="UpdateManager.IsInstalled"/>
/// returns false and we short-circuit, so devs aren't pestered by useless
/// "no updates available" log lines.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/juanma-dev/InfraSftp";

    private readonly LoggingService _log;
    private readonly CrashReportingService _crash;
    private readonly UpdateManager _manager;

    public UpdateService(LoggingService log, CrashReportingService crash)
    {
        _log = log;
        _crash = crash;
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>True when launched from a Velopack install (i.e. updates can apply).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Latest version polled by the most recent <see cref="CheckAsync"/>; null if up-to-date.</summary>
    public string? PendingVersion { get; private set; }

    /// <summary>True once <see cref="DownloadAsync"/> has staged a release ready to apply.</summary>
    public bool IsDownloaded { get; private set; }

    private UpdateInfo? _pending;

    /// <summary>
    /// Polls GitHub Releases for a newer version. Returns true if one was found.
    /// Network errors are swallowed and logged — a missed check should never
    /// surface to the user.
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        if (!IsInstalled) return false;
        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            if (_pending == null)
            {
                PendingVersion = null;
                return false;
            }
            PendingVersion = _pending.TargetFullRelease.Version.ToString();
            _log.Info($"Update available: {PendingVersion}");
            return true;
        }
        catch (Exception ex)
        {
            // Offline / GitHub rate-limited / DNS down — don't crash, just log.
            _log.Exception(ex, "Update check failed");
            return false;
        }
    }

    /// <summary>
    /// Downloads the staged update in the background. Must be preceded by a
    /// successful <see cref="CheckAsync"/>; otherwise no-op.
    /// </summary>
    public async Task<bool> DownloadAsync(IProgress<int>? progress = null)
    {
        if (_pending == null) return false;
        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p));
            IsDownloaded = true;
            _log.Info($"Update {PendingVersion} downloaded");
            return true;
        }
        catch (Exception ex)
        {
            _crash.Capture(ex, "Update download failed");
            return false;
        }
    }

    /// <summary>
    /// Exits the app and hands off to the updater. Does not return.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_pending == null || !IsDownloaded) return;
        try
        {
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _crash.Capture(ex, "Apply update failed");
        }
    }
}
