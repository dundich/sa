namespace Sa.Configuration.SecretStore.Engine;

internal class ChainedSecretStore(IReadOnlyCollection<ISecretStore> list) : ISecretStore
{
    private readonly List<ISecretStore> _list = [.. list];

    public string? GetSecret(string key)
    {
        foreach (ISecretStore secretStore in _list)
        {
            string? secret = secretStore.GetSecret(key);
            if (secret != null)
            {
                return secret;
            }
        }
        return null;
    }

    public void Add(ISecretStore store) => _list.Add(store);
}
