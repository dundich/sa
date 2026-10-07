using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorageTests;

/// <summary>
/// A minimal storage backend with an arbitrary type: records every probe and lets a test observe
/// which storage the chain picked (upload target, download answer, read-only role).
/// </summary>
internal sealed class FakeFileStorage(
    string basket,
    string storageType,
    bool isReadOnly = false,
    bool hasFileOnDownload = false) : IFileStorage
{
    public int UploadCount { get; private set; }

    public int DownloadCount { get; private set; }

    public string Basket => basket;

    public string StorageType => storageType;

    public bool IsReadOnly => isReadOnly;

    // The same loose rule the in-memory provider uses: the file ID prefix is the type —
    // so type "m" also recognizes "mem://..." file IDs and can be a read candidate.
    public bool CanProcess(string fileId)
        => fileId.StartsWith(storageType, StringComparison.Ordinal);

    public Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        UploadCount++;
        string fileId = $"{storageType}://{basket}/{metadata.TenantId}/{metadata.FileName}";
        return Task.FromResult(new StorageResult(fileId, fileId, storageType, DateTimeOffset.UtcNow));
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        DownloadCount++;

        if (!hasFileOnDownload)
        {
            return false;
        }

        await using var data = new MemoryStream([1, 2, 3]);
        await loadStream(data, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
        => Task.FromResult<FileMetadata?>(null);
}
