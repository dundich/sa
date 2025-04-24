namespace Sa.Configuration.SecretStore;

internal class ChainedSecrets
{
    private readonly ChainedSecretStore _store;

    public ISecretService Service { get; }

    public ChainedSecrets(params ISecretStore[] stores)
    {
        _store = new ChainedSecretStore(stores);
        Service = new SecretService(_store);
    }

    public void AddStore(ISecretStore store) => _store.Add(store);

    public string? GetSecret(string key) => _store.GetSecret(key);
}
