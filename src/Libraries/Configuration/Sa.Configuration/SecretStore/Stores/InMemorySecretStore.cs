namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Keeps secrets in memory.
/// </summary>
/// <param name="secrets">Initial secrets; <see langword="null"/> starts empty.</param>
public sealed class InMemorySecretStore(Dictionary<string, string?>? secrets = null) : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets = new(secrets ?? []);

    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }

    /// <summary>
    /// Adds or replaces a secret.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <param name="value">The secret value.</param>
    /// <returns>The same instance, to allow chaining.</returns>
    public InMemorySecretStore AddSecret(string key, string? value)
    {
        _secrets[key] = value;
        return this;
    }
}
