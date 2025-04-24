namespace Sa.Configuration.SecretStore;

internal class FileSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets;

    public FileSecretStore(string path)
    {
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }


        if (!File.Exists(path))
        {
            _secrets = [];
            return;
        }

        string[] lines = File.ReadAllLines(path);

        _secrets = lines
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#')) // skip empty lines or starting with a comment #
            .Select(x => x.Split('=', 2).Select(i => i.Trim()).ToArray())
            .GroupBy(x => x[0], x => x.Length == 2 ? x[1] : string.Empty)
            .ToDictionary(x => x.Key, x => x.LastOrDefault()); // take the last value for the key
    }

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}
