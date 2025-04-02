namespace Sa.HybridFileStorage.Domain;

/// <summary>
/// Interface for working with file storage.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Gets the type of the storage.
    /// </summary>
    string StorageType { get; }

    /// <summary>
    /// Indicates whether the storage is read-only.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Uploads a file to the storage.
    /// </summary>
    /// <param name="metadata">File metadata.</param>
    /// <param name="fileStream">Stream of the file to upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the file upload.</returns>
    Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if the storage can process the specified file ID.
    /// </summary>
    /// <param name="fileId">File ID.</param>
    /// <returns>True if the file ID is recognized; otherwise, false.</returns>
    bool CanProcessFileId(string fileId);

    /// <summary>
    /// Downloads a file from the storage by its ID.
    /// </summary>
    /// <param name="fileId">File ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file stream.</returns>
    Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a file from the storage by its ID.
    /// </summary>
    /// <param name="fileId">File ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the file was successfully deleted; otherwise, false.</returns>
    Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken);
}