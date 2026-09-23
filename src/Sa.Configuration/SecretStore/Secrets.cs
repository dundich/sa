using Microsoft.Extensions.Hosting;
using Sa.Configuration.SecretStore.Engine;
using Sa.Configuration.SecretStore.Stories;

namespace Sa.Configuration.SecretStore;


/// <summary>
/// The Secrets class simplifies the management of sensitive information in your application
/// </summary>
public sealed class Secrets(params ISecretStore[] stores) : ISecretService
{
    public const string DefaultFileName = "secrets.txt";


    private readonly ChainedSecrets _chain = new(stores);

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
        => _chain.PopulateSecrets(inputString, returnNullIfSecretNotFound);


    public static Secrets CreateDefault(SecretOptions? options = null)
    {
        options ??= new();

        var filename = string.IsNullOrWhiteSpace(options.FileName)
            ? DefaultFileName
            : options.FileName;
        var environmentName = string.IsNullOrWhiteSpace(options.EnvironmentName)
            ? GetEnvironmentName()
            : options.EnvironmentName;
        var args = options.Args ?? Environment.GetCommandLineArgs();

        string prefixName = Path.GetFileNameWithoutExtension(filename);
        string extName = Path.GetExtension(filename);

        return new(
            new FileSecretStore(filename),
            new FileSecretStore($"{prefixName}.{environmentName}{extName}"),
            new EnvironmentVariableSecretStore(),
            new CommandLineArgsSecretStore(args)
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
