# IHybridFileStorage Interface

The IHybridFileStorage interface enhances the resilience and availability of file data in applications that require reliable storage management.

This interface defines a contract for hybrid file storage systems capable of handling file operations such as uploading, downloading, and deleting files. The integration of multiple storage providers (such as file system, S3, and PostgreSQL) ensures reliable file storage, as the system can automatically switch between different providers in the event that one becomes unavailable.

```csharp
public interface IHybridFileStorage
{
    bool IsReadOnly { get; }
    string StorageType { get; }

    bool CanProcess(string fileId);
    Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken);
    Task<bool> DownloadAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken);
    Task<StorageResult> UploadAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken);
}
```

## Usage Example

```csharp

IHostBuilder builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    services.AddHybridStorage((sp, builder) =>
    {
        builder.AddStorage(new InMemoryFileStorage());
    });
    services.TryAddSingleton<Proccessor>();
});


builder.UseConsoleLifetime();
var host = builder.Build();
await host.Services.GetRequiredService<Proccessor>().Run();

namespace HybridFileStorage.Console
{
    public class Proccessor(IHybridFileStorage storage)
    {
        public async Task Run(CancellationToken cancellationToken = default)
        {
            var expected = "Hello, HybridFileStorage!";
            using var stream = expected.ToStream();

            var result = await storage.UploadAsync(new UploadFileInput { FileName = "file.txt" }, stream, cancellationToken);

            string? actual = null;

            var isDownload = await storage.DownloadAsync(result.FileId, async (fs, t) => actual = await fs.ToStrAsync(t), cancellationToken);

            Debug.Assert(isDownload);
            Debug.Assert(expected == actual);
        }
    }
}
```