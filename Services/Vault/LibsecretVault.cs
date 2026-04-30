using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace InfraSftp.Services.Vault;

/// <summary>
/// Linux password vault backed by libsecret / Secret Service. Each entry is
/// stored as an independent item in the user's session keyring (typically
/// gnome-keyring or kwallet5), keyed on two attributes:
///   <c>application = "com.webjuanma.InfraSftp"</c> (domain separator) and
///   <c>key</c> = the caller-supplied identifier.
///
/// We shell out to the <c>secret-tool</c> CLI from the <c>libsecret</c>
/// package rather than P/Invoke libsecret or speak D-Bus directly. Reasons:
///   1. No new managed dependencies.
///   2. <c>secret-tool</c> handles unlock prompts and keyring discovery
///      transparently — re-implementing that via raw D-Bus would be much
///      more code for no security gain.
///   3. The binary is the canonical Fedora / Debian package and is the
///      natural dependency to declare in the future RPM <c>.spec</c>.
///
/// Passwords are passed via stdin so they never appear in the process
/// argument list (i.e. <c>/proc/&lt;pid&gt;/cmdline</c>).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LibsecretVault : IPasswordVault
{
    private const string Tool = "secret-tool";
    private const string AttrApp = "application";
    private const string AttrAppValue = "com.webjuanma.InfraSftp";
    private const string AttrKey = "key";

    public void Save(string key, string secret)
    {
        // `store --label=... application VAL key KEY` — reads the secret
        // from stdin until EOF.
        var (exit, _, stderr) = Run(
            new[] { "store", "--label", $"InfraSftp: {key}",
                    AttrApp, AttrAppValue, AttrKey, key },
            stdin: secret);

        if (exit != 0)
            throw new VaultUnavailableException(
                $"secret-tool store failed (exit {exit}): {stderr.Trim()}");
    }

    public string? Get(string key)
    {
        // `lookup` prints the secret raw on success (no trailing newline).
        // Exit 0 = found, exit 1 = not found, anything else = error.
        var (exit, stdout, stderr) = Run(
            new[] { "lookup", AttrApp, AttrAppValue, AttrKey, key },
            stdin: null);

        return exit switch
        {
            0 => stdout,
            1 => null,
            _ => throw new VaultUnavailableException(
                    $"secret-tool lookup failed (exit {exit}): {stderr.Trim()}"),
        };
    }

    public void Delete(string key)
    {
        // `clear` is idempotent: deleting a missing entry exits 0.
        var (exit, _, stderr) = Run(
            new[] { "clear", AttrApp, AttrAppValue, AttrKey, key },
            stdin: null);

        if (exit != 0)
            throw new VaultUnavailableException(
                $"secret-tool clear failed (exit {exit}): {stderr.Trim()}");
    }

    private static (int exitCode, string stdout, string stderr) Run(
        string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try
        {
            p = Process.Start(psi)
                ?? throw new VaultUnavailableException(
                    $"Failed to start '{Tool}'. Install the libsecret package " +
                    "(e.g. `dnf install libsecret` on Fedora, " +
                    "`apt install libsecret-tools` on Debian/Ubuntu).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // ENOENT — binary not found in PATH.
            throw new VaultUnavailableException(
                $"'{Tool}' is not installed. Install the libsecret package " +
                "(e.g. `dnf install libsecret` on Fedora, " +
                "`apt install libsecret-tools` on Debian/Ubuntu).", ex);
        }

        using (p)
        {
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout, stderr);
        }
    }
}
