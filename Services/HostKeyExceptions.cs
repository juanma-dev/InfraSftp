namespace InfraSftp.Services;

/// <summary>
/// Thrown by SftpService.ConnectAsync when the remote presents a host key that is
/// not in the known_hosts store. The caller should prompt the user (TOFU) and,
/// on confirmation, re-call ConnectAsync with acceptNewHostKey: true.
/// </summary>
public class UnknownHostKeyException : Exception
{
    public string Host { get; }
    public int Port { get; }
    public string Algorithm { get; }
    public string FingerprintSha256 { get; }

    public UnknownHostKeyException(string host, int port, string algorithm, string fingerprint)
        : base($"Unknown host key for {host}:{port}. Fingerprint SHA256:{fingerprint}")
    {
        Host = host;
        Port = port;
        Algorithm = algorithm;
        FingerprintSha256 = fingerprint;
    }
}

/// <summary>
/// Thrown when the remote presents a host key whose fingerprint does NOT match the
/// one stored in known_hosts. This is the high-severity case: the server changed
/// keys, was reinstalled, OR the connection is being intercepted. The caller must
/// require explicit user action to update the trust anchor.
/// </summary>
public class HostKeyMismatchException : Exception
{
    public string Host { get; }
    public int Port { get; }
    public string Algorithm { get; }
    public string ReceivedFingerprintSha256 { get; }
    public string StoredFingerprintSha256 { get; }

    public HostKeyMismatchException(string host, int port, string algorithm, string received, string stored)
        : base($"Host key mismatch for {host}:{port}. Stored: SHA256:{stored} — received: SHA256:{received}")
    {
        Host = host;
        Port = port;
        Algorithm = algorithm;
        ReceivedFingerprintSha256 = received;
        StoredFingerprintSha256 = stored;
    }
}
