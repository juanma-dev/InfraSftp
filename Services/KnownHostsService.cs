using System.Security.Cryptography;
using System.Text.Json;

namespace InfraSftp.Services;

/// <summary>
/// Persists trusted SSH host keys per host:port. Acts as the local trust anchor
/// used to detect MITM attempts (mismatching fingerprint on a known host) and
/// to drive the TOFU prompt on first connection.
///
/// Storage shape (%APPDATA%/InfraSftp/known_hosts.json):
/// {
///   "host:port": { "Algorithm": "ssh-rsa", "FingerprintSha256": "base64-no-padding" }
/// }
/// Fingerprint is computed from the raw host-key bytes (SHA-256 → base64, padding
/// trimmed) so the value matches what `ssh-keygen -lf` and OpenSSH show.
/// </summary>
public class KnownHostsService
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InfraSftp");
    private static readonly string KnownHostsPath = Path.Combine(AppDir, "known_hosts.json");

    private readonly object _gate = new();
    private Dictionary<string, KnownHostEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public KnownHostsService()
    {
        Directory.CreateDirectory(AppDir);
        Load();
    }

    private void Load()
    {
        if (!File.Exists(KnownHostsPath)) return;
        try
        {
            var json = File.ReadAllText(KnownHostsPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, KnownHostEntry>>(json);
            if (parsed != null) _entries = new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt store — start fresh, will be rewritten on first save */ }
    }

    private void SaveLocked()
    {
        var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(KnownHostsPath, json);
    }

    public static string Key(string host, int port) => $"{host}:{port}";

    public KnownHostEntry? Lookup(string host, int port)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(host, port), out var e) ? e : null;
    }

    public void Trust(string host, int port, string algorithm, string fingerprintSha256)
    {
        lock (_gate)
        {
            _entries[Key(host, port)] = new KnownHostEntry
            {
                Algorithm = algorithm,
                FingerprintSha256 = fingerprintSha256,
                AddedUtc = DateTime.UtcNow
            };
            SaveLocked();
        }
    }

    public void Forget(string host, int port)
    {
        lock (_gate)
        {
            if (_entries.Remove(Key(host, port))) SaveLocked();
        }
    }

    /// <summary>
    /// OpenSSH-compatible SHA-256 host-key fingerprint: base64 of SHA-256(hostKey),
    /// padding trimmed. Matches `ssh-keygen -lf` output (without the "SHA256:" prefix).
    /// </summary>
    public static string ComputeSha256Fingerprint(byte[] hostKey)
        => Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
}

public class KnownHostEntry
{
    public string Algorithm { get; set; } = "";
    public string FingerprintSha256 { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}
