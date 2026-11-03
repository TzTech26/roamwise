using System.Security.Cryptography;
using System.Text;

namespace Roamwise.Core;

/// <summary>Encrypts controller keys for the current Windows account (DPAPI); the ciphertext lives in settings.json.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    public string Description => "Stored encrypted for your Windows account. Roamwise only reads device names.";

    public Task<string> SaveAsync(string secret) =>
        Task.FromResult(Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser)));

    public Task<string?> ReadAsync(string stored)
    {
        try { return Task.FromResult<string?>(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return Task.FromResult<string?>(null); }
    }

    public Task ForgetAsync(string stored) => Task.CompletedTask;
}
