namespace InfraSftp.Services.Vault;

/// <summary>
/// Per-platform password store. Implementations bind secrets to the OS user
/// session: DPAPI on Windows, libsecret (Secret Service API) on Linux.
///
/// Keys are opaque strings — by convention <c>ProfileService</c> uses
/// <c>"user@host"</c>. Implementations may apply their own normalisation but
/// must round-trip whatever key the caller passes to Save through Get.
/// </summary>
internal interface IPasswordVault
{
    /// <summary>
    /// Persists <paramref name="secret"/> under <paramref name="key"/>,
    /// overwriting any prior value. Throws <see cref="VaultUnavailableException"/>
    /// if the underlying store cannot be reached (e.g. no session keyring).
    /// </summary>
    void Save(string key, string secret);

    /// <summary>
    /// Returns the secret for <paramref name="key"/>, or <c>null</c> if no
    /// entry exists. Returns <c>null</c> (not throws) when the store is
    /// reachable but empty for this key.
    /// </summary>
    string? Get(string key);

    /// <summary>
    /// Removes the entry for <paramref name="key"/>. No-op when absent.
    /// </summary>
    void Delete(string key);
}

/// <summary>
/// Thrown when the platform vault backend is installed but cannot be
/// reached — typically a missing Secret Service daemon on Linux. Surfaced
/// to the UI so the user gets a concrete remediation message instead of
/// a silent save failure.
/// </summary>
internal sealed class VaultUnavailableException : Exception
{
    public VaultUnavailableException(string message) : base(message) { }
    public VaultUnavailableException(string message, Exception inner) : base(message, inner) { }
}
