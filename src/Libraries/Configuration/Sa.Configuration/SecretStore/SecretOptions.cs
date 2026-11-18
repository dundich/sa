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
    /// <summary>Plain secrets file name; defaults to <see cref="Secrets.DefaultFileName"/>.</summary>
    public string FileName { get; init; }

    /// <summary>Command-line arguments used to resolve secrets; <see langword="null"/> uses the process arguments.</summary>
    public string[]? Args { get; init; }

    /// <summary>Environment name used to pick environment-specific files; <see langword="null"/> is detected.</summary>
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

    /// <summary>
    /// Dynamic resolution and diagnostics for lookups that no store could satisfy — see
    /// <see cref="SecretResolutionOptions"/>.
    /// </summary>
    public SecretResolutionOptions Resolution { get; init; } = new();

    /// <summary>
    /// Creates options with default values.
    /// </summary>
    public SecretOptions()
    {
        FileName = Secrets.DefaultFileName;
    }

    /// <summary>
    /// Creates options with an explicit file name, arguments and environment.
    /// </summary>
    /// <param name="fileName">Plain secrets file name; <see langword="null"/> uses <see cref="Secrets.DefaultFileName"/>.</param>
    /// <param name="args">Command-line arguments used to resolve secrets.</param>
    /// <param name="environmentName">Environment name used to pick environment-specific files.</param>
    public SecretOptions(string? fileName, string[]? args = null, string? environmentName = null)
    {
        FileName = fileName ?? Secrets.DefaultFileName;
        Args = args;
        EnvironmentName = environmentName;
    }
}
