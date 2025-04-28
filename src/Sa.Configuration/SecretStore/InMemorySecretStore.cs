namespace Sa.Configuration.SecretStore;

public class InMemorySecretStore(Dictionary<string, string?> secrets) : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets = secrets;

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}