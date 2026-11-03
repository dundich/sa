using System.Text;
using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Loads secrets from a <c>.env</c> file.
/// </summary>
/// <remarks>
/// Supports <c>KEY=VALUE</c>, <c>export KEY=VALUE</c>, <c>#</c> comments, a UTF-8 BOM,
/// CRLF/LF line endings and surrounding single, double or backtick quotes. Double-quoted
/// values understand the escapes <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\"</c> and <c>\\</c>.
/// Duplicate keys keep the last value. Malformed lines are ignored, matching
/// <see cref="FileSecretStore"/>.
/// </remarks>
public sealed class DotEnvSecretStore : ISecretStore
{
    public const string DefaultFileName = ".env";

    private readonly Dictionary<string, string?> _secrets;

    /// <param name="path">
    /// Relative or absolute path to the file. Defaults to <see cref="DefaultFileName"/>.
    /// </param>
    public DotEnvSecretStore(string? path = null)
    {
        string? filepath = SecretFileResolver.GetFilePath(path ?? DefaultFileName);
        _secrets = filepath is null ? [] : Load(filepath);
    }

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }

    private static Dictionary<string, string?> Load(string filepath)
    {
        Dictionary<string, string?> secrets = new(StringComparer.Ordinal);

        foreach (string rawLine in File.ReadLines(filepath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (name.Length == 0)
            {
                continue;
            }

            secrets[name] = Unquote(value);
        }

        return secrets;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        char quote = value[0];
        if (quote is not ('"' or '\'' or '`') || value[^1] != quote)
        {
            return value;
        }

        string inner = value[1..^1];
        return quote == '"' ? UnescapeDoubleQuoted(inner) : inner;
    }

    private static string UnescapeDoubleQuoted(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        StringBuilder result = new(value.Length);

        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                result.Append(current);
                continue;
            }

            char next = value[++index];
            switch (next)
            {
                case 'n':
                    result.Append('\n');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 't':
                    result.Append('\t');
                    break;
                case '"':
                    result.Append('"');
                    break;
                case '\\':
                    result.Append('\\');
                    break;
                default:
                    // Unknown escapes keep their backslash: Windows paths such as C:\Users
                    // must survive a double-quoted .env value.
                    result.Append('\\').Append(next);
                    break;
            }
        }

        return result.ToString();
    }
}
