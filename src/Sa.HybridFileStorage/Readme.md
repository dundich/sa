# IHybridFileStorage Interface

The `IHybridFileStorage` interface defines a contract for hybrid file storage systems that can handle file operations such as uploading, downloading, and deleting files. This interface is designed to be implemented by various storage providers, allowing for flexibility in file storage solutions.


```csharp
public interface IHybridFileStorage
{
    bool IsReadOnly { get; }
    string StorageType { get; }

    bool CanProcessFileId(string fileId);
    Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken);
    Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken);
    Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken);
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

            var result = await storage.UploadFileAsync(new UploadFileInput { FileName = "file.txt" }, stream, cancellationToken);

            string? actual = null;

            var isDownload = await storage.DownloadFileAsync(result.FileId, async (fs, t) => actual = await fs.ToStrAsync(t), cancellationToken);

            Debug.Assert(isDownload);
            Debug.Assert(expected == actual);
        }
    }
}
```