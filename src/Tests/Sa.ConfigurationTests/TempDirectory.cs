using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// Creates and removes a private directory for file-based secret tests.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sa-configuration-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string content)
    {
        string filePath = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the test result must not depend on cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: the test result must not depend on cleanup.
        }
    }
}
