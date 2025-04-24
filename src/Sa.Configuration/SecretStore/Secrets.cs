namespace Sa.Configuration.SecretStore;

/// <summary>
/// </summary>
/// <seealso href="https://github.com/zarusz/SlimMessageBus/tree/master/src/Tools/SecretStore"/>
public static class Secrets
{
    const string SA_HOST_KEY = "sa_host_key";

    static readonly string s_environmentName =
        (
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("environment")
            ?? "Production"
        )
        .ToLower();

    public static string DefaultSecretsPath => $"secrets.{s_environmentName}.txt";


    private static Lazy<ChainedSecrets> s_service = new(() => CreateSecrets(DefaultSecretsPath));

    public static void Reload(params string[] path) =>
        s_service = new Lazy<ChainedSecrets>(() => CreateSecrets(path));

    private static ChainedSecrets CreateSecrets(params string[] paths)
    {
        ChainedSecrets secrets = new([
            .. paths.Select(path => new FileSecretStore(path))
            , new EnvironmentVariableSecretStore()
        ]);

        string? hostKey = secrets.GetSecret(SA_HOST_KEY);

        if (!string.IsNullOrWhiteSpace(hostKey))
        {
            string? filename = Path.ChangeExtension(DefaultSecretsPath, hostKey + Path.GetExtension(DefaultSecretsPath));
            if (!string.IsNullOrWhiteSpace(filename))
            {
                secrets.AddStore(new FileSecretStore(filename));
            }
        }

        return secrets;
    }

    public static ISecretService Service => s_service.Value.Service;
}
