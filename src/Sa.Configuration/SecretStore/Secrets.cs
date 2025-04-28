using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore;

/// <summary>
/// </summary>
/// <seealso href="https://github.com/zarusz/SlimMessageBus/tree/master/src/Tools/SecretStore"/>
public class Secrets(params IReadOnlyCollection<ISecretStore> stores) : ISecretService
{
    private readonly ChainedSecrets _chainedSecrets = new(stores);

    public string? PopulateSecrets(string? inputString) => _chainedSecrets.PopulateSecrets(inputString);


    private static readonly Lazy<Secrets> s_secrets = new(CreateDefaultSecrets);
    private static Secrets CreateDefaultSecrets()
    {
        string prefixName = Path.GetFileNameWithoutExtension(FileSecrets);
        string extName = Path.GetExtension(FileSecrets);
        string envName = SaEnvironment.Default.EnvironmentName;

        var store = new Secrets(
            new FileSecretStore(FileSecrets)
            , new FileSecretStore($"{prefixName}.{envName}{extName}")
            , new EnvironmentVariableSecretStore()
        );

        string? hostKey = store._chainedSecrets.GetSecret(SaEnvironment.Default.SA_HOST_KEY);

        if (!string.IsNullOrWhiteSpace(hostKey))
        {
            store._chainedSecrets.AddStore(new FileSecretStore($"{prefixName}.{hostKey}{extName}"));
            store._chainedSecrets.AddStore(new FileSecretStore($"{prefixName}.{hostKey}.{envName}{extName}"));
        }

        return store;
    }


    public static ISecretService Service => s_secrets.Value;
    public static string FileSecrets => SaEnvironment.Default.DefaultFileSecrets;
}
