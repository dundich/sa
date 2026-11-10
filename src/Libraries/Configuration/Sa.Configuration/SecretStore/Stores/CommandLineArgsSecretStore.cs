using Sa.Configuration.CommandLine;

namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Reads secrets from command-line arguments.
/// </summary>
/// <param name="args">The arguments to parse; <see langword="null"/> uses the process arguments.</param>
public sealed class CommandLineArgsSecretStore(string[]? args = null) : ISecretStore
{

    private readonly Arguments _args = Arguments.CreateDefault(args);

    /// <inheritdoc />
    public string? GetSecret(string key) => _args[key];
}
