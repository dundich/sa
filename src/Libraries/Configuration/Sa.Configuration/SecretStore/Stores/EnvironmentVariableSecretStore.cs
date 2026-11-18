namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Reads secrets from environment variables.
/// </summary>
public sealed class EnvironmentVariableSecretStore : ISecretStore
{
    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);
        if (value == "(empty)")
        {
            return string.Empty;
        }
        return value;
    }
}
