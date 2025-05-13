namespace Sa.Configuration.SecretStore.Engine;

internal class ChainedSecrets : ISecretService, ISecretStore
{
    private readonly ChainedSecretStore _store;
    private readonly SecretService _service;

    public ChainedSecrets(IReadOnlyCollection<ISecretStore> stores)
    {
        _store = new ChainedSecretStore(stores);
        _service = new SecretService(_store);
    }

    public void AddStore(ISecretStore store) => _store.Add(store);

    public string? GetSecret(string key) => _store.GetSecret(key);

    public string? PopulateSecrets(string? inputString) => _service.PopulateSecrets(inputString);
}
