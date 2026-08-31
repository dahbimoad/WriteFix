using System.IO;
using System.Security.Cryptography;
using System.Text;
using WriteFix.Services.Logging;
using WriteFix.Services.Platform;

namespace WriteFix.Services.Settings;

/// <summary>
/// The provider API key, encrypted at rest with DPAPI (current user). The
/// ciphertext is only decryptable by this Windows account on this machine.
/// </summary>
public sealed class SecretStore
{
    // Ties the ciphertext to WriteFix so a blob lifted from another app won't decrypt.
    // The literal is load-bearing: changing it makes every already-stored key
    // undecryptable, so it keeps its original OpenRouter-era name.
    private static readonly byte[] Entropy = "WriteFix.OpenRouter.v1"u8.ToArray();

    public bool HasKey => File.Exists(AppPaths.ApiKeyFile);

    public string? Read()
    {
        if (!HasKey) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(AppPaths.ApiKeyFile);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // Typically a copied profile or a different Windows account.
            AppLog.Error("Stored API key could not be decrypted; treating as absent.", ex);
            return null;
        }
    }

    public void Write(string apiKey)
    {
        AppPaths.EnsureCreated();
        var plain = Encoding.UTF8.GetBytes(apiKey);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.ApiKeyFile, protectedBytes);
        Array.Clear(plain);
        AppLog.Info("API key saved.");
    }

    public void Delete()
    {
        if (!HasKey) return;
        File.Delete(AppPaths.ApiKeyFile);
        AppLog.Info("API key deleted.");
    }
}
