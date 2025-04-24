namespace Sa.Configuration.SecretStore;

public interface ISecretService
{
    string? PopulateSecrets(string? inputString);
    string? this[string? inputString] => PopulateSecrets(inputString);
}
