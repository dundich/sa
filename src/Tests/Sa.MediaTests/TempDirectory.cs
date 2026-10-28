namespace Sa.MediaTests;

/// <summary>
/// Временный каталог с уникальным именем: тесты можно гонять параллельно, ничего не делят
/// и не мусорят в выходном каталоге сборки.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sa_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string fileName) => System.IO.Path.Combine(Path, fileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Файлы могут держать антивирус/индексатор — не валим тест из-за уборки.
        }
    }
}
