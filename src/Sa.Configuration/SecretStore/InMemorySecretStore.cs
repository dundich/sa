namespace Sa.Configuration.SecretStore;

public sealed class InMemorySecretStore(Dictionary<string, string?>? secrets = null) : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets = new(secrets ?? []);

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }

    public InMemorySecretStore AddSecret(string key, string? value)
    {
        _secrets[key] = value;
        return this;
    }
}