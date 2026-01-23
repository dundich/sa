namespace Sa.Configuration.SecretStore.Engine;

internal sealed class ChainedSecretStore(IReadOnlyCollection<ISecretStore> stores) : ISecretStore
{
    private readonly Stack<ISecretStore> _stores = new(stores);

    public string? GetSecret(string key)
    {
        foreach (ISecretStore secretStore in _stores)
        {
            string? secret = secretStore.GetSecret(key);
            if (secret != null)
            {
                return secret;
            }
        }
        return null;
    }

    public void Add(ISecretStore store) => _stores.Push(store);
}
