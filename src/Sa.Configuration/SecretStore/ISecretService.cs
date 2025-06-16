namespace Sa.Configuration.SecretStore;

public interface ISecretService
{
    string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false);
}
