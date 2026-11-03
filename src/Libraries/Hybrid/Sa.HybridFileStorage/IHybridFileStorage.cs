using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Defines a contract for hybrid file storage systems that support various file operations.
/// This interface allows for the management of files, including uploading, downloading, and deleting.
/// Implementing classes should provide specific storage mechanisms and handle file processing based on the provided file IDs.
/// </summary>
public interface IHybridFileStorage
{
    /// <summary>
    /// Gets the collection of registered file storage providers in registration order —
    /// the default failover chain of every basket.
    /// </summary>
    IEnumerable<IFileStorage> Storages { get; }

    /// <summary>
    /// Uploads a file asynchronously using the provided input and file stream.
    /// </summary>
    /// <param name="input">Metadata about the file being uploaded.</param>
    /// <param name="fileStream">A stream containing the file data to be uploaded.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>A <see cref="StorageResult"/> containing the result of the upload operation.</returns>
    Task<StorageResult> UploadAsync(
        string basket,
        UploadFileInput input,
        Stream fileStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the file associated with the specified file ID asynchronously.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file to be downloaded.</param>
    /// <param name="loadStream">A function that processes the downloaded file stream.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>
    /// <c>true</c> if a storage served the file; <c>false</c> when no candidate had it — the
    /// probing continues after a miss but only within one storage type. Errors fail over to the
    /// next storage of any type and are thrown as <see cref="HybridFileStorageAggregateException"/>
    /// when no storage answered; an empty chain throws <see cref="HybridFileStorageNoAvailableException"/>.
    /// </returns>
    Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Deletes the file associated with the specified file ID asynchronously.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file to be deleted.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>
    /// <c>true</c> if a storage deleted the file; <c>false</c> when no candidate had it — the
    /// same probing and error rules as <see cref="DownloadAsync"/> apply.
    /// </returns>
    Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the metadata for the file associated with the specified file ID.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>The file metadata, or <c>null</c> if the file is not found.</returns>
    Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default);
}
