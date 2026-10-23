using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore.Stores;

public sealed class FileSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets;

    public FileSecretStore(string path)
    {
        var filepath = GetFilePath(path);
        _secrets = filepath is null
            ? []
            : SecretFileParser.Parse(File.ReadAllLines(filepath));
    }

    public static string? GetFilePath(string path) => SecretFileResolver.GetFilePath(path);

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}
