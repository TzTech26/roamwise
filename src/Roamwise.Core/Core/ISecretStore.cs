namespace Roamwise.Core;

/// <summary>
/// Keeps controller keys and passwords out of the settings file in plain text: DPAPI on Windows,
/// the desktop keyring (Secret Service) on Linux.
/// </summary>
public interface ISecretStore
{
    /// <summary>One sentence for the settings page, e.g. "Stored encrypted for your Windows account."</summary>
    string Description { get; }

    /// <summary>Stores the secret and returns what to keep in the settings file.</summary>
    Task<string> SaveAsync(string secret);

    /// <summary>The secret, or null when it can't be read on this account any more.</summary>
    Task<string?> ReadAsync(string stored);

    Task ForgetAsync(string stored);
}
