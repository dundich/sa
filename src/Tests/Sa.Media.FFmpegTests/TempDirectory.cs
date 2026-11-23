namespace Sa.Media.FFmpegTests;

/// <summary>
/// Временный каталог с уникальным именем для выходных файлов тестов.
/// <para>
/// Тесты конвертации раньше писали результат прямо в <c>./data</c> — каталог с фикстурами.
/// При двух параллельно работающих классах (xunit.runner.json: <c>maxParallelThreads: 2</c>)
/// это общий изменяемый каталог, а после прогона в нём остаются артефакты, влияющие на
/// следующий запуск (например, <c>isOverwrite: false</c> на уже существующем файле).
/// </para>
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sa_ffmpeg_" + Guid.NewGuid().ToString("N"));
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
