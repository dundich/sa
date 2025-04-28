namespace Sa.Configuration.SecretStore;

public interface ISecretStore
{
    string? GetSecret(string key);
}
