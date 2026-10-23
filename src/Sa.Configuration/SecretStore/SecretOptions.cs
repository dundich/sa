namespace Sa.Configuration.SecretStore;

/// <summary>
/// Immutable options for <see cref="Secrets.CreateDefault(SecretOptions?)"/>.
/// </summary>
/// <remarks>
/// <see cref="FileName"/> is never null; null/whitespace values are normalized
/// to <see cref="Secrets.DefaultFileName"/> by <see cref="Secrets.CreateDefault(SecretOptions?)"/>.
/// <see cref="Args"/> and <see cref="EnvironmentName"/> are null by default —
/// <see cref="Secrets.CreateDefault(SecretOptions?)"/> resolves them from
/// <see cref="Environment.GetCommandLineArgs()"/> and <see cref="Secrets.GetEnvironmentName()"/>.
/// </remarks>
public sealed record SecretOptions
{
    public string FileName { get; init; }

    public string[]? Args { get; init; }

    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Encrypted layer options. <see langword="null"/> uses the defaults
    /// (<see cref="SecretEncryptionOptions.Enabled"/> is <see langword="true"/>).
    /// </summary>
    public SecretEncryptionOptions? Encryption { get; init; }

    /// <summary>Load <see cref="DotEnvFileName"/> and its environment-specific variant. Default is <see langword="true"/>.</summary>
    public bool LoadDotEnv { get; init; } = true;

    /// <summary>Base <c>.env</c> file name. The environment-specific file is <c>{DotEnvFileName}.{env}</c>.</summary>
    public string DotEnvFileName { get; init; } = Stores.DotEnvSecretStore.DefaultFileName;

    /// <summary>
    /// Explicit key aliases applied after the secrets pass, over the merged configuration of all
    /// providers — see <see cref="KeyAliasOptions"/>.
    /// </summary>
    public KeyAliasOptions KeyAliases { get; init; } = new();

    public SecretOptions()
    {
        FileName = Secrets.DefaultFileName;
    }

    public SecretOptions(string? fileName, string[]? args = null, string? environmentName = null)
    {
        FileName = fileName ?? Secrets.DefaultFileName;
        Args = args;
        EnvironmentName = environmentName;
    }
}
