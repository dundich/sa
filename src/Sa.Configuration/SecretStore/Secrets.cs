using Microsoft.Extensions.Hosting;
using Sa.Configuration.SecretStore.Engine;
using Sa.Configuration.SecretStore.Stories;

namespace Sa.Configuration.SecretStore;


/// <summary>
/// 
/// </summary>
public sealed class Secrets(params IReadOnlyCollection<ISecretStore> stores) : ISecretService
{
    public const string DefaultFileName = "secrets.txt";


    private readonly ChainedSecrets _chain = new(stores);

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
        => _chain.PopulateSecrets(inputString, returnNullIfSecretNotFound);


    public static Secrets CreateDefault(SecretOptions? options = null)
    {
        options ??= new SecretOptions();

        var filename = options.FileName ?? DefaultFileName;
        var environmentName = options.EnvironmentName ?? GetEnvironmentName();

        string prefixName = Path.GetFileNameWithoutExtension(filename);
        string extName = Path.GetExtension(filename);

        return new(
            new FileSecretStore(filename),
            new FileSecretStore($"{prefixName}.{environmentName}{extName}"),
            new EnvironmentVariableSecretStore(),
            new CommandLineArgsSecretStore(options.Args)
        );
    }

    public Secrets AddStore(ISecretStore store)
    {
        _chain.AddStore(store);
        return this;
    }

    public string? GetSecret(string key) => _chain.GetSecret(key);

    public static string GetEnvironmentName() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("environment")
            ?? Environments.Production;
}
