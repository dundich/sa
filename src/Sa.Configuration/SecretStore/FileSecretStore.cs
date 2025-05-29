namespace Sa.Configuration.SecretStore;

public class FileSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets;

    public FileSecretStore(string path)
    {
        var filepath = GetFilePath(path);
        _secrets = filepath is null ? [] : LoadSecrets(filepath);
    }

    private static Dictionary<string, string?> LoadSecrets(string filepath)
    {
        return File
            .ReadAllLines(filepath)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#')) // skip empty lines or starting with a comment #
            .Select(x => x.Split('=', 2).Select(i => i.Trim()).ToArray())
            .GroupBy(x => x[0], x => x.Length == 2 ? x[1] : string.Empty)
            .ToDictionary(x => x.Key, x => x.LastOrDefault()); // take the last value for the key
    }

    public static string? GetFilePath(string path)
    {
        if (File.Exists(path)) return path;
        var filepath = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(filepath) ? filepath : null;
    }

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}
