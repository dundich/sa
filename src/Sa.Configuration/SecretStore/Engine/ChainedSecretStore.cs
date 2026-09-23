namespace Sa.Configuration.SecretStore.Engine;

/// <summary>
/// A chain of secret stores with LIFO priority: the store passed/added last has the highest priority.
/// Thread-safe — <see cref="Add"/> swaps an immutable array snapshot via <see cref="Interlocked.Exchange"/>.
/// </summary>
internal sealed class ChainedSecretStore(ISecretStore[] stores): ISecretStore
{
    private volatile ISecretStore[] _stores = [.. stores.Reverse()];

    public string? GetSecret(string key)
    {
        // Snapshot is immutable — safe to enumerate while other threads call Add.
        foreach (ISecretStore store in _stores)
        {
            string? secret = store.GetSecret(key);
            if (secret != null)
            {
                return secret;
            }
        }
        return null;
    }

    public void Add(ISecretStore store)
    {
        ISecretStore[] current = _stores;

        var next = new ISecretStore[current.Length + 1];
        next[0] = store; // newly added store has the highest priority
        Array.Copy(current, 0, next, 1, current.Length);

        Interlocked.Exchange(ref _stores, next);
    }
}
