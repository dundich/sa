# IHybridFileStorage Interface

The IHybridFileStorage interface enhances the resilience and availability of file data in applications that require reliable storage management.

This interface defines a contract for hybrid file storage systems capable of handling file operations such as uploading, downloading, and deleting files. The integration of multiple storage providers (such as file system, S3, and PostgreSQL) ensures reliable file storage, as the system can automatically switch between different providers in the event that one becomes unavailable.

```csharp

public interface IHybridFileStorage
{
    /// <summary>
    /// storages
    /// </summary>
    IReadOnlyCollection<IFileStorage> Storages { get; }

    /// <summary>
    /// Deletes the file associated with the specified file ID asynchronously.
    /// </summary>
    Task<bool> DeleteAsync(string fileId, string? scopeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the file associated with the specified file ID asynchronously.
    /// </summary>
    Task<bool> DownloadAsync(
        string fileId,
        string? scopeName,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file asynchronously using the provided input and file stream.
    /// </summary>
    Task<StorageResult> UploadAsync(
        UploadFileInput input,
        string? scopeName,
        Stream fileStream,
        CancellationToken cancellationToken = default);
}
```

## Usage Example

```csharp

// di
builder.Services.AddSaHybridStorage((_, b) => b.AddStorage(new InMemoryFileStorage()));

// some test
using var stream = "Hello, HybridFileStorage!".ToStream();

await storage.UploadAsync(
    new UploadFileInput { FileName = "file.txt" },
    stream,
    cancellationToken);

await storage.DownloadAsync(
    result.FileId,
    async (fs, t) => actual = await fs.ToStrAsync(t),
    cancellationToken);

```
