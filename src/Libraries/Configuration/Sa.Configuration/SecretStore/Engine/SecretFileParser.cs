namespace Sa.Configuration.SecretStore.Engine;

/// <summary>
/// Parses the <c>key=value</c> format shared by plaintext and decrypted secret files.
/// </summary>
internal static class SecretFileParser
{
    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        return lines
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#')) // skip empty lines or lines starting with a comment #
            .Where(x => x.Contains('='))
            .Select(x => x.Split('=', 2).Select(i => i.Trim()).ToArray())
            .GroupBy(x => x[0], x => x.Length == 2 ? x[1] : string.Empty)
            .ToDictionary(x => x.Key, x => x.LastOrDefault()); // take the last value for the key
    }
}
