using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Reads secrets from a plain-text secrets file.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets;

    /// <summary>
    /// Creates a store over the given secrets file.
    /// </summary>
    /// <param name="path">Relative or absolute path to the secrets file.</param>
    public FileSecretStore(string path)
    {
        var filepath = GetFilePath(path);
        _secrets = filepath is null
            ? []
            : SecretFileParser.Parse(File.ReadAllLines(filepath));
    }

    /// <summary>
    /// Resolves a secrets file path against the well-known search locations.
    /// </summary>
    /// <param name="path">Relative or absolute path to resolve.</param>
    /// <returns>The resolved path, or <see langword="null"/> when the file was not found.</returns>
    public static string? GetFilePath(string path) => SecretFileResolver.GetFilePath(path);

    /// <inheritdoc />
    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}
