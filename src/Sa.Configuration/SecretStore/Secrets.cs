namespace Sa.Configuration.SecretStore;

/// <summary>
/// </summary>
/// <seealso href="https://github.com/zarusz/SlimMessageBus/tree/master/src/Tools/SecretStore"/>
public static class Secrets
{
    public static string FileSecrets => SaConfigurationEnvironment.Default.DefaultFileSecrets;


    private static Lazy<ChainedSecrets> s_service = new(() => CreateSecrets(FileSecrets));

    public static void Reload(params string[] path) =>
        s_service = new Lazy<ChainedSecrets>(() => CreateSecrets(path));

    private static ChainedSecrets CreateSecrets(params string[] paths)
    {
        ChainedSecrets secrets = new([
            .. paths.Select(path => new FileSecretStore(path))
            , new EnvironmentVariableSecretStore()
        ]);

        string? hostKey = secrets.GetSecret(SaConfigurationEnvironment.Default.SA_HOST_KEY);

        if (!string.IsNullOrWhiteSpace(hostKey))
        {
            string? filename = Path.ChangeExtension(FileSecrets, hostKey + Path.GetExtension(FileSecrets));
            if (!string.IsNullOrWhiteSpace(filename))
            {
                secrets.AddStore(new FileSecretStore(filename));
            }
        }

        return secrets;
    }

    public static ISecretService Service => s_service.Value.Service;
}
