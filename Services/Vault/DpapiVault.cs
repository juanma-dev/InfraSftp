#if WINDOWS
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InfraSftp.Services.Vault;

/// <summary>
/// Windows password vault. Stores all secrets as a single JSON dictionary
/// in <c>%APPDATA%/InfraSftp/vault.dat</c>, encrypted with DPAPI under
/// <see cref="DataProtectionScope.CurrentUser"/>.
///
/// The OS binds the encryption key to the logged-in user's credential, so
/// the file cannot be decrypted by other accounts on the same machine, nor
/// copied to a different machine. v1 (machine-derived AES-256-CBC) is still
/// readable for one cycle so existing vaults migrate transparently on first
/// load.
/// </summary>
internal sealed class DpapiVault : IPasswordVault
{
    private static readonly string VaultPath = Path.Combine(AppPaths.Root, "vault.dat");

    // Magic header that tags v2 (DPAPI) blobs. v1 files start with raw IV
    // bytes and never collide with this prefix.
    private static readonly byte[] V2Magic = Encoding.ASCII.GetBytes("ISv2");

    // Extra entropy bound into DPAPI — another app running as the same user
    // can't decrypt this vault without also knowing this constant. Treat as
    // a domain separator, not a secret.
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("InfraSftp.Vault.v2");

    public void Save(string key, string secret)
    {
        var vault = LoadVault();
        vault[key] = secret;
        SaveVault(vault);
    }

    public string? Get(string key)
    {
        var vault = LoadVault();
        return vault.TryGetValue(key, out var pw) ? pw : null;
    }

    public void Delete(string key)
    {
        var vault = LoadVault();
        if (vault.Remove(key)) SaveVault(vault);
    }

    private static Dictionary<string, string> LoadVault()
    {
        if (!File.Exists(VaultPath)) return new();
        try
        {
            var blob = File.ReadAllBytes(VaultPath);
            string plainText;
            var migrated = false;

            if (IsV2(blob))
            {
                plainText = DecryptV2(blob);
            }
            else
            {
                plainText = DecryptV1Legacy(blob);
                migrated = true;
            }

            var vault = JsonSerializer.Deserialize<Dictionary<string, string>>(plainText) ?? new();

            // Quietly upgrade legacy vaults so we eventually phase v1 out. If
            // the rewrite fails (e.g. file in use) keep going — next save
            // will produce a v2 blob anyway.
            if (migrated)
            {
                try { SaveVault(vault); } catch { }
            }
            return vault;
        }
        catch { return new(); }
    }

    private static void SaveVault(Dictionary<string, string> vault)
    {
        var json = JsonSerializer.Serialize(vault);
        var blob = EncryptV2(json);
        File.WriteAllBytes(VaultPath, blob);
    }

    private static bool IsV2(byte[] blob)
    {
        if (blob.Length < V2Magic.Length) return false;
        for (var i = 0; i < V2Magic.Length; i++)
            if (blob[i] != V2Magic[i]) return false;
        return true;
    }

    private static byte[] EncryptV2(string plainText)
    {
        var data = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);
        var result = new byte[V2Magic.Length + protectedBytes.Length];
        Buffer.BlockCopy(V2Magic, 0, result, 0, V2Magic.Length);
        Buffer.BlockCopy(protectedBytes, 0, result, V2Magic.Length, protectedBytes.Length);
        return result;
    }

    private static string DecryptV2(byte[] blob)
    {
        var protectedBytes = new byte[blob.Length - V2Magic.Length];
        Buffer.BlockCopy(blob, V2Magic.Length, protectedBytes, 0, protectedBytes.Length);
        var data = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    // v1 legacy reader (AES-256-CBC, machine/user-seeded key). No longer
    // written. Kept only to migrate vaults created before the v2 cut.
    private static byte[] DeriveLegacyKey()
    {
        var seed = $"InfraSftp|{Environment.MachineName}|{Environment.UserName}|v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static string DecryptV1Legacy(byte[] cipherText)
    {
        var key = DeriveLegacyKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var iv = new byte[16];
        Buffer.BlockCopy(cipherText, 0, iv, 0, 16);
        aes.IV = iv;

        using var dec = aes.CreateDecryptor();
        var data = dec.TransformFinalBlock(cipherText, 16, cipherText.Length - 16);
        return Encoding.UTF8.GetString(data);
    }
}
#endif
