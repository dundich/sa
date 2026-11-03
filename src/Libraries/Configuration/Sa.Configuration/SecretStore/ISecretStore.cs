namespace Sa.Configuration.SecretStore;

/// <summary>
/// Provides access to secret values by key.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Gets a secret value by its key.
    /// </summary>
    /// <param name="key">The key of the secret to retrieve.</param>
    /// <returns>The secret value, or null if the key is not found.</returns>
    string? GetSecret(string key);
}
