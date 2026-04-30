namespace InfraSftp.Services;

/// <summary>
/// Single source of truth for InfraSftp's per-user data directory.
///
/// Why this exists: <see cref="Environment.SpecialFolder.ApplicationData"/>
/// returns an empty string on Linux when <c>~/.config</c> does not yet
/// exist (observed on a fresh Fedora user account). Every consumer that
/// did <c>Path.Combine(GetFolderPath(ApplicationData), "InfraSftp")</c>
/// would then create a relative <c>"InfraSftp"</c> directory under the
/// process CWD instead of the user's home — silent data loss on first
/// run. Centralising the resolution means we get one chance to fall back
/// safely to <c>$HOME/.config</c>.
///
/// Layout:
///   Windows: <c>%APPDATA%\InfraSftp</c>
///   Linux  : <c>$XDG_CONFIG_HOME/InfraSftp</c> or <c>$HOME/.config/InfraSftp</c>
/// </summary>
internal static class AppPaths
{
    private const string AppName = "InfraSftp";

    /// <summary>
    /// Per-user data directory for InfraSftp. The directory is guaranteed
    /// to exist after the first call; if creation fails the original
    /// <see cref="IOException"/> bubbles up rather than returning a path
    /// that doesn't exist.
    /// </summary>
    public static string Root { get; } = Resolve();

    private static string Resolve()
    {
        var configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Linux fallback: SpecialFolder.ApplicationData is documented to
        // return $XDG_CONFIG_HOME or $HOME/.config, but on a fresh user
        // account where ~/.config doesn't exist yet, .NET 8 returns "".
        // Build the path explicitly so first-run installs work.
        if (string.IsNullOrEmpty(configRoot))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
            {
                configRoot = xdg;
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                    home = Environment.GetEnvironmentVariable("HOME") ?? ".";
                configRoot = Path.Combine(home, ".config");
            }
        }

        var appDir = Path.Combine(configRoot, AppName);
        Directory.CreateDirectory(appDir);
        return appDir;
    }
}
