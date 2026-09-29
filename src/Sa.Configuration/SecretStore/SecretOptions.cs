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
