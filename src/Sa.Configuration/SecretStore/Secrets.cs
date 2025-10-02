using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore;

/// <summary>
/// </summary>
/// <seealso href="https://github.com/zarusz/SlimMessageBus/tree/master/src/Tools/SecretStore"/>
public sealed class Secrets(params IReadOnlyCollection<ISecretStore> stores) : ISecretService
{
    private readonly ChainedSecrets _chainedSecrets = new(stores);

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false) => _chainedSecrets.PopulateSecrets(inputString, returnNullIfSecretNotFound);

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

        string? hostKey = store.GetSecret(SaEnvironment.Default.SA_HOST_KEY);

        if (!string.IsNullOrWhiteSpace(hostKey))
        {
            store
                .AddStore(new FileSecretStore($"{prefixName}.{hostKey}{extName}"))
                .AddStore(new FileSecretStore($"{prefixName}.{hostKey}.{envName}{extName}"))
                ;
        }

        return store;
    }
    
    public Secrets AddStore(ISecretStore store)
    {
        _chainedSecrets.AddStore(store);
        return this;
    }

    public string? GetSecret(string key) => _chainedSecrets.GetSecret(key);

    public static ISecretService Service => s_secrets.Value;
    
    public static string FileSecrets => SaEnvironment.Default.DefaultFileSecrets;
}
