using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace InfraSftp.Models;

public enum AuthMethod
{
    Password,
    PrivateKey
}

public partial class Profile : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";

    // How this profile authenticates. Default is Password to preserve behaviour
    // for profiles created before this field existed (System.Text.Json fills
    // missing enum values with the default — Password — on load).
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    // Path to the OpenSSH-format private key (e.g. id_ed25519, id_rsa). Stored
    // by absolute path so users can keep keys in their preferred location and
    // share profiles without leaking key material.
    public string PrivateKeyPath { get; set; } = "";

    // Password (for AuthMethod.Password) and passphrase (for PrivateKey) are
    // both stored encrypted in the separate vault file, never in plain text
    // here. They share the same vault key shape (user@host).

    // Live connection state — true while any tab references this profile's
    // SftpService. Not persisted; always false on load.
    [property: JsonIgnore]
    [ObservableProperty] private bool _isConnected;

    public string Initials => string.IsNullOrEmpty(Name)
        ? "?"
        : string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(w => char.ToUpper(w[0])));
}
