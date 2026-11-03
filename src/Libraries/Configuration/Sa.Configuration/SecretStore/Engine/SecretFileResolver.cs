namespace Sa.Configuration.SecretStore.Engine;

/// <summary>
/// Resolves secret file paths against the current working directory first and
/// <see cref="AppContext.BaseDirectory"/> second.
/// </summary>
internal static class SecretFileResolver
{
    public static string? GetFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        string combined = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(combined) ? combined : null;
    }
}
