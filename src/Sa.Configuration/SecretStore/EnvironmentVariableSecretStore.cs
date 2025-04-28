namespace Sa.Configuration.SecretStore;

public class EnvironmentVariableSecretStore : ISecretStore
{
    public string? GetSecret(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);
        if (value == "(empty)")
        {
            return string.Empty;
        }
        return value;
    }
}