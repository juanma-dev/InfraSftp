using System.Runtime.InteropServices;

namespace InfraSftp.Services.Vault;

/// <summary>
/// Resolves the per-platform <see cref="IPasswordVault"/> implementation.
/// Selection happens at compile time via the <c>WINDOWS</c> symbol that
/// the <c>net8.0-windows</c> TFM auto-defines, then at runtime for the
/// non-Windows TFMs (currently Linux only).
/// </summary>
internal static class PasswordVaultFactory
{
    public static IPasswordVault Create()
    {
#if WINDOWS
        return new DpapiVault();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LibsecretVault();

        throw new PlatformNotSupportedException(
            "InfraSftp's password vault has no implementation for this OS. " +
            "Supported platforms: Windows (DPAPI), Linux (libsecret).");
#endif
    }
}
