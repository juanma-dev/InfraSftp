using System.Text.Json;
using InfraSftp.Models;
using InfraSftp.Services.Vault;

namespace InfraSftp.Services;

/// <summary>
/// Manages profile persistence (JSON) and password storage. Profiles live
/// in a plain <c>profiles.json</c>; passwords delegate to the per-platform
/// <see cref="IPasswordVault"/> (DPAPI on Windows, libsecret on Linux).
/// </summary>
public class ProfileService
{
    private static readonly string ProfilesPath = Path.Combine(AppPaths.Root, "profiles.json");

    private readonly IPasswordVault _vault;

    public ProfileService()
    {
        _vault = PasswordVaultFactory.Create();
    }

    // ── Profiles ───────────────────────────────────────────────
    public List<Profile> LoadProfiles()
    {
        if (!File.Exists(ProfilesPath)) return new List<Profile>();
        try
        {
            var json = File.ReadAllText(ProfilesPath);
            return JsonSerializer.Deserialize<List<Profile>>(json) ?? new List<Profile>();
        }
        catch { return new List<Profile>(); }
    }

    public void SaveProfiles(List<Profile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ProfilesPath, json);
    }

    // ── Import / Export ────────────────────────────────────────
    // Export produces the same indented JSON shape used for profiles.json.
    // No password data is ever serialised — the vault is a separate store
    // by design — so the caller cannot accidentally leak credentials.
    public string ExportProfiles(IList<Profile> profiles)
        => JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });

    public List<Profile> ParseImportedProfiles(string json)
        => JsonSerializer.Deserialize<List<Profile>>(json) ?? new List<Profile>();

    // ── Password Vault ─────────────────────────────────────────
    private static string VaultKey(string host, string user) => $"{user}@{host}";

    public void SavePassword(string host, string user, string password)
        => _vault.Save(VaultKey(host, user), password);

    public string? GetPassword(string host, string user)
        => _vault.Get(VaultKey(host, user));

    public void DeletePassword(string host, string user)
        => _vault.Delete(VaultKey(host, user));
}
