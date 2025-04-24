namespace Sa.Configuration.SecretStore;

internal interface ISecretStore
{
    string? GetSecret(string key);
}
