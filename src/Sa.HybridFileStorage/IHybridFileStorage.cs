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
    /// storages
    /// </summary>
    IReadOnlyCollection<IFileStorage> Storages { get; }

    /// <summary>
    /// Deletes the file associated with the specified file ID asynchronously.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file to be deleted.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>True if the file was successfully deleted; otherwise, false.</returns>
    Task<bool> DeleteAsync(string fileId, string scopeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the file associated with the specified file ID asynchronously.
    /// </summary>
    /// <param name="fileId">The unique identifier for the file to be downloaded.</param>
    /// <param name="loadStream">A function that processes the downloaded file stream.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>True if the file was successfully downloaded; otherwise, false.</returns>
    Task<bool> DownloadAsync(
        string fileId,
        string scopeName,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file asynchronously using the provided input and file stream.
    /// </summary>
    /// <param name="input">Metadata about the file being uploaded.</param>
    /// <param name="fileStream">A stream containing the file data to be uploaded.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation if needed.</param>
    /// <returns>A <see cref="StorageResult"/> containing the result of the upload operation.</returns>
    Task<StorageResult> UploadAsync(
        UploadFileInput input,
        string scopeName,
        Stream fileStream,
        CancellationToken cancellationToken = default);


    Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        string scopeName,
        CancellationToken cancellationToken = default);
}
