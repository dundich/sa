using Sa.Configuration.CommandLine;

namespace Sa.Configuration.SecretStore.Stories;

public sealed class CommandLineArgsSecretStore(string[]? args = null) : ISecretStore
{

    private readonly Arguments _args = Arguments.CreateDefault(args);

    public string? GetSecret(string key) => _args[key];
}
